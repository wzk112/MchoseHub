import Foundation

public enum AudioProtocol {
  public static let devices: Set<String> = [
    "291D:385D", "41E4:3402", "41E4:3502", "41E4:3201", "41E4:3202", "3837:6045", "3837:600D",
    "3837:600A", "3837:6011", "3837:6013", "3837:6009", "3837:8080", "0E6A:0111", "3837:60C4",
    "3837:6003", "3837:60C6",
  ]
  public struct Frame: Equatable, Sendable {
    public let flags: UInt8
    public let type: Data
    public let command: UInt8
    public let payload: Data
    public let valid: Bool
  }
  public static func frame(
    type: Data, command: UInt8, payload: Data = Data(), check: Bool = false, length: Int = 63
  ) throws -> Data {
    guard type.count == 2, payload.count <= 254, length >= 8 + payload.count else {
      throw MchoseError.invalid("音频帧类型或长度无效。")
    }
    let flags: UInt8 = check ? 1 : 0
    let n = UInt8(payload.count + 1)
    var data = Data([0xAA, 1, flags, n]) + type + Data([command]) + payload
    data.append(check ? (Data([flags, n]) + type + Data([command]) + payload).reduce(0, ^) : 0)
    data.append(Data(repeating: 0, count: length - data.count))
    return data
  }
  public static func parse(_ data: Data) -> Frame? {
    let b = Array(data)
    guard b.count >= 8, b[0] == 0xAA, b[1] == 1, b[2] <= 1, b[3] >= 1, 6 + Int(b[3]) < b.count
    else { return nil }
    let count = Int(b[3]) - 1
    let sumIndex = 7 + count
    let expected = b[2..<sumIndex].reduce(0, ^)
    return Frame(
      flags: b[2], type: Data(b[4...5]), command: b[6], payload: Data(b[7..<sumIndex]),
      valid: b[2] == 0 || b[sumIndex] == expected)
  }
  public static func legacy(_ header: [UInt8]) throws -> Data {
    guard header.count <= 15 else { throw MchoseError.invalid("第一代音频命令超过 15 字节。") }
    return Data(header) + Data(repeating: 0, count: 15 - header.count)
  }
  public struct LegacyState: Sendable {
    public let eq: Int, voice: Bool, speaker: Bool, mic: Bool, workMode: Int
    public let lightMode: Int, brightness: Int, cycle: Int, colorIndex: Int, groups: Int,
      groupsOn: Int
    public let color: [UInt8]
    public init(data: Data) throws {
      let b = Array(data)
      guard b.count >= 15 else { throw MchoseError.invalid("音频状态回包被截断。") }
      eq = Int(b[2])
      voice = b[1] & 1 != 0
      speaker = b[1] & 2 != 0
      mic = b[1] & 4 != 0
      workMode = Int(b[1] >> 4)
      lightMode = Int(b[3])
      brightness = Int(b[4])
      cycle = Int(b[5])
      colorIndex = Int(b[6])
      groups = Int(b[9])
      groupsOn = Int(b[10])
      color = Array(b[12...14])
    }
  }
}
