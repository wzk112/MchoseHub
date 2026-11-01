import Foundation
import IOKit
import IOKit.hid
import IOKit.hidsystem
import MchoseCore

public struct HIDDeviceInfo: Codable, Sendable, Identifiable, Equatable {
  public let registryID: UInt64
  public let physicalID: String
  public let product: String
  public let vendorID: Int
  public let productID: Int
  public let usagePage: Int
  public let usage: Int
  public let transport: String
  public let version: Int
  public let maxFeatureReportSize: Int
  public let descriptor: Data
  public let reports: [HIDDescriptor.Report]
  public let rejection: String?
  public var id: UInt64 { registryID }
  public var isSupported: Bool { rejection == nil }
  public var isAudio: Bool {
    AudioProtocol.devices.contains(String(format: "%04X:%04X", vendorID, productID))
      && reports.contains { $0.type == "output" && [0xBC, 0x55].contains($0.id) }
  }
  public var isWireless: Bool {
    usagePage >= 0xFF00
      && (product.localizedCaseInsensitiveContains("MCHOSE 2.4G Wireless")
        || product.localizedCaseInsensitiveContains("2.4G Wireless Receiver"))
      && reports.contains { $0.type == "input" && $0.id == 19 && $0.payloadBytes >= 19 }
      && reports.contains { $0.type == "output" && $0.id == 19 && $0.payloadBytes >= 19 }
  }
  public var isMchose: Bool {
    product.localizedCaseInsensitiveContains("MCHOSE") || isWireless || isAudio
  }
}

public enum MacHID {
  public static var inputAccessDescription: String {
    switch IOHIDCheckAccess(kIOHIDRequestTypeListenEvent) {
    case kIOHIDAccessTypeGranted: "已允许"
    case kIOHIDAccessTypeDenied: "已拒绝"
    default: "尚未授权"
    }
  }
  /// IORegistry enumeration only: does not open HID devices, request input monitoring, or send reports.
  public static func scan() throws -> [HIDDeviceInfo] {
    var iterator: io_iterator_t = 0
    let status = IOServiceGetMatchingServices(
      kIOMainPortDefault, IOServiceMatching("IOHIDDevice"), &iterator)
    try require(status, "枚举 HID")
    defer { IOObjectRelease(iterator) }
    var result: [HIDDeviceInfo] = []
    while case let service = IOIteratorNext(iterator), service != 0 {
      defer { IOObjectRelease(service) }
      if let device = IOHIDDeviceCreate(kCFAllocatorDefault, service) {
        result.append(info(device))
      }
    }
    return result.sorted { $0.registryID < $1.registryID }
  }

  static func info(_ device: IOHIDDevice) -> HIDDeviceInfo {
    func number(_ key: String) -> Int {
      (IOHIDDeviceGetProperty(device, key as CFString) as? NSNumber)?.intValue ?? 0
    }
    func string(_ key: String) -> String {
      IOHIDDeviceGetProperty(device, key as CFString) as? String ?? ""
    }
    var registryID: UInt64 = 0
    IORegistryEntryGetRegistryEntryID(IOHIDDeviceGetService(device), &registryID)
    let product = string(kIOHIDProductKey)
    let vendor = number(kIOHIDVendorIDKey)
    let pid = number(kIOHIDProductIDKey)
    let usagePage = number(kIOHIDPrimaryUsagePageKey)
    let transport = string(kIOHIDTransportKey)
    let descriptor =
      IOHIDDeviceGetProperty(device, kIOHIDReportDescriptorKey as CFString) as? Data ?? Data()
    let parsed = try? HIDDescriptor(data: descriptor)
    let reports = parsed?.reports ?? []
    let rejection: String?
    let wireless =
      usagePage >= 0xFF00
      && (product.localizedCaseInsensitiveContains("MCHOSE 2.4G Wireless")
        || product.localizedCaseInsensitiveContains("2.4G Wireless Receiver"))
      && reports.contains { $0.type == "input" && $0.id == 19 && $0.payloadBytes >= 19 }
      && reports.contains { $0.type == "output" && $0.id == 19 && $0.payloadBytes >= 19 }
    let schema = (try? KeyboardSchema.resolve(product: product)).flatMap {
      try? KeyboardSchema.load(model: $0)
    }
    let audio =
      AudioProtocol.devices.contains(String(format: "%04X:%04X", vendor, pid))
      && reports.contains { $0.type == "output" && [0xBC, 0x55].contains($0.id) }
    if transport == "USB" && (wireless || audio) {
      rejection = nil
    } else if schema == nil || !product.localizedCaseInsensitiveContains("MCHOSE") {
      rejection = "未识别的键盘产品名。"
    } else if transport != "USB" {
      rejection = "仅支持 USB 有线连接。"
    } else if usagePage < 0xFF00 {
      rejection = "不是厂商配置集合。"
    } else if !reports.contains(where: {
      $0.type == "feature" && $0.id == Int(schema!.reportID) && $0.payloadBytes == 519
    }) {
      rejection = "描述符未确认型号对应的 519 字节 feature payload；禁止探测。"
    } else {
      rejection = nil
    }
    let location = number(kIOHIDLocationIDKey)
    // A Mac USB location distinguishes identical models without Windows ContainerId or serial numbers.
    let physicalID =
      location == 0
      ? "registry:\(registryID)" : String(format: "usb:%08X:%04X:%04X", location, vendor, pid)
    return HIDDeviceInfo(
      registryID: registryID, physicalID: physicalID, product: product,
      vendorID: vendor, productID: pid, usagePage: usagePage, usage: number(kIOHIDPrimaryUsageKey),
      transport: transport, version: number(kIOHIDVersionNumberKey),
      maxFeatureReportSize: number(kIOHIDMaxFeatureReportSizeKey), descriptor: descriptor,
      reports: reports, rejection: rejection)
  }

