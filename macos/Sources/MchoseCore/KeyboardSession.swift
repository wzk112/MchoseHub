import Foundation

public protocol FeatureTransport: AnyObject {
  var reportID: UInt8 { get }
  var requiresPadding: Bool { get }
  /// Payload excludes ID. The platform boundary adds it exactly once.
  func sendFeature(payload: Data) throws
  /// Normalized return includes ID and must preserve the actual native length.
  func getFeature() throws -> Data
}
extension FeatureTransport {
  public var reportID: UInt8 { 6 }
  public var requiresPadding: Bool { true }
}

public struct RawExchange: Codable, Sendable {
  public let section: String
  public let request: Data
  public let response: Data
}

public struct DeviceBackup: Sendable {
  public let profile: KeyboardProfile
  public let battery: Int?
  public let exchanges: [RawExchange]
}

/// All operations are synchronous and must be confined to a background serial queue.
/// No unbounded retries, no configuration writes during read/backup, no cached settings after failure.
public final class KeyboardSession {
  private let transport: any FeatureTransport
  private let schema: K87Schema
  private let checkCancellation: () throws -> Void
  private let sleep: (TimeInterval) -> Void
  private let clock: () -> TimeInterval
  private var lastWrite: TimeInterval?
  public private(set) var exchanges: [RawExchange] = []

