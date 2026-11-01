import XCTest

@testable import MchoseCore

final class KeyboardLayoutTests: XCTestCase {
  func testAllModelKeysHaveUniqueNonoverlappingFramesInsideCanvas() throws {
    let models = try KeyboardSchema.availableModels()
    XCTAssertEqual(models.count, 18)
    for model in models {
      let schema = try KeyboardSchema.load(model: model)
      let layout = KeyboardLayout(keys: schema.board)
      XCTAssertEqual(layout.placements.map(\.id), schema.board.map(\.id), model)
      XCTAssertEqual(Set(layout.placements.map(\.id)).count, schema.board.count, model)
      XCTAssertLessThan(layout.width, 900, "\(model) should not flow into one long row")
      XCTAssertGreaterThanOrEqual(layout.height, 224, model)
      XCTAssertLessThan(layout.height, 230, model)
      for (index, item) in layout.placements.enumerated() {
        XCTAssertGreaterThan(item.width, 0, "\(model): \(item.id)")
        XCTAssertGreaterThan(item.height, 0, "\(model): \(item.id)")
        XCTAssertGreaterThanOrEqual(item.x, 0, model)
        XCTAssertGreaterThanOrEqual(item.y, 0, model)
        XCTAssertLessThanOrEqual(item.x + item.width, layout.width + 0.001, model)
        XCTAssertLessThanOrEqual(item.y + item.height, layout.height + 0.001, model)
        for other in layout.placements.dropFirst(index + 1) {
          let overlapWidth = min(item.x + item.width, other.x + other.width) - max(item.x, other.x)
          let overlapHeight =
            min(item.y + item.height, other.y + other.height) - max(item.y, other.y)
          XCTAssertFalse(
            overlapWidth > 0.001 && overlapHeight > 0.001,
            "\(model): \(item.id) overlaps \(other.id)")
        }
      }
    }
  }

  func testInsetRowStartsAndAuxiliaryControlsDoNotChangeRowNumber() throws {
    for model in ["X75", "X75_V2", "G75_Pro", "G75_V2"] {
      let layout = KeyboardLayout(keys: try KeyboardSchema.load(model: model).board)
      let keys = Dictionary(uniqueKeysWithValues: layout.placements.map { ($0.id, $0) })
      let starts = ["Esc", "backquote", "Tab", "Caps", "Left_Shift", "Left_Ctrl"]
      for (row, name) in starts.enumerated() {
        let key = try XCTUnwrap(keys[name])
        XCTAssertEqual(key.y, Double(row * 38), accuracy: 0.001, "\(model): \(name)")
        XCTAssertGreaterThan(key.x, 0, "\(model) has an inset at each row start")
      }
    }
    let x75 = KeyboardLayout(keys: try KeyboardSchema.load(model: "X75").board)
    XCTAssertEqual(try XCTUnwrap(x75.placements.first).x, 52.8695652173913, accuracy: 0.001)
    let g75 = KeyboardLayout(keys: try KeyboardSchema.load(model: "G75_V2").board)
    let wheel = try XCTUnwrap(g75.placements.first { $0.id == "circle_volumeRoll" })
    XCTAssertEqual(wheel.y, 152, accuracy: 0.001)
    XCTAssertEqual(wheel.height, 28.5714285714286, accuracy: 0.001)
  }

  func testDoubleHeightNumpadKeysDoNotCollapseFollowingRows() throws {
    for model in ["K99", "K99_V2", "G98", "G98_Pro", "G98_V2", "UT98"] {
      let layout = KeyboardLayout(keys: try KeyboardSchema.load(model: model).board)
      let keys = Dictionary(uniqueKeysWithValues: layout.placements.map { ($0.id, $0) })
      let plus = try XCTUnwrap(keys["num_add"])
      let enter = try XCTUnwrap(keys["num_Enter"])
      XCTAssertEqual(plus.y, 76, accuracy: 0.001, model)
      XCTAssertEqual(enter.y, 152, accuracy: 0.001, model)
      XCTAssertEqual(plus.height, 75.6190476190476, accuracy: 0.001, model)
      XCTAssertEqual(enter.height, plus.height, accuracy: 0.001, model)
      XCTAssertEqual(try XCTUnwrap(keys["Caps"]).y, 114, accuracy: 0.001, model)
      XCTAssertEqual(try XCTUnwrap(keys["Left_Ctrl"]).y, 190, accuracy: 0.001, model)
      XCTAssertEqual(layout.height, 227.6190476190476, accuracy: 0.001, model)
    }
  }

  func testK87AnchorsMatchWindowsRemappingAndLightingGeometry() throws {
    let layout = KeyboardLayout(keys: try KeyboardSchema.load(model: "K87").board)
    let keys = Dictionary(uniqueKeysWithValues: layout.placements.map { ($0.id, $0) })
    XCTAssertEqual(layout.placements.count, 87)
    XCTAssertEqual(layout.width, 746.782608695652, accuracy: 0.001)
    XCTAssertEqual(layout.height, 224, accuracy: 0.001)
    let escape = try XCTUnwrap(keys["Esc"])
    XCTAssertEqual(escape.x, 0)
    XCTAssertEqual(escape.y, 0)
    XCTAssertEqual(escape.width, 34)
    XCTAssertEqual(escape.height, 34)
    let space = try XCTUnwrap(keys["Space"])
    XCTAssertEqual(space.x, 153.6521739130435, accuracy: 0.001)
    XCTAssertEqual(space.y, 190)
    XCTAssertEqual(space.width, 230.6086956521739, accuracy: 0.001)
    XCTAssertEqual(try XCTUnwrap(keys["right"]).x, 688.9565217391303, accuracy: 0.001)
  }
}
