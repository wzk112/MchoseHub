import Foundation

/// Data and [Data] deliberately use Codable's base64 strings, matching System.Text.Json byte[].
public struct PayloadSnapshot: Codable, Equatable, Sendable {
  public var numbers: [String: Int] = [:]
  public var buffers: [String: Data] = [:]
  public var tables: [String: [Data]] = [:]
  public init() {}
  enum CodingKeys: String, CodingKey {
    case numbers = "Numbers"
    case buffers = "Buffers"
    case tables = "Tables"
  }
}

public struct KeyboardProfile: Codable, Equatable, Sendable {
  public var version: Int
  public var model: String
  public var performance: PayloadSnapshot
  public var lighting: PayloadSnapshot
  public var diy: PayloadSnapshot
  public var keyLayers: [PayloadSnapshot]
  public init(
    performance: PayloadSnapshot, lighting: PayloadSnapshot, diy: PayloadSnapshot,
    keyLayers: [PayloadSnapshot], model: String = "K87"
  ) {
    version = 1
    self.model = model
    self.performance = performance
    self.lighting = lighting
    self.diy = diy
    self.keyLayers = keyLayers
  }
  enum CodingKeys: String, CodingKey {
    case version = "Version"
    case model = "Model"
    case performance = "Performance"
    case lighting = "Lighting"
    case diy = "Diy"
    case keyLayers = "KeyLayers"
  }
  public static func read(from url: URL, schema: K87Schema) throws -> KeyboardProfile {
    let data = try Data(contentsOf: url, options: .mappedIfSafe)
    guard data.count <= 2_000_000 else { throw MchoseError.invalid("配置文件超过 2 MB。") }
    let profile = try JSONDecoder().decode(Self.self, from: data)
    try profile.validate(schema: schema)
    return profile
  }
  public func save(to url: URL, schema: K87Schema) throws {
    try validate(schema: schema)
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
    let data = try encoder.encode(self)
    guard data.count <= 2_000_000 else { throw MchoseError.invalid("配置文件超过 2 MB。") }
    try data.write(to: url, options: .atomic)
    try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
  }
  public static func read(from url: URL) throws -> KeyboardProfile {
    let data = try Data(contentsOf: url, options: .mappedIfSafe)
    guard data.count <= 2_000_000 else { throw MchoseError.invalid("配置文件超过 2 MB。") }
    let profile = try JSONDecoder().decode(Self.self, from: data)
    try profile.validate(schema: KeyboardSchema.load(model: profile.model))
    return profile
  }

  public func validate(schema: K87Schema) throws {
    guard version == 1, model == schema.model, keyLayers.count == 3 else {
      throw MchoseError.invalid("配置版本、型号或三层按键数量不匹配。")
    }
    let entries: [(ReadSection, PayloadSnapshot)] = [
      (.performance, performance), (.lighting, lighting),
      (.diy, diy), (.defaultKeys, keyLayers[0]), (.fnKeys, keyLayers[1]), (.fn2Keys, keyLayers[2]),
    ]
    for (section, snapshot) in entries {
      let fields = schema.commands[section.command]!.filter { $0.n != "command" }
      let names =
        Array(snapshot.numbers.keys) + Array(snapshot.buffers.keys) + Array(snapshot.tables.keys)
      guard names.count == fields.count, Set(names.map { $0.lowercased() }).count == names.count,
        Set(names) == Set(fields.map(\.n))
      else {
        throw MchoseError.invalid("\(section.rawValue)：缺失、重复或未知字段。")
      }
      for field in fields {
        let valid: Bool
        switch field.k {
        case "u8", "b4":
          valid =
            snapshot.numbers[field.n].map { (0...(field.k == "b4" ? 15 : 255)).contains($0) }
            ?? false
        case "arr": valid = snapshot.buffers[field.n]?.count == field.len
        case "keys":
          valid =
            snapshot.tables[field.n].map {
              $0.count == schema.keyList.count && $0.allSatisfy { $0.count == 4 }
            } ?? false
        default: valid = false
        }
        guard valid else { throw MchoseError.invalid("\(section.rawValue).\(field.n)：类型、范围或长度无效。") }
      }
      guard snapshot.buffers["space0"] == section.header.dropFirst() else {
        throw MchoseError.invalid("\(section.rawValue)：协议头无效。")
      }
    }
    let p = performance.numbers
    guard p["rateVal"] == nil || (1...3).contains(p["rateVal"]!),
      p["latencySwitch", default: 0] <= 3,
      p["winKeySwitch", default: 0] <= 1, p["macSwitch", default: 0] <= 3,
      p["lightMode", default: 0] <= 21, p["lightType", default: 0] <= 1,
      p.filter({ $0.key.hasPrefix("brightness") }).values.allSatisfy({ $0 <= 100 })
    else {
      throw MchoseError.invalid("备份中的性能参数无效。")
    }
    _ = try compileWrites(schema: schema)
  }

