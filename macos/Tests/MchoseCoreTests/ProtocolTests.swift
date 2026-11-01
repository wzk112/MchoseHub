import Foundation
import MchoseHID
import XCTest

@testable import MchoseCore

final class ProtocolTests: XCTestCase {
  struct Wire: Decodable {
    let Responses: [String: Data]
    let Writes: [Data]
  }
  func fixture<T: Decodable>(_ name: String, as type: T.Type = T.self) throws -> T {
    let url = Bundle.module.url(forResource: name, withExtension: "json", subdirectory: "Fixtures")!
    return try JSONDecoder().decode(type, from: Data(contentsOf: url))
  }
  func testCSharpProfileAndAllReadPackets() throws {
    let schema = try K87Schema.load()
    let profile: KeyboardProfile = try fixture("profile")
    let wire: Wire = try fixture("wire")
    try profile.validate(schema: schema)
    let expected = [profile.performance, profile.lighting, profile.diy] + profile.keyLayers
    for (i, section) in ReadSection.allCases.dropFirst().enumerated() {
      let response = wire.Responses[section.rawValue]!
      try WiredFrame.validate(response, for: section)
      XCTAssertEqual(
        expected[i],
        try PayloadCodec.decode(
          schema.commands[section.command]!, response: response, keyList: schema.keyList),
        section.rawValue)
    }
  }
  func testAllRestorePacketsEqualCSharpByteForByte() throws {
    let schema = try K87Schema.load()
    let profile: KeyboardProfile = try fixture("profile")
    let wire: Wire = try fixture("wire")
    let plan = try profile.compileWrites(schema: schema)
    XCTAssertEqual(plan.map(\.payload), wire.Writes)
    XCTAssertEqual(
      plan.map(\.section), [.lighting, .diy, .defaultKeys, .fnKeys, .fn2Keys, .performance])
    XCTAssertEqual(plan.first?.payload.count, 519)
  }
  func testJSONPreservesBase64AndReservedSlots() throws {
    let original: KeyboardProfile = try fixture("profile")
    let data = try JSONEncoder().encode(original)
    let object = try JSONSerialization.jsonObject(with: data) as! [String: Any]
    let snapshot = object["KeyLayers"] as! [[String: Any]]
    let table = (snapshot[0]["Tables"] as! [String: Any])["#keyList"] as! [String]
    XCTAssertEqual(Data(base64Encoded: table[70]), Data([0x12, 70, 0, 0x87]))
    XCTAssertEqual(try JSONDecoder().decode(KeyboardProfile.self, from: data), original)
  }
  func testWrongModelVersionAndLayerCountRejected() throws {
    let schema = try K87Schema.load()
    let original: KeyboardProfile = try fixture("profile")
    var p = original
    p.model = "K99"
    XCTAssertThrowsError(try p.validate(schema: schema))
    p = original
    p.version = 2
    XCTAssertThrowsError(try p.validate(schema: schema))
    p = original
    p.keyLayers.removeLast()
    XCTAssertThrowsError(try p.validate(schema: schema))
  }
  func testMissingUnknownDuplicateAndWrongTypeRejected() throws {
    let schema = try K87Schema.load()
    let original: KeyboardProfile = try fixture("profile")
    var p = original
    p.lighting.buffers.removeValue(forKey: "empty14")
    XCTAssertThrowsError(try p.validate(schema: schema))
    p = original
    p.performance.numbers["unknown"] = 0
    XCTAssertThrowsError(try p.validate(schema: schema))
    p = original
    p.performance.numbers["RATEVAL"] = 1
    XCTAssertThrowsError(try p.validate(schema: schema))
    p = original
    p.performance.buffers["rateVal"] = Data([3])
    XCTAssertThrowsError(try p.validate(schema: schema))
  }
  func testBadHeadersAndArrayLengthsRejected() throws {
    let schema = try K87Schema.load()
    let original: KeyboardProfile = try fixture("profile")
    var p = original
    p.diy.buffers["space0"] = schema.diyPre
    XCTAssertThrowsError(try p.validate(schema: schema))
    p = original
    p.diy.buffers["rTable"] = Data([0])
    XCTAssertThrowsError(try p.validate(schema: schema))
    p = original
    p.keyLayers[2].tables["#keyList"]?[70] = Data([0])
    XCTAssertThrowsError(try p.validate(schema: schema))
    p = original
    p.keyLayers[1].buffers["space0"] = p.keyLayers[0].buffers["space0"]
    XCTAssertThrowsError(try p.validate(schema: schema))
  }
  func testSemanticBoundsAndNibblesRejected() throws {
    let schema = try K87Schema.load()
    let original: KeyboardProfile = try fixture("profile")
    for (name, value) in [
      ("rateVal", 0), ("rateVal", 4), ("brightness2", 101), ("speed1", 16), ("macSwitch", 4),
      ("space2", -1),
    ] {
      var p = original
      p.performance.numbers[name] = value
      XCTAssertThrowsError(try p.validate(schema: schema), name)
    }
  }
  func testTruncatedReadsNeverBecomeZeros() throws {
    let schema = try K87Schema.load()
    let wire: Wire = try fixture("wire")
    for section in ReadSection.allCases.dropFirst() {
      let data = wire.Responses[section.rawValue]!
      XCTAssertThrowsError(
        try PayloadCodec.decode(
          schema.commands[section.command]!, response: data.prefix(12), keyList: schema.keyList))
      XCTAssertThrowsError(try WiredFrame.validate(data.dropLast(), for: section))
    }
  }
  func testReportIDSevenByteEchoAndStaleBufferRejected() throws {
    let wire: Wire = try fixture("wire")
    var response = wire.Responses["performance"]!
    response[0] = 9
    XCTAssertThrowsError(try WiredFrame.validate(response, for: .performance))
    response = wire.Responses["performance"]!
    response[7] ^= 1
    XCTAssertThrowsError(try WiredFrame.validate(response, for: .performance))
    response = wire.Responses["performance"]!
    response.replaceSubrange(8..<12, with: ReadSection.performance.header.prefix(4))
    XCTAssertThrowsError(try WiredFrame.validate(response, for: .performance))
  }
  func testIDAddedOnlyAtBoundaryAndPayloadSizeStrict() throws {
    let payload = ReadSection.performance.payload
    let frame = try WiredFrame.sendBuffer(payload: payload)
    XCTAssertEqual(frame.count, 520)
    XCTAssertEqual(Array(frame.prefix(3)), [6, 0x84, 0])
    XCTAssertThrowsError(try WiredFrame.sendBuffer(payload: payload.dropLast()))
    XCTAssertThrowsError(try WiredFrame.sendBuffer(payload: frame))
  }
  func testIncompleteAndOversizeWriteRejected() throws {
    let schema = try K87Schema.load()
    XCTAssertThrowsError(
      try PayloadCodec.encode(schema.commands["setPerformance"]!, PayloadSnapshot(), command: 4))
    var profile: KeyboardProfile = try fixture("profile")
    profile.lighting.buffers["empty14"] = Data(repeating: 0, count: 1000)
    XCTAssertThrowsError(try profile.compileWrites(schema: schema))
  }
  func testDiffIncludesInactiveLightingAndReservedKeySlots() throws {
    let schema = try K87Schema.load()
    let original: KeyboardProfile = try fixture("profile")
    var changed = original
    changed.performance.numbers["brightness2"] = 42
    changed.performance.buffers["space11"]?[3] ^= 1
    changed.keyLayers[2].tables["#keyList"]?[70][3] ^= 1
    XCTAssertEqual(
      try original.differences(from: changed, schema: schema),
      ["Performance.brightness2", "Performance.space11", "Fn2.#keyList[70]"])
  }
  func testDescriptorCountsReportIDsPaddingAndGlobalPushPop() throws {
    let d = try HIDDescriptor(
      data: Data([
        0x85, 6, 0x75, 8, 0x96, 7, 2, 0xB1, 2, 0xA4, 0x85, 7, 0x95, 2, 0x81, 1, 0xB4, 0xB1, 1,
      ]))
    XCTAssertEqual(d.reports.first { $0.type == "feature" && $0.id == 6 }?.payloadBytes, 1038)
    XCTAssertEqual(d.reports.first { $0.type == "input" }?.payloadBytes, 2)
    let feature = try HIDDescriptor(data: Data([0x85, 6, 0x75, 8, 0x96, 7, 2, 0xB1, 2])).reports[0]
    XCTAssertEqual(feature.payloadBytes, 519)
    XCTAssertEqual(feature.framedBytes, 520)
  }
  func testDescriptorTruncationAndOverflowRejected() {
    for bytes: [UInt8] in [
      [0x96, 7], [0xB4], [0xA4], [0x85, 0], [0xFE, 10, 0, 0],
      [0x77, 255, 255, 255, 255, 0x95, 2, 0xB1, 2],
    ] {
      XCTAssertThrowsError(try HIDDescriptor(data: Data(bytes)))
    }
  }
  func testPreviewUsesSeparateValueStorage() throws {
    let schema = try K87Schema.load()
    var preview = try PreviewProfile.make(schema: schema)
    preview.keyLayers[0].tables["#keyList"]?[70] = Data([1, 2, 3, 4])
    let fresh = try PreviewProfile.make(schema: schema)
    XCTAssertEqual(fresh.keyLayers[0].tables["#keyList"]?[70], Data(repeating: 0, count: 4))
  }
  func testBackupUsesOnlyKnownReadsAndDoubleFullComparison() throws {
    let fake = try FakeDevice()
    let session = KeyboardSession(transport: fake, schema: fake.schema, sleep: { _ in })
    let result = try session.backup()
    XCTAssertEqual(result.profile, fake.profile)
    XCTAssertEqual(result.battery, 50)
    XCTAssertEqual(fake.writes, 0)
    XCTAssertEqual(fake.requests.count, 25)
    XCTAssertEqual(result.exchanges.count, 25)
  }
  func testStableReadRequiresConsecutiveMatches() throws {
    let fake = try FakeDevice()
    fake.alternate = true
    let session = KeyboardSession(transport: fake, schema: fake.schema, sleep: { _ in })
    XCTAssertThrowsError(try session.stableRead(.performance))
    XCTAssertEqual(fake.requests.count, 4)
  }
  func testCancellationStopsBeforeAnyReport() throws {
    let fake = try FakeDevice()
    let token = CancellationToken()
    token.cancel()
    let session = KeyboardSession(
      transport: fake, schema: fake.schema, checkCancellation: token.check, sleep: { _ in })
    XCTAssertThrowsError(try session.backup())
    XCTAssertEqual(fake.requests.count, 0)
  }
  func testRestoreOrderFullComparisonAndMinimumWriteGap() throws {
    let fake = try FakeDevice()
    var time: TimeInterval = 10
    let session = KeyboardSession(
      transport: fake, schema: fake.schema, sleep: { time += $0 }, clock: { time })
    var target = fake.profile
    target.performance.numbers["brightness2"] = 42
    target.keyLayers[1].tables["#keyList"]?[70] = Data([0, 0, 0, 0x87])
    fake.now = { time }
    try session.restore(target)
    XCTAssertEqual(fake.writeCommands, [10, 6, 3, 3, 3, 4])
    XCTAssertEqual(fake.profile, target)
    XCTAssertTrue(zip(fake.writeTimes, fake.writeTimes.dropFirst()).allSatisfy { $1 - $0 >= 1.5 })
  }
  func testRestoreFailureStopsWithoutRepeatingAnyWrite() throws {
    let fake = try FakeDevice()
    fake.failWrite = 2
    let session = KeyboardSession(transport: fake, schema: fake.schema, sleep: { _ in })
    XCTAssertThrowsError(try session.restore(fake.profile))
    XCTAssertEqual(fake.writes, 2)
    XCTAssertEqual(fake.writeCommands, [10, 6])
  }
  func testIgnoredInactiveLightingFailsAndDoesNotRetry() throws {
    let fake = try FakeDevice()
    fake.ignoreBrightness2 = true
    let session = KeyboardSession(transport: fake, schema: fake.schema, sleep: { _ in })
    var target = fake.profile
    target.performance.numbers["brightness2"] = 42
    XCTAssertThrowsError(try session.restore(target)) {
      XCTAssertTrue($0.localizedDescription.contains("brightness2"))
    }
    XCTAssertEqual(fake.writes, 6)
  }
}