  static func require(_ status: IOReturn, _ operation: String) throws {
    if status == kIOReturnNotPermitted {
      throw MchoseError.invalid(
        "macOS 未允许此应用访问 HID 输入报告。请在系统设置 → 隐私与安全性 → 输入监控中允许 MCHOSE HUB，退出应用后重新打开。")
    }
    guard status == kIOReturnSuccess else {
      throw MchoseError.invalid(
        String(format: "%@失败（IOKit 0x%08X）。", operation, UInt32(bitPattern: status)))
    }
  }
}

/// Confined to ConfigurationService's queue. It never registers input callbacks or seizes a keyboard.
struct NativeInputReport {
  let id: UInt8
  let data: Data
}
final class MacFeatureTransport: FeatureTransport, PacketTransport {
  private final class InputState: @unchecked Sendable {
    let condition = NSCondition()
    var inbox: [NativeInputReport] = []
    var trace: [[String: String]] = []
    var removed = false
  }
  private let device: IOHIDDevice
  private let registryID: UInt64
  private var isOpen = false
  let reportID: UInt8
  private let wireless: Bool
  private let audio: Bool
  private let outputSizes: [Int: Int]
  private let inputBuffer: UnsafeMutablePointer<UInt8>
  private let input = InputState()
  private let eventQueue = DispatchQueue(label: "com.mchosehub.hid.input")
  private let cancelled = DispatchSemaphore(value: 0)
  private let checkCancellation: () throws -> Void
  private let traceURL: URL?
  private var trace: [[String: String]] = []
  init(registryID: UInt64, checkCancellation: @escaping () throws -> Void, traceURL: URL? = nil)
    throws
  {
    self.registryID = registryID
    self.checkCancellation = checkCancellation
    self.traceURL = traceURL
    let service = IOServiceGetMatchingService(
      kIOMainPortDefault, IORegistryEntryIDMatching(registryID))
    guard service != 0 else { throw MchoseError.invalid("设备已经拔出，请重新选择。") }
    defer { IOObjectRelease(service) }
    guard let device = IOHIDDeviceCreate(kCFAllocatorDefault, service) else {
      throw MchoseError.invalid("无法创建 HID 设备。")
    }
    let info = MacHID.info(device)
    guard info.isSupported else { throw MchoseError.invalid(info.rejection ?? "不支持的接口。") }
    self.device = device
    wireless = info.isWireless
    audio = info.isAudio
    outputSizes = Dictionary(
      info.reports.filter { $0.type == "output" }.map { ($0.id, $0.payloadBytes) },
      uniquingKeysWith: max)
    reportID =
      wireless
      ? 19
      : audio
        ? 0xBC
        : (try KeyboardSchema.load(model: KeyboardSchema.resolve(product: info.product))).reportID
    try MacHID.require(IOHIDDeviceOpen(device, IOOptionBits(kIOHIDOptionsTypeNone)), "打开 K87 配置集合")
    inputBuffer = .allocate(capacity: 4096)
    inputBuffer.initialize(repeating: 0, count: 4096)
    isOpen = true
    trace.append(["inputAccess": MacHID.inputAccessDescription])
    if wireless || audio {
      IOHIDDeviceRegisterInputReportCallback(
        device, inputBuffer, 4096,
        { context, status, _, _, id, report, length in
          guard let context else { return }
          let state = Unmanaged<InputState>.fromOpaque(context).takeUnretainedValue()
          state.condition.lock()
          defer { state.condition.unlock() }
          if [19, 0xBC, 0x55].contains(id) {
            state.trace.append([
              "direction": "input", "id": String(id), "length": String(length),
              "status": String(status),
              "data": Data(bytes: report, count: max(0, length)).base64EncodedString(),
            ])
          }
          guard status == kIOReturnSuccess, [19, 0xBC, 0x55].contains(id), length > 0 else {
            return
          }
          var packet = Data(bytes: report, count: length)
          if id == 19 {
            if packet.count == 20, packet.first == 19 { packet = Data(packet.dropFirst()) }
            guard packet.count == 19 else { return }
          } else if packet.first == UInt8(id) {
            packet = Data(packet.dropFirst())
          }
          state.inbox.append(NativeInputReport(id: UInt8(id), data: packet))
          if state.inbox.count > 512 { state.inbox.removeFirst(state.inbox.count - 512) }
          state.condition.signal()
        }, Unmanaged.passUnretained(input).toOpaque())
      IOHIDDeviceRegisterRemovalCallback(
        device,
        { context, _, _ in
          guard let context else { return }
          let state = Unmanaged<InputState>.fromOpaque(context).takeUnretainedValue()
          state.condition.lock()
          state.removed = true
          state.condition.broadcast()
          state.condition.unlock()
        }, Unmanaged.passUnretained(input).toOpaque())
      let signal = cancelled
      IOHIDDeviceSetDispatchQueue(device, eventQueue)
      IOHIDDeviceSetCancelHandler(device) { signal.signal() }
      IOHIDDeviceActivate(device)
    }
  }
  deinit {
    if isOpen {
      if wireless || audio {
        IOHIDDeviceCancel(device)
        cancelled.wait()
      }
      IOHIDDeviceClose(device, IOOptionBits(kIOHIDOptionsTypeNone))
    }
    inputBuffer.deinitialize(count: 4096)
    inputBuffer.deallocate()
    if let traceURL,
      let data = try? JSONSerialization.data(
        withJSONObject: trace + input.trace, options: [.prettyPrinted, .sortedKeys])
    {
      try? data.write(to: traceURL, options: .atomic)
    }
  }

