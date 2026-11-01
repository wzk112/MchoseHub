import Foundation
import XCTest

@testable import MchoseCore

final class ExtendedTests: XCTestCase {
  struct ModelFixture: Decodable {
    let Profile: KeyboardProfile
    let Writes: [Data]
  }
  struct Extended: Decodable {
    struct WirelessFixture: Decodable {
      let Command: UInt8
      let Layer: UInt8
      let Data: Data
      let Packets: [Data]
    }
    let Wireless: [WirelessFixture]
    let Macros: [KeyboardMacro]
    let Bank: Data
    let Pages: [Data]
  }
  func read<T: Decodable>(_ name: String, as type: T.Type = T.self) throws -> T {
    try JSONDecoder().decode(
      type,
      from: Data(
        contentsOf: Bundle.module.url(
          forResource: name, withExtension: "json", subdirectory: "Fixtures")!))
  }
  func testAll18ModelsCompileExactlyAsCSharp() throws {
    let fixtures: [ModelFixture] = try read("all-models")
    XCTAssertEqual(fixtures.count, 18)
    for fixture in fixtures {
      let schema = try KeyboardSchema.load(model: fixture.Profile.model)
      try fixture.Profile.validate(schema: schema)
      XCTAssertEqual(
        try fixture.Profile.compileWrites(schema: schema).map(\.payload), fixture.Writes,
        fixture.Profile.model)
      try PreviewProfile.make(schema: schema).validate(schema: schema)
    }
  }
  func testAllWirelessWritePacketGoldens() throws {
    let f: Extended = try read("extended")
    XCTAssertEqual(f.Wireless.count, 60)
    for w in f.Wireless {
      XCTAssertEqual(
        try WirelessProtocol.writePackets(command: w.Command, data: w.Data, layer: w.Layer),
        w.Packets, "\(w.Command)/\(w.Layer)/\(w.Data.count)")
    }
    XCTAssertThrowsError(
      try WirelessProtocol.writePackets(command: 1, data: Data(repeating: 0, count: 3571)))
    XCTAssertThrowsError(try WirelessProtocol.writePackets(command: 5, data: Data([1])))
  }
  func testMacroBankPagesAndEventsMatchCSharp() throws {
    let f: Extended = try read("extended")
    let catalog = try FunctionCatalog.load()
    XCTAssertEqual(catalog.functions.count, 166)
    XCTAssertEqual(try MacroCodec.bank(f.Macros, catalog: catalog), f.Bank)
    XCTAssertEqual(try MacroCodec.pages(f.Bank), f.Pages)
    XCTAssertEqual(try MacroCodec.binding(.hold, index: 0), Data([3, 4, 1, 0]))
    XCTAssertEqual(try MacroCodec.binding(.toggle, index: 1), Data([3, 2, 1, 1]))
    XCTAssertEqual(try MacroCodec.binding(.once, index: 2), Data([3, 1, 1, 2]))
  }
  func testMacroInvalidCapacityNamesDelayAndKeys() throws {
    let c = try FunctionCatalog.load()
    for steps in [
      [], [MacroStep(key: "unknown", down: true)], [MacroStep(key: "a", down: true, delayMs: -1)],
      [MacroStep(key: "a", down: true, delayMs: 60001)],
      Array(repeating: MacroStep(key: "a", down: true), count: 15001),
    ] {
      XCTAssertThrowsError(try MacroCodec.events(steps, catalog: c))
    }
    let m = KeyboardMacro(name: "A", steps: [MacroStep(key: "a", down: true)])
    XCTAssertThrowsError(try MacroCodec.bank([m, m], catalog: c))
    XCTAssertThrowsError(try MacroCodec.binding(.once, index: 255))
    XCTAssertEqual(try MacroCodec.pages(Data(repeating: 1, count: 513)).map(\.count), [519, 8])
  }
  func testRecorderRepetitionOrphansReleaseAndCapacity() throws {
    let c = try FunctionCatalog.load()
    var r = MacroRecordingBuffer()
    XCTAssertFalse(try r.record(key: "a", down: false, elapsedMs: 0, catalog: c))
    XCTAssertTrue(try r.record(key: "a", down: true, elapsedMs: 1, catalog: c))
    XCTAssertFalse(try r.record(key: "a", down: true, elapsedMs: 2, catalog: c))
    try r.releaseHeld(elapsedMs: 70000, catalog: c)
    XCTAssertEqual(r.steps.count, 2)
    XCTAssertEqual(r.steps[0].delayMs, 60000)
    XCTAssertFalse(r.steps[1].down)
    r = MacroRecordingBuffer()
    for i in 0..<7499 {
      try r.record(key: "a", down: true, elapsedMs: i * 2, catalog: c)
      try r.record(key: "a", down: false, elapsedMs: i * 2 + 1, catalog: c)
    }
    try r.record(key: "a", down: true, elapsedMs: 14998, catalog: c)
    XCTAssertFalse(try r.record(key: "b", down: true, elapsedMs: 14999, catalog: c))
    try r.releaseHeld(elapsedMs: 15000, catalog: c)
    XCTAssertEqual(r.steps.count, 15000)
    XCTAssertTrue(r.capacityReached)
  }
  func testFactoryAndRemapPreserveReservedSlots() throws {
    let fixtures: [ModelFixture] = try read("all-models")
    let c = try FunctionCatalog.load()
    for f in fixtures {
      var p = f.Profile
      let schema = try KeyboardSchema.load(model: p.model)
      let reserved = schema.keyList.enumerated().filter {
        schema.reservedKeys.contains($0.element.n)
      }.map(\.offset)
      try p.restoreFactory(schema: schema)
      for layer in 0...2 {
        for i in reserved {
          XCTAssertEqual(
            p.keyLayers[layer].tables["#keyList"]?[i],
            f.Profile.keyLayers[layer].tables["#keyList"]?[i])
        }
      }
      if let function = c.functions.first(where: { $0.value.first == 13 }),
        let key = schema.board.first?.key
      {
        XCTAssertThrowsError(
          try p.remap(key: key, layer: 1, value: Data(function.value), schema: schema))
      }
    }
  }
  func testNamedProfileRoundtripAndTraversalRejected() throws {
    let root = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
      .appendingPathComponent(UUID().uuidString, isDirectory: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let library = ProfileLibrary(root: root)
    let schema = try KeyboardSchema.load()
    let p = try PreviewProfile.make(schema: schema)
    try library.save(name: "我的配置", profile: p)
    XCTAssertEqual(try library.load(model: "K87", name: "我的配置"), p)
    XCTAssertEqual(try library.list(model: "K87"), ["我的配置"])
    for name in ["../outside", "a/b", "CON", " test", "test.", "a\\b", ""] {
      XCTAssertThrowsError(try library.save(name: name, profile: p))
    }
    try library.delete(model: "K87", name: "我的配置")
    XCTAssertEqual(try library.list(model: "K87"), [])
  }
  func testWirelessModelMustBeConfirmed() throws {
    XCTAssertEqual(try WirelessProtocol.identify(Data([3, 0, 0, 0, 1, 0x35])), "K87")
    XCTAssertThrowsError(try WirelessProtocol.identify(Data(repeating: 0, count: 6)))
    XCTAssertThrowsError(try KeyboardSchema.resolve(product: "MCHOSE K870"))
    XCTAssertEqual(try KeyboardSchema.resolve(product: "MCHOSE K99 V2"), "K99_V2")
  }
  func testAudioFrameChecksumAndLegacyState() throws {
    let frame = try AudioProtocol.frame(
      type: Data([0, 2]), command: 3, payload: Data([1, 100, 5, 0]), check: true)
    XCTAssertEqual(Array(frame.prefix(12)), [0xAA, 1, 1, 5, 0, 2, 3, 1, 100, 5, 0, 101])
    XCTAssertTrue(AudioProtocol.parse(frame)?.valid == true)
    var broken = frame
    broken[7] ^= 1
    XCTAssertFalse(AudioProtocol.parse(broken)?.valid ?? true)
    XCTAssertNil(AudioProtocol.parse(frame.prefix(8)))
    XCTAssertThrowsError(try AudioProtocol.frame(type: Data([0]), command: 0))
    let state = try AudioProtocol.LegacyState(
      data: Data([0x11, 0x37, 2, 1, 50, 5, 0, 1, 100, 3, 1, 0, 1, 2, 3]))
    XCTAssertEqual(state.eq, 2)
    XCTAssertTrue(state.voice)
    XCTAssertTrue(state.mic)
    XCTAssertEqual(state.workMode, 3)
    XCTAssertEqual(state.color, [1, 2, 3])
  }
  func testWirelessSignatureHandlesNonzeroDataIndices() throws {
    let transport = PacketFake()
    let channel = WirelessChannel(transport: transport, checkCancellation: {})
    XCTAssertEqual(try channel.resolveModel().model, "K87")
    try channel.sendFeature(payload: ReadSection.battery.payload)
    let report = try channel.getFeature()
    XCTAssertEqual(report.count, 520)
    XCTAssertEqual(report[0], 19)
    XCTAssertEqual(report[8], 100)
    try WiredFrame.validate(report, for: .battery, reportID: 19)
  }
}

private final class PacketFake: PacketTransport {
  var incoming: [Data] = []
  func sendOutput(_ payload: Data) throws {
    var p = Data(repeating: 0, count: 19)
    p[0] = payload[0]
    p[1] = 1
    if payload[0] == 5 {
      p.replaceSubrange(4..<10, with: [3, 0, 0, 0, 1, 0x35])
    } else if payload[0] == 0x4A {
      p[4] = 100
    }
    // Mimic IOHID's ID prefix removal returning a Data slice with startIndex == 1.
    incoming.append((Data([19]) + p).dropFirst())
  }
  func nextPacket(timeout: TimeInterval) throws -> Data? {
    incoming.isEmpty ? nil : incoming.removeFirst()
  }
  func clearInbox() { incoming = [] }
}