private final class FakeDevice: FeatureTransport {
  let schema: K87Schema
  var profile: KeyboardProfile
  var requests: [Data] = [], writes = 0, writeCommands: [UInt8] = [],
    writeTimes: [TimeInterval] = []
  var failWrite: Int?, alternate = false, ignoreBrightness2 = false
  var now: () -> TimeInterval = { 0 }
  private var response = Data()
  init() throws {
    schema = try K87Schema.load()
    let url = Bundle.module.url(
      forResource: "profile", withExtension: "json", subdirectory: "Fixtures")!
    profile = try JSONDecoder().decode(KeyboardProfile.self, from: Data(contentsOf: url))
  }
  func sendFeature(payload: Data) throws {
    requests.append(payload)
    if payload[0] < 0x80 {
      writes += 1
      writeCommands.append(payload[0])
      writeTimes.append(now())
      if writes == failWrite { throw MchoseError.invalid("injected write failure") }
      let section: ReadSection
      switch payload[0] {
      case 10: section = .lighting
      case 6: section = .diy
      case 3: section = [.defaultKeys, .fnKeys, .fn2Keys][Int(payload[1])]
      default: section = .performance
      }
      var value = try PayloadCodec.decode(
        schema.commands[section.command]!, response: Data([6]) + payload, keyList: schema.keyList)
      value.buffers["space0"] = Data(section.header.dropFirst())
      switch section {
      case .lighting: profile.lighting = value
      case .diy: profile.diy = value
      case .defaultKeys: profile.keyLayers[0] = value
      case .fnKeys: profile.keyLayers[1] = value
      case .fn2Keys: profile.keyLayers[2] = value
      default:
        if ignoreBrightness2 {
          value.numbers["brightness2"] = profile.performance.numbers["brightness2"]
        }
        profile.performance = value
      }
      response = Data([6]) + Data(repeating: 0, count: 519)
      return
    }
    let section = ReadSection.allCases.first { $0.header == payload.prefix(7) }!
    if section == .battery {
      response = Data([6]) + section.header + Data([50]) + Data(repeating: 0, count: 511)
      return
    }
    let snapshot: PayloadSnapshot
    switch section {
    case .performance: snapshot = profile.performance
    case .lighting: snapshot = profile.lighting
    case .diy: snapshot = profile.diy
    case .defaultKeys: snapshot = profile.keyLayers[0]
    case .fnKeys: snapshot = profile.keyLayers[1]
    case .fn2Keys: snapshot = profile.keyLayers[2]
    default: fatalError()
    }
    let encoded = try PayloadCodec.encode(
      schema.commands[section.command]!, snapshot, command: section.header.first!)
    response = Data([6]) + encoded + Data(repeating: 0, count: 519 - encoded.count)
    if alternate { response[8] ^= UInt8(requests.count % 2) }
  }
  func getFeature() throws -> Data { response }
}
