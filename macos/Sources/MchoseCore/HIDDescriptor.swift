import Foundation

/// Report sizes in a HID descriptor exclude report IDs. Counts include constant/padding fields.
public struct HIDDescriptor: Sendable {
  public struct Report: Codable, Equatable, Sendable {
    public let type: String
    public let id: Int
    public let payloadBytes: Int
    public var framedBytes: Int { payloadBytes + (id == 0 ? 0 : 1) }
  }
  public let reports: [Report]
  public init(data: Data) throws {
    struct Globals {
      var size = 0
      var count = 0
      var id = 0
    }
    let bytes = Array(data)
    var cursor = 0
    var globals = Globals()
    var stack: [Globals] = []
    var bits: [String: Int] = [:]
    while cursor < bytes.count {
      let prefix = bytes[cursor]
      cursor += 1
      // Long items are not used for report sizing; still check their bounds.
      if prefix == 0xFE {
        guard cursor + 2 <= bytes.count else { throw MchoseError.invalid("截断的 HID 长条目。") }
        let length = Int(bytes[cursor])
        cursor += 2
        guard cursor + length <= bytes.count else { throw MchoseError.invalid("截断的 HID 长条目。") }
        cursor += length
        continue
      }
      let length = [0, 1, 2, 4][Int(prefix & 3)]
      guard cursor + length <= bytes.count else { throw MchoseError.invalid("截断的 HID 描述符。") }
      var value: UInt32 = 0
      for i in 0..<length { value |= UInt32(bytes[cursor + i]) << (i * 8) }
      cursor += length
      let type = (prefix >> 2) & 3
      let tag = prefix >> 4
      if type == 1 {
        switch tag {
        case 7: globals.size = Int(value)
        case 8:
          guard value > 0, value <= 255 else { throw MchoseError.invalid("HID report ID 无效。") }
          globals.id = Int(value)
        case 9: globals.count = Int(value)
        case 10: stack.append(globals)
        case 11:
          guard let previous = stack.popLast() else { throw MchoseError.invalid("HID 全局栈不匹配。") }
          globals = previous
        default: break
        }
      } else if type == 0, let name = [UInt8(8): "input", 9: "output", 11: "feature"][tag] {
        guard globals.size <= 65536, globals.count <= 65536,
          globals.size * globals.count <= 8_000_000
        else { throw MchoseError.invalid("HID 报告过大。") }
        let key = "\(name):\(globals.id)"
        let total = bits[key, default: 0] + globals.size * globals.count
        guard total <= 8_000_000 else { throw MchoseError.invalid("HID 报告过大。") }
        bits[key] = total
      }
    }
    guard stack.isEmpty else { throw MchoseError.invalid("HID 全局栈未结束。") }
    reports = bits.keys.sorted().map {
      let parts = $0.split(separator: ":")
      return Report(type: String(parts[0]), id: Int(parts[1])!, payloadBytes: (bits[$0]! + 7) / 8)
    }
  }
}