  public func compileWrites(schema: K87Schema) throws -> [ProfileWrite] {
    var tail = Data(repeating: 0, count: KeyboardSchema.groupB.contains(model) ? 29 : 22)
    tail[tail.count - 6] = 0x5A
    tail[tail.count - 5] = 0xA5
    var diyWrite = diy
    diyWrite.buffers["pre"] = schema.diyPre
    var result = [
      ProfileWrite(
        section: .lighting,
        payload: try PayloadCodec.encode(
          schema.commands["setLightColor"]!, lighting, command: 10, tail: tail)),
      ProfileWrite(
        section: .diy,
        payload: try PayloadCodec.encode(schema.commands["setDiyLight"]!, diyWrite, command: 6)),
    ]
    guard keyLayers.count == 3 else { throw MchoseError.invalid("缺少三层按键表。") }
    for (i, layer) in keyLayers.enumerated() {
      var expanded = layer
      guard let table = layer.tables["#keyList"], table.count == schema.keyList.count else {
        throw MchoseError.invalid("按键表长度无效。")
      }
      expanded.buffers.merge(schema.keyWriteCommon) { _, common in common }
      for (key, value) in zip(schema.keyList, table) { expanded.buffers[key.n] = value }
      result.append(
        ProfileWrite(
          section: [.defaultKeys, .fnKeys, .fn2Keys][i],
          payload: try PayloadCodec.encode(schema.commands["setKeySetting"]!, expanded, command: 3))
      )
    }
    result.append(
      ProfileWrite(
        section: .performance,
        payload: try PayloadCodec.encode(
          schema.commands["setPerformance"]!, performance, command: 4)))
    return result
  }

  public func differences(from other: KeyboardProfile, schema: K87Schema) throws -> [String] {
    try validate(schema: schema)
    try other.validate(schema: schema)
    return Self.differences(performance, other.performance, path: "Performance")
      + Self.differences(lighting, other.lighting, path: "Lighting")
      + Self.differences(diy, other.diy, path: "Diy")
      + zip(keyLayers, other.keyLayers).enumerated().flatMap {
        Self.differences($0.element.0, $0.element.1, path: ["Default", "Fn", "Fn2"][$0.offset])
      }
  }

  public static func differences(_ a: PayloadSnapshot, _ b: PayloadSnapshot, path: String)
    -> [String]
  {
    var result: [String] = []
    for name in Set(a.numbers.keys).union(b.numbers.keys).sorted()
    where a.numbers[name] != b.numbers[name] {
      result.append("\(path).\(name)")
    }
    // space0 is excluded only after the complete profiles have passed header validation.
    for name in Set(a.buffers.keys).union(b.buffers.keys).sorted()
    where name != "space0" && a.buffers[name] != b.buffers[name] {
      result.append("\(path).\(name)")
    }
    for name in Set(a.tables.keys).union(b.tables.keys).sorted() {
      let lhs = a.tables[name, default: []]
      let rhs = b.tables[name, default: []]
      if lhs.count != rhs.count {
        result.append("\(path).\(name).Count")
        continue
      }
      for i in lhs.indices where lhs[i] != rhs[i] { result.append("\(path).\(name)[\(i)]") }
    }
    return result
  }
}

public struct ProfileWrite: Sendable {
  public let section: ReadSection
  public let payload: Data
}
