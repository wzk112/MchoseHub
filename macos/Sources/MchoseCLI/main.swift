import Foundation
import MchoseCore
import MchoseHID

@main struct MchoseCLI {
  static func main() async {
    do { try await run(Array(CommandLine.arguments.dropFirst())) } catch {
      FileHandle.standardError.write(Data("错误：\(error.localizedDescription)\n".utf8))
      exit(1)
    }
  }
  static func run(_ args: [String]) async throws {
    let service = ConfigurationService()
    guard let command = args.first, command != "--help" else {
      print(help)
      return
    }
    var options: [String: String] = [:]
    var flags = Set<String>()
    var index = 1
    while index < args.count {
      let name = args[index]
      guard name.hasPrefix("--"), options[name] == nil, !flags.contains(name) else {
        throw MchoseError.invalid("重复或无效参数：\(name)。")
      }
      if ["--all", "--experimental-writes"].contains(name) {
        flags.insert(name)
        index += 1
      } else {
        guard index + 1 < args.count, !args[index + 1].hasPrefix("--") else {
          throw MchoseError.invalid("\(name) 缺少参数。")
        }
        options[name] = args[index + 1]
        index += 2
      }
    }
    let allowed: [String: Set<String>] = [
      "list": ["--all"], "watch": ["--seconds"],
      "backup": ["--device", "--out"], "validate": ["--profile"],
      "roundtrip": ["--profile", "--out"], "compare": ["--expected", "--actual"],
      "restore": ["--device", "--profile", "--backup", "--experimental-writes"],
      "verify-k87-writes": ["--device", "--out", "--experimental-writes"],
    ]
    guard let accepted = allowed[command], Set(options.keys).union(flags).isSubset(of: accepted)
    else {
      throw MchoseError.invalid("未知命令或选项。\n\(help)")
    }
    func required(_ key: String) throws -> String {
      guard let value = options[key], !value.isEmpty else {
        throw MchoseError.invalid("缺少 \(key)。")
      }
      return value
    }
    func url(_ key: String) throws -> URL { URL(fileURLWithPath: try required(key)) }
    func output(_ key: String) throws -> URL {
      let value = try url(key)
      guard !FileManager.default.fileExists(atPath: value.path) else {
        throw MchoseError.invalid("输出已存在：\(value.path)")
      }
      return value
    }
    func deviceID() throws -> UInt64 {
      guard let id = UInt64(try required("--device")) else {
        throw MchoseError.invalid("设备 ID 应为 list 给出的十进制 registryID。")
      }
      return id
    }
    switch command {
    case "list":
      FileHandle.standardError.write(Data("输入报告访问：\(MacHID.inputAccessDescription)\n".utf8))
      let devices = try await service.scan().filter { flags.contains("--all") || $0.isMchose }
      let encoder = JSONEncoder()
      encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
      print(String(decoding: try encoder.encode(devices), as: UTF8.self))
    case "watch":
      guard let seconds = Double(options["--seconds"] ?? "15"), seconds > 0, seconds <= 3600 else {
        throw MchoseError.invalid("--seconds 应为 0–3600 秒。")
      }
      let end = Date().addingTimeInterval(seconds)
      var previous: [HIDDeviceInfo] = []
      repeat {
        let current = try await service.scan().filter(\.isMchose)
        for device in current where !previous.contains(where: { $0.id == device.id }) {
          print("已连接 \(device.id) \(device.product) 配置接口：\(device.isSupported)")
        }
        for device in previous where !current.contains(where: { $0.id == device.id }) {
          print("已拔出 \(device.id) \(device.product)")
        }
        previous = current
        try await Task.sleep(for: .milliseconds(500))
      } while Date() < end
    case "validate":
      let p = try KeyboardProfile.read(from: url("--profile"))
      print("有效：\(p.model) v\(p.version)，三层按键，六条完整恢复报文；不含完整宏库。")
    case "roundtrip":
      let p = try KeyboardProfile.read(from: url("--profile"))
      try p.save(to: output("--out"), schema: KeyboardSchema.load(model: p.model))
      print("已完成兼容 JSON 往返。")
    case "compare":
      let a = try KeyboardProfile.read(from: url("--expected"))
      let b = try KeyboardProfile.read(from: url("--actual"))
      let diff = try a.differences(from: b, schema: KeyboardSchema.load(model: a.model))
      guard diff.isEmpty else { throw MchoseError.invalid(diff.joined(separator: "\n")) }
      print("全部已解析配置字段一致。")
    case "backup":
      let destination = try output("--out")
      let id = try deviceID()
      let devices = try await service.scan()
      guard let info = devices.first(where: { $0.id == id && $0.isSupported }) else {
        throw MchoseError.invalid("找不到已确认的 K87 USB 配置集合。")
      }
      try FileManager.default.createDirectory(
        at: destination, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
      let encoder = JSONEncoder()
      encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
      try encoder.encode(info).write(
        to: destination.appendingPathComponent("device.json"), options: .atomic)
      let result = try await service.backup(
        deviceID: id, token: CancellationToken(),
        diagnosticURL: destination.appendingPathComponent("failed-reports.json"))
      try result.profile.save(
        to: destination.appendingPathComponent("profile.json"),
        schema: KeyboardSchema.load(model: result.profile.model))
      try encoder.encode(result.exchanges).write(
        to: destination.appendingPathComponent("reports.json"), options: .atomic)
      try Data(
        "macOS \(ProcessInfo.processInfo.operatingSystemVersionString)\nHardware: \(hardwareModel())\nConnection: \(info.isWireless ? "2.4G receiver over USB" : "USB wired")\nBattery: \(result.battery.map(String.init) ?? "unknown")\n"
          .utf8
      )
      .write(to: destination.appendingPathComponent("system.txt"), options: .atomic)
      print("两次完整读取一致，已保存备份和原始报告：\(destination.path)")
    case "verify-k87-writes":
      guard flags.contains("--experimental-writes") else {
        throw MchoseError.invalid("可恢复实测须显式启用 --experimental-writes。")
      }
      let directory = try output("--out")
      try FileManager.default.createDirectory(
        at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
      let results = try await service.reversibleK87Tests(
        deviceID: deviceID(), directory: directory, token: CancellationToken())
      print(results.joined(separator: "\n"))
    case "restore":
      guard flags.contains("--experimental-writes") else {
        throw MchoseError.invalid("Mac 实机写入尚未验证；恢复需要显式 --experimental-writes。")
      }
      let target = try KeyboardProfile.read(from: url("--profile"))
      let backupURL = try output("--backup")
      try await service.restore(
        deviceID: deviceID(), target: target, backupURL: backupURL, token: CancellationToken())
      print("恢复完成，全部已解析字段回读一致；仍需人工验证灯光和按键行为。")
    default: throw MchoseError.invalid("未知命令。")
    }
  }
  static func hardwareModel() -> String {
    var size = 0
    sysctlbyname("hw.model", nil, &size, nil, 0)
    guard size > 0 else { return "unknown" }
    var value = [CChar](repeating: 0, count: size)
    guard sysctlbyname("hw.model", &value, &size, nil, 0) == 0 else { return "unknown" }
    return String(
      decoding: value.prefix(while: { $0 != 0 }).map { UInt8(bitPattern: $0) }, as: UTF8.self)
  }
  static let help = """
    MCHOSE HUB macOS · K87 USB developer preview
      mchose list [--all]                   仅枚举，不打开设备或发送请求
      mchose watch [--seconds 15]           只读拔插监测
      mchose backup --device ID --out DIR   两次完整读取及原始报告（DIR 必须不存在）
      mchose validate --profile FILE
      mchose roundtrip --profile FILE --out FILE
      mchose compare --expected FILE --actual FILE
      mchose restore --device ID --profile FILE --backup NEW_FILE --experimental-writes
    不支持 2.4G/蓝牙、宏库写入、固件升级或恢复出厂。
    """
}
