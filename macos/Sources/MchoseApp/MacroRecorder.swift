import AppKit
import MchoseCore
import SwiftUI

struct MacroRecorder: NSViewRepresentable {
  @Binding var recording: Bool
  let completed: ([MacroStep]) -> Void
  func makeNSView(context: Context) -> RecorderView { RecorderView() }
  func updateNSView(_ view: RecorderView, context: Context) {
    view.completed = completed
    view.stop = { recording = false }
    if recording != view.recording {
      if recording {
        view.begin()
        view.window?.makeFirstResponder(view)
      } else {
        view.finish()
      }
    }
  }
  static func dismantleNSView(_ view: RecorderView, coordinator: ()) { view.finish() }
}

@MainActor final class RecorderView: NSView {
  var completed: ([MacroStep]) -> Void = { _ in }
  var stop: () -> Void = {}
  private(set) var recording = false
  private var buffer = MacroRecordingBuffer()
  private let catalog = try? FunctionCatalog.load()
  private var start: TimeInterval = 0
  private var heldModifiers = Set<UInt16>()
  private var windowObserver: (any NSObjectProtocol)?
  override var acceptsFirstResponder: Bool { true }
  func begin() {
    buffer = MacroRecordingBuffer()
    heldModifiers = []
    start = ProcessInfo.processInfo.systemUptime
    recording = true
    needsDisplay = true
  }
  func finish() {
    guard recording else { return }
    if let catalog { try? buffer.releaseHeld(elapsedMs: elapsed, catalog: catalog) }
    recording = false
    needsDisplay = true
    completed(buffer.steps)
  }
  private var elapsed: Int { max(0, Int((ProcessInfo.processInfo.systemUptime - start) * 1000)) }
  override func resignFirstResponder() -> Bool {
    if recording {
      finish()
      stop()
    }
    return true
  }
  override func viewWillMove(toWindow newWindow: NSWindow?) {
    if newWindow == nil {
      finish()
      stop()
    }
    super.viewWillMove(toWindow: newWindow)
  }
  override func viewDidMoveToWindow() {
    if let windowObserver { NotificationCenter.default.removeObserver(windowObserver) }
    if let window {
      windowObserver = NotificationCenter.default.addObserver(
        forName: NSWindow.didResignKeyNotification, object: window, queue: .main
      ) { [weak self] _ in
        Task { @MainActor [weak self] in
          self?.finish()
          self?.stop()
        }
      }
    } else {
      windowObserver = nil
    }
  }
  override func mouseDown(with event: NSEvent) { window?.makeFirstResponder(self) }
  override func keyDown(with event: NSEvent) { record(event, down: true) }
  override func keyUp(with event: NSEvent) { record(event, down: false) }
  override func performKeyEquivalent(with event: NSEvent) -> Bool {
    if recording {
      record(event, down: event.type != .keyUp)
      return true
    }
    return false
  }
  override func flagsChanged(with event: NSEvent) {
    let mask: NSEvent.ModifierFlags
    switch event.keyCode {
    case 56, 60: mask = .shift
    case 59, 62: mask = .control
    case 58, 61: mask = .option
    case 54, 55: mask = .command
    default: return
    }
    let down = !heldModifiers.contains(event.keyCode) && event.modifierFlags.contains(mask)
    if down { heldModifiers.insert(event.keyCode) } else { heldModifiers.remove(event.keyCode) }
    record(event, down: down)
  }
  private func record(_ event: NSEvent, down: Bool) {
    guard recording, let catalog, let key = Self.keys[event.keyCode] else { return }
    do {
      try buffer.record(key: key, down: down, elapsedMs: elapsed, catalog: catalog)
      if buffer.capacityReached {
        finish()
        stop()
      }
      needsDisplay = true
    } catch {
      finish()
      stop()
    }
  }
  override func draw(_ dirtyRect: NSRect) {
    NSColor.controlBackgroundColor.setFill()
    NSBezierPath(roundedRect: bounds, xRadius: 8, yRadius: 8).fill()
    let text = recording ? "正在录制 · \(buffer.steps.count) 个事件 · 点击其他区域结束" : "点击此区域获得录制焦点"
    (text as NSString).draw(
      at: NSPoint(x: 16, y: bounds.midY - 7),
      withAttributes: [.font: NSFont.systemFont(ofSize: 13), .foregroundColor: NSColor.labelColor])
  }
  static let keys: [UInt16: String] = [
    0: "a", 1: "s", 2: "d", 3: "f", 4: "h", 5: "g", 6: "z", 7: "x", 8: "c", 9: "v", 11: "b",
    12: "q", 13: "w", 14: "e", 15: "r", 16: "y", 17: "t", 18: "1", 19: "2", 20: "3", 21: "4",
    22: "6", 23: "5", 24: "=", 25: "9", 26: "7", 27: "-", 28: "8", 29: "0", 30: "]", 31: "o",
    32: "u", 33: "[", 34: "i", 35: "p", 36: "Enter", 37: "l", 38: "j", 39: "'", 40: "k", 41: ";",
    42: "\\", 43: ",", 44: "/", 45: "n", 46: "m", 47: ".", 48: "Tab", 49: "Space", 50: "`",
    51: "Backspace", 53: "Esc", 54: "WinRight", 55: "WinLeft", 56: "ShiftLeft", 57: "CapsLock",
    58: "AltLeft", 59: "ControlLeft", 60: "ShiftRight", 61: "AltRight", 62: "ControlRight",
    65: "NumpadDecimal", 67: "NumpadMultiply", 69: "NumpadAdd", 71: "NumLock", 75: "NumpadDivide",
    76: "NumpadEnter", 78: "NumpadSubtract", 82: "Numpad0", 83: "Numpad1", 84: "Numpad2",
    85: "Numpad3", 86: "Numpad4", 87: "Numpad5", 88: "Numpad6", 89: "Numpad7", 91: "Numpad8",
    92: "Numpad9", 96: "F5", 97: "F6", 98: "F7", 99: "F3", 100: "F8", 101: "F9", 103: "F11",
    109: "F10", 111: "F12", 114: "Insert", 115: "Home", 116: "PageUp", 117: "Delete", 118: "F4",
    119: "End", 120: "F2", 121: "PageDown", 122: "F1", 123: "ArrowLeft", 124: "ArrowRight",
    125: "ArrowDown", 126: "ArrowUp",
  ]
}