  private func checkConnected() throws {
    let service = IOServiceGetMatchingService(
      kIOMainPortDefault, IORegistryEntryIDMatching(registryID))
    guard service != 0 else { throw MchoseError.invalid("设备已经拔出；已停止旧会话。") }
    IOObjectRelease(service)
  }
  func sendFeature(payload: Data) throws {
    try checkConnected()
    if let command = payload.first, command < 0x80 {
      WriteThrottle.shared.waitForSlot()
      try checkCancellation()
    }
    let framed = try WiredFrame.sendBuffer(payload: payload, reportID: reportID)
    let status = framed.withUnsafeBytes {
      IOHIDDeviceSetReport(
        device, kIOHIDReportTypeFeature, CFIndex(reportID),
        $0.bindMemory(to: UInt8.self).baseAddress!, framed.count)
    }
    try MacHID.require(status, "发送 feature report")
  }
  func getFeature() throws -> Data {
    try checkConnected()
    var buffer = [UInt8](repeating: 0, count: 520)
    var actual = 520
    let status = IOHIDDeviceGetReport(
      device, kIOHIDReportTypeFeature, CFIndex(reportID), &buffer, &actual)
    try MacHID.require(status, "读取 feature report")
    guard actual >= 0, actual <= buffer.count else {
      throw MchoseError.invalid("IOKit 返回长度无效：\(actual)。")
    }
    // Fail closed until actual hardware establishes another native convention. A 519-byte
    // reply could be a truncated framed report; blindly prepending 06 would conceal that.
    // Return exactly what IOKit supplied. Core records it before checking length/ID/echo,
    // so a short or unexpected response remains available in a local diagnostic capture.
    return Data(buffer.prefix(actual))
  }
  func sendOutput(_ payload: Data) throws {
    try checkCancellation()
    try checkConnected()
    guard payload.count == 19 else { throw MchoseError.invalid("无线逻辑包必须为 19 字节。") }
    try sendOutput(reportID: 19, payload: payload)
  }
  func sendOutput(reportID: UInt8, payload: Data) throws {
    try checkCancellation()
    try checkConnected()
    guard let length = outputSizes[Int(reportID)], payload.count <= length else {
      throw MchoseError.invalid("该 HID 描述符不支持报告 ID 或 payload 长度。")
    }
    let frame =
      (reportID == 0 ? Data() : Data([reportID])) + payload
      + Data(repeating: 0, count: length - payload.count)
    trace.append([
      "direction": "output", "id": String(reportID), "length": String(frame.count),
      "data": frame.base64EncodedString(),
    ])
    try MacHID.require(
      frame.withUnsafeBytes {
        IOHIDDeviceSetReport(
          device, kIOHIDReportTypeOutput, CFIndex(reportID),
          $0.bindMemory(to: UInt8.self).baseAddress!, frame.count)
      }, "发送 output report")
  }
  func nextPacket(timeout: TimeInterval) throws -> Data? {
    let report = try nextNativePacket(timeout: timeout)
    return report?.id == 19 ? report?.data : nil
  }
  func nextNativePacket(timeout: TimeInterval) throws -> NativeInputReport? {
    try checkCancellation()
    try checkConnected()
    input.condition.lock()
    if input.inbox.isEmpty && !input.removed {
      _ = input.condition.wait(until: Date().addingTimeInterval(min(max(timeout, 0.001), 0.2)))
    }
    let packet = input.inbox.isEmpty ? nil : input.inbox.removeFirst()
    input.condition.unlock()
    try checkCancellation()
    try checkConnected()
    return packet
  }
  func clearInbox() {
    input.condition.lock()
    input.inbox.removeAll()
    input.condition.unlock()
  }
}

