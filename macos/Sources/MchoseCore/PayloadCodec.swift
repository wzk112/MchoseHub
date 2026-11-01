import Foundation

public enum PayloadCodec {
  public static func decode(_ fields: [FieldSpec], response: Data, keyList: [FieldSpec] = []) throws
    -> PayloadSnapshot
  {
    let bytes = Array(response)
    var offset = 2
    var pending: UInt8?
    var result = PayloadSnapshot()
    func take(_ count: Int) throws -> Data {
      guard count >= 0, offset + count <= bytes.count else {
        throw MchoseError.invalid("回包被截断；拒绝补零。")
      }
      defer { offset += count }
      return Data(bytes[offset..<(offset + count)])
    }
    for field in fields where field.n != "command" {
      switch field.k {
      case "u8": result.numbers[field.n] = Int(try take(1).first!)
      case "b4":
        if let byte = pending {
          result.numbers[field.n] = Int(byte & 15)
          pending = nil
        } else {
          let byte = try take(1).first!
          result.numbers[field.n] = Int(byte >> 4)
          pending = byte
        }
      case "arr": result.buffers[field.n] = try take(field.len ?? 0)
      case "keys":
        guard !keyList.isEmpty else { throw MchoseError.invalid("缺少按键布局。") }
        result.tables[field.n] = try keyList.map { try take($0.len ?? 4) }
      default: throw MchoseError.invalid("不支持的读字段：\(field.k)。")
      }
    }
    return result
  }

  public static func encode(
    _ fields: [FieldSpec], _ values: PayloadSnapshot, command: UInt8, tail: Data = Data()
  ) throws -> Data {
    var result = Data([command])
    var pending: Int?
    for field in fields where field.n != "command" {
      switch field.k {
      case "u8", "b4":
        guard let value = values.numbers[field.n],
          (0...(field.k == "b4" ? 15 : 255)).contains(value)
        else {
          throw MchoseError.invalid("\(field.n)：缺少有效数值，拒绝覆盖。")
        }
        if field.k == "u8" {
          result.append(UInt8(value))
        } else if let high = pending {
          result.append(UInt8((high << 4) | value))
          pending = nil
        } else {
          pending = value
        }
      case "arr", "var":
        var buffer = values.buffers[field.n]
        if buffer == nil, let number = values.numbers[field.n], (0...255).contains(number) {
          buffer = Data([UInt8(number)])
        }
        guard let buffer, !buffer.isEmpty, (field.len ?? 0) == 0 || buffer.count == field.len else {
          throw MchoseError.invalid("\(field.n)：缺少完整数组，拒绝覆盖。")
        }
        result.append(buffer)
      case "keys":
        guard let table = values.tables[field.n], !table.isEmpty else {
          throw MchoseError.invalid("缺少按键表。")
        }
        table.forEach { result.append($0) }
      default: throw MchoseError.invalid("未知写字段：\(field.k)。")
      }
    }
    if let pending { result.append(UInt8(pending << 4)) }
    result.append(tail)
    guard result.count <= 519 else { throw MchoseError.invalid("配置超过 519 字节，拒绝截断。") }
    return result
  }
}
