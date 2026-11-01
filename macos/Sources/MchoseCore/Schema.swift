import Foundation

public enum MchoseError: Error, LocalizedError, Equatable {
  case invalid(String)
  public var errorDescription: String? {
    switch self {
    case .invalid(let message): message
    }
  }
}

public struct FieldSpec: Decodable, Sendable {
  public let n: String
  public let k: String
  public let len: Int?
}

public struct BoardKey: Decodable, Sendable, Identifiable {
  public let name: String
  public let key: String
  public let width: Int
  public let top: Int
  public let left: Int
  public let height: Int
  public let right: Int
  public let def: [UInt8]
  public let fn: [UInt8]
  public let fn2: [UInt8]
  public var id: String { key }
}

public typealias K87Schema = KeyboardSchema
public struct KeyboardSchema: Sendable {
  public let model: String
  public let commands: [String: [FieldSpec]]
  public let board: [BoardKey]
  public let diyPre: Data
  public let keyWriteCommon: [String: Data]
  public let reservedKeys: [String]
  public var keyList: [FieldSpec] { commands["keyList"]! }
  public var customLightMode: Int { Self.groupB.contains(model) ? 19 : 21 }
  public var reportID: UInt8 { Self.groupB.contains(model) ? 9 : 6 }
  public static let groupB: Set<String> = ["G98_Pro", "G98_V2", "G87_V2", "UT98", "Z75", "Z75S"]

  private struct Resource: Decodable, Sendable {
    struct Layout: Decodable, Sendable {
      let board: [BoardKey]
      let diyPre: [UInt8]
      let keyWriteCommon: [String: [UInt8]]
      let reservedKeys: [String]
    }
    let models: [String: [String: [FieldSpec]]]
    let layouts: [String: Layout]
  }
  private static let resource: Result<Resource, any Error> = Result {
    guard let url = Bundle.module.url(forResource: "models", withExtension: "json") else {
      throw MchoseError.invalid("缺少键盘协议资源。")
    }
    return try JSONDecoder().decode(Resource.self, from: Data(contentsOf: url))
  }

  public static func load(model: String = "K87") throws -> KeyboardSchema {
    let root = try resource.get()
    guard var commands = root.models[model], let layout = root.layouts[model] else {
      throw MchoseError.invalid("未收录的键盘型号：\(model)。")
    }
    // Same correction as KeyboardModelSchemas.NormalizeNibbles in Core.
    if var fields = commands["getPerformance"] {
      for i in 0..<(fields.count - 1)
      where fields[i].k == "b4" && fields[i + 1].k == "b4"
        && fields[i].n.hasPrefix("multiColor") && fields[i + 1].n.hasPrefix("speed")
      {
        fields.swapAt(i, i + 1)
      }
      commands["getPerformance"] = fields
    }
    return KeyboardSchema(
      model: model, commands: commands, board: layout.board, diyPre: Data(layout.diyPre),
      keyWriteCommon: layout.keyWriteCommon.mapValues(Data.init), reservedKeys: layout.reservedKeys)
  }
  public static func availableModels() throws -> [String] {
    let root = try resource.get()
    return root.layouts.keys.sorted()
  }
  public static func resolve(product: String) throws -> String {
    var normalized = product.uppercased().map { $0.isLetter || $0.isNumber ? String($0) : "_" }
      .joined()
    normalized = normalized.replacingOccurrences(of: "Z75_S", with: "Z75S")
    for model in try availableModels().sorted(by: { $0.count > $1.count }) {
      if normalized.range(of: "(?:^|_)\(model.uppercased())(?:_|$)", options: .regularExpression)
        != nil
      {
        return model
      }
    }
    throw MchoseError.invalid("未收录的键盘产品名：\(product)。")
  }
}

public enum ReadSection: String, CaseIterable, Sendable {
  case battery, performance, lighting, diy, defaultKeys, fnKeys, fn2Keys
  public var command: String {
    switch self {
    case .battery: "getBattery"
    case .performance: "getPerformance"
    case .lighting: "getLightColor"
    case .diy: "getDiyLight"
    case .defaultKeys, .fnKeys, .fn2Keys: "getKeySetting"
    }
  }
  public var header: Data {
    switch self {
    case .battery: Data([0x87, 0, 0, 1, 0, 2, 0])
    case .performance: Data([0x84, 0, 0, 1, 0, 128, 0])
    case .lighting: Data([0x8A, 0, 0, 1, 0, 0, 2])
    case .diy: Data([0x86, 0, 0, 1, 0, 122, 1])
    case .defaultKeys: Data([0x83, 0, 0, 1, 0, 248, 1])
    case .fnKeys: Data([0x83, 1, 0, 1, 0, 248, 1])
    case .fn2Keys: Data([0x83, 2, 0, 1, 0, 248, 1])
    }
  }
  public var payload: Data { header + Data(repeating: 0, count: 519 - header.count) }
}

public enum WiredFrame {
  public static let reportID: UInt8 = 6
  public static func sendBuffer(payload: Data, reportID: UInt8 = 6) throws -> Data {
    guard payload.count == 519 else { throw MchoseError.invalid("有线 payload 必须是 519 字节。") }
    return Data([reportID]) + payload
  }
  public static func validate(_ response: Data, for section: ReadSection, reportID: UInt8 = 6)
    throws
  {
    guard response.count == 520, response.first == reportID,
      response.dropFirst().prefix(7) == section.header
    else {
      throw MchoseError.invalid("\(section.rawValue)：报告长度、ID 或七字节回显不符（\(response.count) 字节）。")
    }
    guard response.dropFirst(8).prefix(4) != section.header.prefix(4) else {
      throw MchoseError.invalid("\(section.rawValue)：拒绝残留的请求缓冲区。")
    }
  }
}
