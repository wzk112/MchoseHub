import Foundation

public enum WirelessProtocol {
  public static let reportID: UInt8 = 0x13
  public static let signatures = [
    "03000000021C": "G87", "030000000077": "K99", "0300000001DF": "K99_V2", "030000000132": "G98",
    "0300000000D6": "X75", "0A0000000003": "G98_Pro", "0300000001C6": "X75_V2",
    "030000000115": "G75_Pro", "0A0000000018": "G98_V2", "0A000000002B": "UT98",
    "0A0000000001": "Z75", "0A0000000009": "Z75S", "0300000001C7": "KX75", "030000000174": "GX87",
    "0300000001ED": "GX87_V2", "030000000135": "K87", "030000000335": "G75_V2",
    "0A0000000034": "G87_V2",
  ]
  public static func identify(_ data: Data) throws -> String {
    let signature = data.map { String(format: "%02X", $0) }.joined()
    guard data.count == 6, let model = signatures[signature] else {
      throw MchoseError.invalid("接收器未返回已收录的型号签名。")
    }
    return model
  }
  public static func checksum(_ data: Data) -> UInt8 { data.reduce(reportID, &+) }
  public static func readRequest(command: UInt8, layer: UInt8 = 0) throws -> Data {
    guard layer <= 2 else { throw MchoseError.invalid("无线按键层无效。") }
    var p = Data(repeating: 0, count: 19)
    p[0] = command
    p[1] = 1
    if command == 0x41 { p[3] = layer << 4 }
    if command == 0x43 {
      p[3] = 0x10
      p[4] = 3
      p[5] = 2
    }
    p[18] = checksum(p.prefix(18))
    return p
  }
  public static func writePackets(command: UInt8, data: Data, layer: UInt8 = 0) throws -> [Data] {
    guard (1...3570).contains(data.count), layer <= 2, [1, 2, 3, 4, 6, 9].contains(command) else {
      throw MchoseError.invalid("无线写命令、层或容量无效。")
    }
    let total = (data.count + 13) / 14
    return (0..<total).map { sequence in
      let start = sequence * 14
      let chunk = Array(data[start..<min(start + 14, data.count)])
      var p = Data(repeating: 0, count: 19)
      p[0] = command
      p[1] = UInt8(total)
      p[2] = UInt8(sequence)
      var yy =
        command == 1
        ? 14 + (Int(layer) << 4) : command == 3 ? ((sequence * 14 / 512) << 4) | chunk.count : 14
      if sequence == total - 1 {
        if command == 4 {
          yy = chunk.count
        } else if [1, 6, 9].contains(command) {
          let last = chunk.lastIndex(where: { $0 != 0 })
          yy = last.map { 14 + (command == 1 ? Int(layer) << 4 : 0) - (chunk.count - 1 - $0) } ?? 0
        }
      }
      p[3] = UInt8(yy)
      p.replaceSubrange(4..<(4 + chunk.count), with: chunk)
      p[18] = checksum(p.prefix(18))
      return p
    }
  }
}