  public init(
    transport: any FeatureTransport, schema: K87Schema,
    checkCancellation: @escaping () throws -> Void = {},
    sleep: @escaping (TimeInterval) -> Void = { Thread.sleep(forTimeInterval: $0) },
    clock: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }
  ) {
    self.transport = transport
    self.schema = schema
    self.checkCancellation = checkCancellation
    self.sleep = sleep
    self.clock = clock
  }

  public func read(_ section: ReadSection) throws -> Data {
    // Exactly one known request per transaction. Failure returns to the caller, never probes IDs.
    try checkCancellation()
    try transport.sendFeature(payload: section.payload)
    try checkCancellation()
    let response = try transport.getFeature()
    exchanges.append(
      RawExchange(section: section.rawValue, request: section.payload, response: response))
    try checkCancellation()
    try WiredFrame.validate(response, for: section, reportID: transport.reportID)
    return response
  }

  public func stableRead(_ section: ReadSection) throws -> Data {
    var previous = try read(section)
    for _ in 1..<4 {
      sleep(0.04)
      let next = try read(section)
      if previous.dropFirst(2) == next.dropFirst(2) { return next }
      previous = next
    }
    throw MchoseError.invalid("\(section.rawValue)：四次读取没有连续两份一致的回包。")
  }

  private func snapshot(_ section: ReadSection) throws -> PayloadSnapshot {
    try PayloadCodec.decode(
      schema.commands[section.command]!, response: stableRead(section), keyList: schema.keyList)
  }

  private func profile() throws -> KeyboardProfile {
    let p = try snapshot(.performance)
    let l = try snapshot(.lighting)
    let d = try snapshot(.diy)
    let keys = try [ReadSection.defaultKeys, .fnKeys, .fn2Keys].map(snapshot)
    let profile = KeyboardProfile(
      performance: p, lighting: l, diy: d, keyLayers: keys, model: schema.model)
    try profile.validate(schema: schema)
    return profile
  }

  public func backup() throws -> DeviceBackup {
    exchanges.removeAll()
    let batteryByte = try read(.battery)[8]
    let first = try profile()
    let second = try profile()
    let differences = try first.differences(from: second, schema: schema)
    guard differences.isEmpty else {
      throw MchoseError.invalid("两次完整读取不一致，未建立备份基线：\(differences.joined(separator: ", "))")
    }
    return DeviceBackup(
      profile: second, battery: batteryByte <= 100 ? Int(batteryByte) : nil, exchanges: exchanges)
  }

  /// Experimental hardware path; callers must persist a fresh backup before invoking it.
  /// One write per section, at least 1.5 s apart. Stop on first failure, no automatic full retry.
  public func restore(_ target: KeyboardProfile, sections: Set<ReadSection>? = nil) throws {
    try target.validate(schema: schema)
    let plan = try target.compileWrites(schema: schema).filter {
      sections == nil || sections!.contains($0.section)
    }
    for write in plan {
      try checkCancellation()
      if let lastWrite { sleep(max(0, 1.5 - (clock() - lastWrite))) }
      try checkCancellation()
      lastWrite = clock()
      try transport.sendFeature(
        payload: transport.requiresPadding
          ? write.payload + Data(repeating: 0, count: 519 - write.payload.count) : write.payload)
      // Drain the native response, but do not invent ACK semantics. Verify by a stable read.
      _ = try transport.getFeature()
      sleep(0.25)
      let actual = try snapshot(write.section)
      let expected: PayloadSnapshot
      switch write.section {
      case .lighting: expected = target.lighting
      case .diy: expected = target.diy
      case .performance: expected = target.performance
      case .defaultKeys: expected = target.keyLayers[0]
      case .fnKeys: expected = target.keyLayers[1]
      case .fn2Keys: expected = target.keyLayers[2]
      case .battery: throw MchoseError.invalid("电量不能写入。")
      }
      let differences = KeyboardProfile.differences(expected, actual, path: write.section.rawValue)
      guard differences.isEmpty else {
        throw MchoseError.invalid("写入回读不符，已停止，未重写：\(differences.joined(separator: ", "))")
      }
    }
    let differences = try target.differences(from: profile(), schema: schema)
    guard differences.isEmpty else {
      throw MchoseError.invalid("完整恢复回读不符：\(differences.joined(separator: ", "))")
    }
  }
  public func uploadMacros(_ macros: [KeyboardMacro], catalog: FunctionCatalog) throws {
    let bank = try MacroCodec.bank(macros, catalog: catalog)
    if let wireless = transport as? WirelessChannel {
      if let lastWrite { sleep(max(0, 1.5 - (clock() - lastWrite))) }
      try checkCancellation()
      lastWrite = clock()
      try wireless.write(command: 3, data: bank)
    } else {
      for page in try MacroCodec.pages(bank) {
        if let lastWrite { sleep(max(0, 1.5 - (clock() - lastWrite))) }
        try checkCancellation()
        lastWrite = clock()
        try transport.sendFeature(payload: page + Data(repeating: 0, count: 519 - page.count))
        _ = try transport.getFeature()
      }
    }
    sleep(0.25)
    let header = Data([0x82, 0, 0, 1, 0, 4, 0, 0, 4, 0, 0])
    let payload = header + Data(repeating: 0, count: 519 - header.count)
    func directory() throws -> Data {
      try checkCancellation()
      try transport.sendFeature(payload: payload)
      let r = try transport.getFeature()
      guard r.count == 520, r.first == transport.reportID else {
        throw MchoseError.invalid("宏目录回包被截断。")
      }
      return r
    }
    var previous = try directory()
    var stable: Data?
    for _ in 1..<4 {
      sleep(0.04)
      let next = try directory()
      if previous.dropFirst(2) == next.dropFirst(2) {
        stable = next
        break
      }
      previous = next
    }
    guard let stable, stable.subdata(in: 8..<12) == bank.prefix(4) else {
      throw MchoseError.invalid("宏库首个目录项回读不一致；未绑定新宏。")
    }
  }
  public func diagnostic(_ name: String) throws -> Data {
    if let section = ReadSection(rawValue: name) { return try read(section) }
    let header: Data
    if name == "macro" {
      header = Data([0x82, 0, 0, 1, 0, 4, 0, 0, 4, 0, 0])
    } else if name == "password" {
      header = Data([0x82, 1, 0, 1, 0, 6, 0])
    } else {
      throw MchoseError.invalid("未收录的诊断读取。")
    }
    let result = try raw(header)
    guard result.count == 520, result.first == transport.reportID else {
      throw MchoseError.invalid("诊断回包长度或 ID 无效。")
    }
    return result
  }
  public func raw(_ data: Data) throws -> Data {
    guard (1...519).contains(data.count) else {
      throw MchoseError.invalid("手工 payload 应为 1–519 字节。")
    }
    try checkCancellation()
    if data.first! < 0x80 {
      if let lastWrite { sleep(max(0, 1.5 - (clock() - lastWrite))) }
      lastWrite = clock()
    }
    try transport.sendFeature(
      payload: transport.requiresPadding ? data + Data(repeating: 0, count: 519 - data.count) : data
    )
    return try transport.getFeature()
  }
}
