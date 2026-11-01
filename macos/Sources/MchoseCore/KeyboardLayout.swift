import Foundation

/// Physical key geometry shared by the remapping and per-key lighting editors.
/// Matches the Windows client's BuildRemapBoard / BuildDiyBoard layout rules.
public struct KeyboardLayout: Sendable {
  public struct Placement: Sendable, Identifiable {
    public let key: BoardKey
    public let x: Double
    public let y: Double
    public let width: Double
    public let height: Double
    public var id: String { key.id }
  }

  public let placements: [Placement]
  public let width: Double
  public let height: Double

  public init(keys: [BoardKey]) {
    let rowStarts: Set<String> = ["backquote", "Tab", "Caps", "Left_Shift", "Left_Ctrl"]
    let unit = 38.0
    let gap = 4.0
    var row = 0.0
    var cursor = 0.0
    var placements: [Placement] = []
    var width = 0.0
    var height = 0.0
    for key in keys {
      // Row starts can have a positive left inset (X75/G75), so margin zero
      // cannot identify rows. Negative top margins compensate for tall keys
      // in the source web flow; Windows places these semantic rows directly.
      if rowStarts.contains(key.key) {
        row += 1
        cursor = 0
      }
      cursor += Double(key.left) / 46
      let slotWidth = Double(key.width + 4) / 46
      let placement = Placement(
        key: key, x: cursor * unit, y: row * unit,
        width: slotWidth * unit - gap, height: Double(key.height) / 42 * unit - gap)
      placements.append(placement)
      width = max(width, (cursor + slotWidth) * unit)
      height = max(height, placement.y + placement.height)
      cursor += slotWidth + Double(key.right) / 46
    }
    self.placements = placements
    self.width = width
    self.height = height
  }
}