public protocol PacketTransport: AnyObject {
  func sendOutput(_ payload: Data) throws
  func nextPacket(timeout: TimeInterval) throws -> Data?
  func clearInbox()
}
public final class WirelessChannel: FeatureTransport {
  public var reportID: UInt8 { 0x13 }
  public var requiresPadding: Bool { false }
  public private(set) var schema: KeyboardSchema?
  private let transport: any PacketTransport
  private let check: () throws -> Void
  private var response = Data()
  public init(transport: any PacketTransport, checkCancellation: @escaping () throws -> Void) {
    self.transport = transport
    check = checkCancellation
  }
  public func resolveModel() throws -> KeyboardSchema {
    let model = try WirelessProtocol.identify(Data(readData(command: 5, minimum: 6).prefix(6)))
    let schema = try KeyboardSchema.load(model: model)
    self.schema = schema
    return schema
  }
  public func sendFeature(payload: Data) throws {
    guard payload.count >= 7, let schema else { throw MchoseError.invalid("无线型号尚未确认。") }
    let cmd = payload[0]
    if cmd >= 0x80 {
      let section = ReadSection.allCases.first { $0.header == payload.prefix(7) }
      let code: UInt8
      let minimum: Int
      if cmd == 0x82 {
        code = payload[1] == 1 ? 5 : 0x43
        minimum = code == 5 ? 6 : 10
      } else {
        guard let section else { throw MchoseError.invalid("未知无线读命令。") }
        code = [
          .battery: 0x4A, .performance: 0x44, .lighting: 0x49, .diy: 0x42, .defaultKeys: 0x41,
          .fnKeys: 0x41, .fn2Keys: 0x41,
        ][section]!
        if section == .battery {
          minimum = 2
        } else if section == .diy {
          minimum = (schema.commands["getDiyLight"]!.filter { $0.n != "space0" }).reduce(0) {
            $0 + ($1.len ?? 0)
          }
        } else if cmd == 0x83 {
          minimum = schema.keyList.reduce(0) { $0 + ($1.len ?? 4) }
        } else {
          minimum = try bodySize(schema.commands[section.command]!)
        }
      }
      let data = try readData(command: code, layer: cmd == 0x83 ? payload[1] : 0, minimum: minimum)
      response = Data(repeating: 0, count: 520)
      response[0] = reportID
      response.replaceSubrange(1..<8, with: payload.prefix(7))
      let offset = code == 0x43 ? 2 : 8
      let count = min(data.count, 520 - offset)
      response.replaceSubrange(offset..<(offset + count), with: data.prefix(count))
    } else {
      guard let code: UInt8 = [3: 1, 4: 4, 6: 2, 10: 9][cmd] else {
        throw MchoseError.invalid("无线宏库须使用独立上传入口。")
      }
      try write(command: code, data: Data(payload.dropFirst(7)), layer: cmd == 3 ? payload[1] : 0)
      response = Data(repeating: 0, count: 520)
    }
  }
  public func getFeature() throws -> Data { response }
  private func bodySize(_ fields: [FieldSpec]) throws -> Int {
    var bits = 0
    for f in fields where f.n != "command" && f.n != "space0" {
      switch f.k {
      case "u8": bits += 8
      case "b4": bits += 4
      case "arr": bits += (f.len ?? 0) * 8
      default: throw MchoseError.invalid("无线 schema 不完整。")
      }
    }
    return (bits + 7) / 8
  }
  private func readData(command: UInt8, layer: UInt8 = 0, minimum: Int) throws -> Data {
    for _ in 0..<3 {
      try check()
      transport.clearInbox()
      try transport.sendOutput(WirelessProtocol.readRequest(command: command, layer: layer))
      var packets: [Int: Data] = [:]
      var total = 0
      let deadline = ProcessInfo.processInfo.systemUptime + 10
      while ProcessInfo.processInfo.systemUptime < deadline {
        try check()
        guard
          let raw = try transport.nextPacket(
            timeout: min(0.2, deadline - ProcessInfo.processInfo.systemUptime))
        else { continue }
        let p = Data(Array(raw))
        guard p.count == 19, p[0] == command else { continue }
        guard p[1] > 0, p[2] < p[1], total == 0 || total == Int(p[1]) else {
          throw MchoseError.invalid("无线分包编号或总数无效。")
        }
        total = Int(p[1])
        let seq = Int(p[2])
        if let old = packets[seq], old != p { throw MchoseError.invalid("同一无线分包内容冲突。") }
        packets[seq] = p
        if packets.count == total {
          let data = (0..<total).reduce(into: Data()) {
            $0.append(packets[$1]!.subdata(in: 4..<18))
          }
          guard data.count >= minimum else { throw MchoseError.invalid("无线回包被截断。") }
          return data
        }
      }
    }
    throw MchoseError.invalid("无线读取三次超时，未收到完整配置。")
  }
  public func write(command: UInt8, data: Data, layer: UInt8 = 0) throws {
    let packets = try WirelessProtocol.writePackets(command: command, data: data, layer: layer)
    WriteThrottle.shared.waitForSlot()
    try check()
    let deadline = ProcessInfo.processInfo.systemUptime + 120
    for p in packets {
      try check()
      guard ProcessInfo.processInfo.systemUptime < deadline else {
        throw MchoseError.invalid("无线写入超过两分钟。")
      }
      // One send per packet; stop on missing ACK instead of repeating a possibly applied write.
      transport.clearInbox()
      try transport.sendOutput(p)
      let ackDeadline = min(deadline, ProcessInfo.processInfo.systemUptime + 5)
      var accepted = false
      while ProcessInfo.processInfo.systemUptime < ackDeadline {
        try check()
        if let ack = try transport.nextPacket(timeout: 0.2), ack.count == 19, ack[0] == command,
          ack[2] == p[2]
        {
          accepted = true
          break
        }
      }
      guard accepted else { throw MchoseError.invalid("无线分包 \(p[2]+1)/\(packets.count) 未确认；已停止。") }
    }
  }
}
