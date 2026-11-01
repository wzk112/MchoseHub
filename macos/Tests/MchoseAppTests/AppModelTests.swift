import Observation
import XCTest

@testable import MchoseApp
@testable import MchoseCore
@testable import MchoseHID

final class AppModelTests: XCTestCase {
  @MainActor func testMacroBindingRejectsEmptyLibraryAndReservedKeys() async throws {
    let model = AppModel(scanner: { [] })
    let schema = try XCTUnwrap(model.schema)
    model.selectedKey = schema.board.first { !schema.reservedKeys.contains($0.key) }?.key
    XCTAssertTrue(model.canBindMacro)
    let macros = model.macros
    model.macros = []
    XCTAssertFalse(model.canBindMacro)
    model.macros = macros
    model.selectedKey = try XCTUnwrap(schema.reservedKeys.first)
    XCTAssertFalse(model.canBindMacro)
  }

  @MainActor func testReselectingLocalSidebarKeepsEditedDraftAndGeneration() async {
    let model = AppModel(scanner: { [] })
    model.setNumber("brightness1", 42)
    let generation = model.generation
    model.chooseSidebar(.local)
    XCTAssertEqual(model.generation, generation)
    XCTAssertEqual(model.number("brightness1"), 42)
    XCTAssertTrue(model.hasUnsavedChanges)
  }

  @MainActor func testUnchangedScanDoesNotInvalidateSidebar() async {
    final class Probe: @unchecked Sendable {
      private let lock = NSLock()
      private var count = 0
      func mark() { lock.withLock { count += 1 } }
      var value: Int { lock.withLock { count } }
    }
    let model = AppModel(scanner: { [] })
    let probe = Probe()
    withObservationTracking {
      _ = model.devices
      _ = model.scanMessage
    } onChange: {
      probe.mark()
    }
    await model.scanDevices()
    await model.scanDevices()
    XCTAssertEqual(probe.value, 0)
  }

  @MainActor func testEditedDraftSurvivesDisconnectAndCancelsSession() async throws {
    let model = AppModel(scanner: { [] })
    let token = model.token
    let generation = model.generation
    model.selection = 123
    model.previewMode = false
    model.captured = DeviceBackup(profile: try XCTUnwrap(model.profile), battery: 75, exchanges: [])
    model.setNumber("brightness1", 42)
    XCTAssertTrue(model.hasDeviceChanges)
    model.disconnect()
    XCTAssertThrowsError(try token.check())
    XCTAssertNotEqual(model.generation, generation)
    XCTAssertNil(model.selection)
    XCTAssertNil(model.captured)
    XCTAssertEqual(model.number("brightness1"), 42)
    XCTAssertTrue(model.hasUnsavedChanges)
    XCTAssertFalse(model.hasDeviceChanges)
    XCTAssertFalse(model.previewMode)
    XCTAssertTrue(model.canEdit)
  }

  @MainActor func testSavingDraftDoesNotMarkItAppliedToDevice() async throws {
    let model = AppModel(scanner: { [] })
    model.captured = DeviceBackup(
      profile: try XCTUnwrap(model.profile), battery: nil, exchanges: [])
    model.setNumber("brightness1", 42)
    model.savedProfile = model.profile
    XCTAssertFalse(model.hasUnsavedChanges)
    XCTAssertTrue(model.hasDeviceChanges)
  }

  @MainActor func testDetachedDraftKeepsUnsavedStatusAndLibraryKind() async {
    let model = AppModel(scanner: { [] })
    model.previewMode = false
    model.selection = 123
    model.setNumber("brightness1", 42)
    model.makeDraft()
    XCTAssertNil(model.selection)
    XCTAssertTrue(model.hasUnsavedChanges)
    XCTAssertFalse(model.previewMode)
    XCTAssertEqual(model.number("brightness1"), 42)
  }

  @MainActor func testReturningToK87PreviewResetsImportedModel() async throws {
    let model = AppModel(scanner: { [] })
    model.schema = try KeyboardSchema.load(model: "Z75")
    model.profile = try PreviewProfile.make(schema: XCTUnwrap(model.schema))
    model.savedProfile = model.profile
    model.preview()
    XCTAssertEqual(model.schema?.model, "K87")
    XCTAssertEqual(model.profile?.model, "K87")
    XCTAssertFalse(model.hasUnsavedChanges)
  }

  @MainActor func testBusyAndRecordingBlockDraftMutation() async {
    let model = AppModel(scanner: { [] })
    let initial = model.profile
    model.busy = true
    model.setNumber("brightness1", 42)
    model.makeDraft()
    XCTAssertEqual(model.profile, initial)
    model.busy = false
    model.recording = true
    model.setNumber("brightness1", 42)
    XCTAssertEqual(model.profile, initial)
    XCTAssertFalse(model.canEdit)
  }

  @MainActor func testScanFailureCanRecoverWithoutDroppingDraft() async throws {
    actor Scanner {
      var calls = 0
      func scan() throws -> [HIDDeviceInfo] {
        calls += 1
        if calls <= 2 { throw MchoseError.invalid("temporary failure") }
        return []
      }
    }
    let scanner = Scanner()
    let model = AppModel(scanner: { try await scanner.scan() })
    model.selection = 123
    model.setNumber("brightness1", 42)
    await model.scanDevices()
    XCTAssertNotNil(model.scanMessage)
    XCTAssertEqual(model.selection, 123)  // A failed scan is not evidence of a disconnect.
    await model.scanDevices()
    XCTAssertEqual(model.log.count, 1)  // Repeated failure does not flood the log.
    await model.scanDevices()
    XCTAssertNil(model.scanMessage)
    XCTAssertNil(model.selection)
    XCTAssertEqual(model.number("brightness1"), 42)
    XCTAssertTrue(model.hasUnsavedChanges)
  }
}
