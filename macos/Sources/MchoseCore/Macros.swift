import Foundation

public struct KeyboardFunction: Decodable, Sendable, Identifiable {
  public let name: String
  public let label: String
  public let group: String
  public let value: [UInt8]
  public var id: String { name }
}
public struct FunctionCatalog: Decodable, Sendable {
  public let functions: [KeyboardFunction]
  public let macroKeys: [String: UInt8]
  public static func load() throws -> Self {
    try JSONDecoder().decode(
      Self.self,
      from: Data(contentsOf: Bundle.module.url(forResource: "functions", withExtension: "json")!))
  }
}
public enum MacroTrigger: Int, Codable, CaseIterable, Sendable {
  case hold, toggle, once
  public init(from decoder: any Decoder) throws {
    let value = try decoder.singleValueContainer()
    if let number = try? value.decode(Int.self), let trigger = Self(rawValue: number) {
      self = trigger
      return
    }
    let string = try value.decode(String.self).lowercased()
    guard let trigger = Self.allCases.first(where: { String(describing: $0) == string }) else {
      throw MchoseError.invalid("未知宏触发方式。")
    }
    self = trigger
  }
  public func encode(to encoder: any Encoder) throws {
    var value = encoder.singleValueContainer()
    try value.encode(self == .hold ? "Hold" : self == .toggle ? "Toggle" : "Once")
  }
}
public struct MacroStep: Codable, Equatable, Sendable {
  public var key: String
  public var down: Bool
  public var delayMs: Int
  public var mouse: Bool
  public init(key: String, down: Bool, delayMs: Int = 50, mouse: Bool = false) {
    self.key = key
    self.down = down
    self.delayMs = delayMs
    self.mouse = mouse
  }
  enum CodingKeys: String, CodingKey {
    case key = "Key"
    case down = "Down"
    case delayMs = "DelayMs"
    case mouse = "Mouse"
  }
}
public struct KeyboardMacro: Codable, Equatable, Sendable {
  public var name: String
  public var trigger: MacroTrigger
  public var steps: [MacroStep]
  public init(name: String, trigger: MacroTrigger = .once, steps: [MacroStep]) {
    self.name = name
    self.trigger = trigger
    self.steps = steps
  }
  enum CodingKeys: String, CodingKey {
    case name = "Name"
    case trigger = "Trigger"
    case steps = "Steps"
  }
}
public enum MacroCodec {
  public static func events(_ steps: [MacroStep], catalog: FunctionCatalog) throws -> Data {
    guard (1...15000).contains(steps.count) else { throw MchoseError.invalid("宏须包含 1–15000 个事件。") }
    let mouse: [String: UInt8] = ["Left": 1, "Middle": 4, "Right": 2, "Forward": 16, "Back": 8]
    let extended: Set<UInt8> = [
      79, 80, 81, 82, 98, 89, 90, 91, 92, 93, 94, 95, 96, 97, 87, 86, 85, 84, 99, 88, 76, 72, 73,
      74, 75, 77, 78, 70, 71, 83,
    ]
    var output = Data()
    for step in steps {
      guard (0...60000).contains(step.delayMs),
        let code = (step.mouse ? mouse : catalog.macroKeys)[step.key]
      else {
        throw MchoseError.invalid("无效的宏按键或延时：\(step.key)。")
      }
      let type = step.mouse ? 2 : code > 69 && !extended.contains(code) ? 1 : 0
      output.append(contentsOf: [
        UInt8((step.down ? 0 : 0x80) | (type << 4) | (step.delayMs >> 16)),
        UInt8((step.delayMs >> 8) & 255), UInt8(step.delayMs & 255), code,
      ])
    }
    return output
  }
  public static func bank(_ macros: [KeyboardMacro], catalog: FunctionCatalog) throws -> Data {
    guard (1...255).contains(macros.count), Set(macros.map(\.name)).count == macros.count else {
      throw MchoseError.invalid("宏库须有 1–255 个宏，名称不能重复。")
    }
    let entries = try macros.map { macro -> Data in
      let name = Data(macro.name.utf8)
      guard (1...255).contains(name.count) else {
        throw MchoseError.invalid("宏名须为 1–255 个 UTF-8 字节。")
      }
      return Data([UInt8(name.count)]) + name + (try events(macro.steps, catalog: catalog))
    }
    guard macros.count * 4 + entries.reduce(0, { $0 + $1.count }) <= 65535 else {
      throw MchoseError.invalid("宏库超过 65535 字节。")
    }
    var address = macros.count * 4
    var header = Data()
    var contents = Data()
    for entry in entries {
      header.append(contentsOf: [
        UInt8(address & 255), UInt8(address >> 8), UInt8(entry.count & 255),
        UInt8(entry.count >> 8),
      ])
      contents.append(entry)
      address += entry.count
    }
    return header + contents
  }
  public static func pages(_ bank: Data) throws -> [Data] {
    guard (1...65535).contains(bank.count) else { throw MchoseError.invalid("宏库长度无效。") }
    let total = (bank.count + 511) / 512
    return stride(from: 0, to: bank.count, by: 512).enumerated().map { i, start in
      let count = min(512, bank.count - start)
      return Data([5, 0, 0, UInt8(total), UInt8(i), UInt8(count & 255), UInt8(count >> 8)])
        + bank.subdata(in: start..<(start + count))
    }
  }
  public static func binding(_ trigger: MacroTrigger, index: Int) throws -> Data {
    guard (0...254).contains(index) else { throw MchoseError.invalid("宏索引超出范围。") }
    return Data([3, trigger == .hold ? 4 : trigger == .toggle ? 2 : 1, 1, UInt8(index)])
  }
}

public struct MacroRecordingBuffer {
  public private(set) var steps: [MacroStep] = []
  public private(set) var capacityReached = false
  private var held: [String] = []
  private var lastMs = 0
  public init() {}
  @discardableResult public mutating func record(
    key: String, down: Bool, elapsedMs: Int, catalog: FunctionCatalog
  ) throws -> Bool {
    guard catalog.macroKeys[key] != nil, elapsedMs >= lastMs else {
      throw MchoseError.invalid("录制按键或时间无效。")
    }
    if down == held.contains(key) { return false }
    if down && steps.count + held.count + 2 > 15000 {
      capacityReached = true
      return false
    }
    if !steps.isEmpty { steps[steps.count - 1].delayMs = min(60000, elapsedMs - lastMs) }
    steps.append(MacroStep(key: key, down: down, delayMs: 0))
    lastMs = elapsedMs
    if down { held.append(key) } else { held.removeAll { $0 == key } }
    return true
  }
  public mutating func releaseHeld(elapsedMs: Int, catalog: FunctionCatalog) throws {
    for key in held.reversed() {
      try record(key: key, down: false, elapsedMs: elapsedMs, catalog: catalog)
    }
  }
}