public final class CancellationToken: @unchecked Sendable {
  private let lock = NSLock()
  private var cancelled = false
  public init() {}
  public func cancel() { lock.withLock { cancelled = true } }
  public func check() throws { if lock.withLock({ cancelled }) { throw CancellationError() } }
}

public final class ConfigurationService: Sendable {
  private let queue = DispatchQueue(label: "com.mchosehub.hid", qos: .userInitiated)
  public init() {}
  public func scan() async throws -> [HIDDeviceInfo] {
    try await withCheckedThrowingContinuation { continuation in
      queue.async { continuation.resume(with: Result { try MacHID.scan() }) }
    }
  }
  public func backup(deviceID: UInt64, token: CancellationToken, diagnosticURL: URL? = nil)
    async throws -> DeviceBackup
  {
    try await perform(
      deviceID: deviceID, token: token,
      traceURL: diagnosticURL?.deletingLastPathComponent().appendingPathComponent(
        "wireless-packets.json")
    ) { session in
      do { return try session.backup() } catch {
        if let diagnosticURL {
          let encoder = JSONEncoder()
          encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
          try encoder.encode(session.exchanges).write(to: diagnosticURL, options: .atomic)
        }
        throw error
      }
    }
  }
  public func restore(
    deviceID: UInt64, target: KeyboardProfile, backupURL: URL, token: CancellationToken
  ) async throws {
    try await perform(deviceID: deviceID, token: token) { session in
      let schema = try KeyboardSchema.load(model: target.model)
      try target.validate(schema: schema)
      guard !FileManager.default.fileExists(atPath: backupURL.path) else {
        throw MchoseError.invalid("恢复前备份路径已存在，拒绝覆盖。")
      }
      let before = try session.backup()
      try before.profile.save(to: backupURL, schema: schema)
      try session.restore(target)
    }
  }
  public func apply(
    deviceID: UInt64, target: KeyboardProfile, baseline: KeyboardProfile?, backupURL: URL,
    token: CancellationToken
  ) async throws {
    try await perform(deviceID: deviceID, token: token) { session in
      let schema = try KeyboardSchema.load(model: target.model)
      try target.validate(schema: schema)
      let fresh = try session.backup().profile
      if let baseline {
        let differences = try baseline.differences(from: fresh, schema: schema)
        guard differences.isEmpty else {
          throw MchoseError.invalid("设备设置已变化，请重新读取后编辑：\(differences.joined(separator:", "))")
        }
      }
      guard !FileManager.default.fileExists(atPath: backupURL.path) else {
        throw MchoseError.invalid("恢复前备份已存在。")
      }
      try fresh.save(to: backupURL, schema: schema)
      var changed = Set<ReadSection>()
      if fresh.performance != target.performance { changed.insert(.performance) }
      if fresh.lighting != target.lighting { changed.insert(.lighting) }
      if fresh.diy != target.diy { changed.insert(.diy) }
      for i in 0...2 where fresh.keyLayers[i] != target.keyLayers[i] {
        changed.insert([.defaultKeys, .fnKeys, .fn2Keys][i])
      }
      try session.restore(target, sections: changed)
    }
  }
  public func uploadMacros(deviceID: UInt64, macros: [KeyboardMacro], token: CancellationToken)
    async throws
  {
    try await perform(deviceID: deviceID, token: token) { session in
      try session.uploadMacros(macros, catalog: FunctionCatalog.load())
    }
  }
  public func diagnostic(deviceID: UInt64, name: String, token: CancellationToken) async throws
    -> Data
  {
    try await perform(deviceID: deviceID, token: token) { try $0.diagnostic(name) }
  }
  public func raw(deviceID: UInt64, payload: Data, token: CancellationToken) async throws -> Data {
    try await perform(deviceID: deviceID, token: token) { try $0.raw(payload) }
  }
  public func reversibleK87Tests(deviceID: UInt64, directory: URL, token: CancellationToken)
    async throws -> [String]
  {
    try await perform(
      deviceID: deviceID, token: token,
      traceURL: directory.appendingPathComponent("wireless-packets.json")
    ) { session in
      let schema = try KeyboardSchema.load()
      let original = try session.backup().profile
      try original.validate(schema: schema)
      guard original.performance.numbers["lightMode"] == 11,
        original.performance.numbers["brightness11"] == 3,
        original.performance.numbers["sleepTimeVal"] == 6
      else { throw MchoseError.invalid("当前设置与已批准的测试基线不同，未开始写入。") }
      try original.save(to: directory.appendingPathComponent("before.json"), schema: schema)
      var completed: [String] = []
      let tests: [String] = [
        "brightness11: 3 → 4 → 3", "sleepTimeVal: 6 → 10 → 6",
        "Default.Scroll → F1 → original raw value",
      ]
      for (i, name) in tests.enumerated() {
        let fresh = try session.backup().profile
        guard try original.differences(from: fresh, schema: schema).isEmpty else {
          throw MchoseError.invalid("设备在测试之间发生变化，已停止；未覆盖新的设置。")
        }
        var target = fresh
        let section: ReadSection = i == 2 ? .defaultKeys : .performance
        if i == 0 { target.performance.numbers["brightness11"] = 4 }
        if i == 1 { target.performance.numbers["sleepTimeVal"] = 10 }
        if i == 2 {
          try target.remap(key: "Scroll", layer: 0, value: Data([0, 0, 0, 58]), schema: schema)
        }
        try target.save(to: directory.appendingPathComponent("trial-\(i+1).json"), schema: schema)
        do {
          try session.restore(target, sections: [section])
          try session.backup().profile.save(
            to: directory.appendingPathComponent("applied-\(i+1).json"), schema: schema)
        } catch let failure {
          // Exactly one recovery attempt for the corresponding original block.
          do { try session.restore(fresh, sections: [section]) } catch {
            throw MchoseError.invalid(
              "\(name) 失败：\(failure.localizedDescription)；原值恢复亦失败：\(error.localizedDescription)")
          }
          throw MchoseError.invalid("\(name) 失败，已尝试恢复原值并验证：\(failure.localizedDescription)")
        }
        try session.restore(fresh, sections: [section])
        let restored = try session.backup().profile
        let differences = try original.differences(from: restored, schema: schema)
        guard differences.isEmpty else {
          throw MchoseError.invalid("恢复后存在差异：\(differences.joined(separator:", "))")
        }
        try restored.save(
          to: directory.appendingPathComponent("restored-\(i+1).json"), schema: schema)
        completed.append("PASS \(name); all parsed fields match original")
        try Data(completed.joined(separator: "\n").utf8).write(
          to: directory.appendingPathComponent("results.txt"), options: .atomic)
      }
      return completed
    }
  }
  private func perform<T: Sendable>(
    deviceID: UInt64, token: CancellationToken,
    traceURL: URL? = nil,
    operation: @escaping @Sendable (KeyboardSession) throws -> T
  ) async throws -> T {
    try await withCheckedThrowingContinuation { continuation in
      queue.async {
        continuation.resume(
          with: Result {
            try token.check()
            let transport = try MacFeatureTransport(
              registryID: deviceID, checkCancellation: token.check, traceURL: traceURL)
            guard let selected = try MacHID.scan().first(where: { $0.id == deviceID }) else {
              throw MchoseError.invalid("设备已拔出。")
            }
            let schema: KeyboardSchema
            let channel: any FeatureTransport
            if selected.isWireless {
              let wireless = WirelessChannel(transport: transport, checkCancellation: token.check)
              schema = try wireless.resolveModel()
              channel = wireless
            } else {
              schema = try KeyboardSchema.load(
                model: KeyboardSchema.resolve(product: selected.product))
              channel = transport
            }
            let session = KeyboardSession(
              transport: channel, schema: schema, checkCancellation: token.check)
            return try operation(session)
          })
      }
    }
  }
}
