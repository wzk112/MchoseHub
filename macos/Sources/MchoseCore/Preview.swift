import Foundation

public enum PreviewProfile {
  /// Standalone in-memory data. No transport, no cached device values, no automatic persistence.
  public static func make(schema: K87Schema) throws -> KeyboardProfile {
    func snapshot(_ section: ReadSection) -> PayloadSnapshot {
      var result = PayloadSnapshot()
      for field in schema.commands[section.command]! {
        switch field.k {
        case "u8", "b4":
          result.numbers[field.n] =
            field.n == "rateVal"
            ? 3 : field.n == "lightMode" ? 1 : field.n.hasPrefix("brightness") ? 100 : 0
        case "arr":
          result.buffers[field.n] =
            field.n == "space0"
            ? Data(section.header.dropFirst()) : Data(repeating: 0, count: field.len ?? 0)
        case "keys":
          result.tables[field.n] = schema.keyList.map { Data(repeating: 0, count: $0.len ?? 4) }
        default: break
        }
      }
      return result
    }
    let p = KeyboardProfile(
      performance: snapshot(.performance), lighting: snapshot(.lighting), diy: snapshot(.diy),
      keyLayers: [.defaultKeys, .fnKeys, .fn2Keys].map(snapshot), model: schema.model)
    try p.validate(schema: schema)
    return p
  }
}
