import Foundation

public struct KeyboardEffect: Sendable, Identifiable {
  public let id: Int
  public let name: String
  public var brightnessDisabled: Bool { [0, 15, 17, 19, 21].contains(id) }
  public var speedDisabled: Bool { [0, 1, 15, 17, 19, 21].contains(id) }
  public var colorDisabled: Bool { [0, 3, 6, 15, 16, 17].contains(id) }
  public static func all(schema: KeyboardSchema) -> [KeyboardEffect] {
    let names = [
      0: "关闭", 1: "常亮模式", 2: "呼吸模式", 3: "梦幻彩虹", 4: "一触即发", 5: "雨中漫步", 6: "彩虹轮盘", 7: "涟漪扩散",
      8: "繁星点点", 10: "川流不息", 11: "随波逐流", 12: "如影随形", 13: "正弦光波", 14: "正弦光波", 15: "旋转风车", 16: "七彩瀑布",
      17: "花开富贵",
    ]
    return names.filter {
      $0.key != (schema.customLightMode == 19 ? 13 : 14)
        && ($0.key != 6 || schema.model == "G75_V2")
    }
    .map { KeyboardEffect(id: $0.key, name: $0.value) }.sorted { $0.id < $1.id } + [
      KeyboardEffect(id: schema.customLightMode, name: "自定义（逐键 RGB）")
    ]
  }
}

extension KeyboardSchema {
  public var macSwitchOn: Int {
    ["G87", "G98", "X75", "G75_Pro", "G75_V2", "KX75", "GX87", "GX87_V2"].contains(model) ? 2 : 1
  }
}
extension KeyboardProfile {
  public mutating func setEffect(_ mode: Int, schema: KeyboardSchema) throws {
    guard KeyboardEffect.all(schema: schema).contains(where: { $0.id == mode }) else {
      throw MchoseError.invalid("此型号不支持该灯效。")
    }
    performance.numbers["lightMode"] = mode
    performance.numbers["lightType"] = mode == schema.customLightMode ? 1 : 0
  }
  public mutating func setLatency(_ mode: Int, schema: KeyboardSchema) throws {
    guard [0, 1, 2].contains(mode), mode != 1 || schema.model == "Z75" else {
      throw MchoseError.invalid("此型号不支持该低延迟选项。")
    }
    if schema.model == "Z75" {
      performance.numbers["latencySwitch"] = [0: 0, 1: 1, 2: 3][mode]
      performance.numbers["mode"] = [0: 0, 1: 3, 2: 10][mode]
    } else {
      performance.numbers["latencySwitch"] =
        mode == 0 ? 0 : performance.numbers["latencySwitch"] == 3 ? 3 : 2
    }
  }
  public mutating func remap(key: String, layer: Int, value: Data, schema: KeyboardSchema) throws {
    guard (0...2).contains(layer), value.count == 4,
      schema.board.contains(where: { $0.key == key }),
      !schema.reservedKeys.contains(key), let i = schema.keyList.firstIndex(where: { $0.n == key }),
      !(layer > 0 && value.first == 13)
    else { throw MchoseError.invalid("无效的实体键、层或映射；Fn 层不支持层切换功能。") }
    keyLayers[layer].tables["#keyList"]?[i] = value
  }
  public mutating func setColor(
    _ rgb: [UInt8], key: String? = nil, fill: Bool = false, schema: KeyboardSchema
  ) throws {
    guard rgb.count == 3 else { throw MchoseError.invalid("RGB 颜色无效。") }
    if fill || key != nil {
      let indexes: [Int]
      if fill {
        indexes = Array(0..<(diy.buffers["rTable"]?.count ?? 0))
      } else if let index = schema.keyList.firstIndex(where: { $0.n == key }) {
        indexes = [index]
      } else {
        throw MchoseError.invalid("此键没有 LED 索引。")
      }
      for (channel, name) in ["rTable", "gTable", "bTable"].enumerated() {
        for index in indexes { diy.buffers[name]?[index] = rgb[channel] }
      }
    } else {
      let name = "lightModeColor\(performance.numbers["lightMode",default:0])"
      guard lighting.buffers[name]?.count == 3 else { throw MchoseError.invalid("此灯效不支持单独颜色。") }
      lighting.buffers[name] = Data(rgb)
    }
  }
  public mutating func restoreFactory(schema: KeyboardSchema) throws {
    let defaults = try FactoryValues.load(model: model)
    func merge(_ snapshot: inout PayloadSnapshot, _ name: String) {
      for (key, value) in defaults[name, default: [:]] {
        switch value {
        case .number(let v): if snapshot.numbers[key] != nil { snapshot.numbers[key] = v }
        case .bytes(let v): if snapshot.buffers[key] != nil { snapshot.buffers[key] = Data(v) }
        }
      }
    }
    merge(&performance, "performance")
    merge(&lighting, "lighting")
    merge(&diy, "diy")
    for layer in 0...2 {
      for (i, key) in schema.keyList.enumerated()
      where schema.board.contains(where: { $0.key == key.n })
        && !schema.reservedKeys.contains(key.n)
      { keyLayers[layer].tables["#keyList"]?[i] = Data(repeating: 0, count: 4) }
    }
    try validate(schema: schema)
  }
  public mutating func repairLighting(schema: KeyboardSchema) throws {
    guard model == "K87" else { throw MchoseError.invalid("只有 K87 收录了可验证的灯效参数修复规则。") }
    let current = performance.numbers["lightMode", default: 0]
    var factory = self
    try factory.restoreFactory(schema: schema)
    for name in ["space2"] { performance.numbers[name] = factory.performance.numbers[name] }
    for i in 3...11 {
      let key = "space\(i)"
      if let bytes = factory.performance.buffers[key] { performance.buffers[key] = bytes }
    }
    for slot in [1, 2, 3, 4, 5, 7, 8, 10, 11, 12, 13, 15, 16, 17] {
      if slot != current { performance.numbers["brightness\(slot)"] = 3 }
      performance.numbers["speed\(slot)"] = 3
      performance.numbers["multiColor\(slot)"] = 0
    }
    let brightness = "brightness\(current)"
    if let value = performance.numbers[brightness], (1...10).contains(value) {
      performance.numbers[brightness] = 100
    }
    try validate(schema: schema)
  }
}

enum FactoryValue: Decodable {
  case number(Int)
  case bytes([UInt8])
  init(from decoder: any Decoder) throws {
    let c = try decoder.singleValueContainer()
    if let number = try? c.decode(Int.self) {
      self = .number(number)
    } else {
      self = .bytes(try c.decode([UInt8].self))
    }
  }
}
enum FactoryValues {
  struct Resource: Decodable { let models: [String: [String: [String: FactoryValue]]] }
  static func load(model: String) throws -> [String: [String: FactoryValue]] {
    let resource = try JSONDecoder().decode(
      Resource.self,
      from: Data(contentsOf: Bundle.module.url(forResource: "factory", withExtension: "json")!))
    guard let values = resource.models[model] else { throw MchoseError.invalid("未收录出厂配置。") }
    return values
  }
}
