import Foundation

/// Shared by native transports so closing/reopening a session cannot bypass the write gap.
public final class WriteThrottle: @unchecked Sendable {
  public static let shared = WriteThrottle()
  private let lock = NSLock()
  private var last: TimeInterval?
  private init() {}
  public func waitForSlot() {
    lock.lock()
    defer { lock.unlock() }
    if let last {
      let delay = 1.5 - (ProcessInfo.processInfo.systemUptime - last)
      if delay > 0 { Thread.sleep(forTimeInterval: delay) }
    }
    last = ProcessInfo.processInfo.systemUptime
  }
}
