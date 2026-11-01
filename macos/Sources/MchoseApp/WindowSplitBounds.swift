import AppKit
import SwiftUI

/// Keeps the native split view within the width proposed by the window's root
/// GeometryReader. Place this background inside a NavigationSplitView column.
///
/// A constant is intentional: linking to the window's width anchor lets the
/// split view's temporary animation size enlarge the window through Auto Layout.
@MainActor
struct WindowSplitBounds: NSViewRepresentable {
  let viewportWidth: CGFloat

  func makeNSView(context: Context) -> Probe {
    Probe()
  }

  func updateNSView(_ view: Probe, context: Context) {
    view.requestedWidth = viewportWidth
    view.apply()
  }

  static func dismantleNSView(_ view: Probe, coordinator: ()) {
    view.invalidate()
  }

  final class Probe: NSView {
    var requestedWidth: CGFloat = 0
    private weak var boundSplit: NSSplitView?
    private var widthConstraint: NSLayoutConstraint?
    private var isInvalidated = false

    override func viewDidMoveToSuperview() {
      super.viewDidMoveToSuperview()
      apply()
    }

    override func viewDidMoveToWindow() {
      super.viewDidMoveToWindow()
      apply()
    }

    func apply() {
      guard !isInvalidated else { return }
      guard window != nil else {
        detach()
        return
      }
      guard requestedWidth.isFinite, requestedWidth > 0 else { return }

      var ancestor = superview
      var nearestSplit: NSSplitView?
      while let view = ancestor {
        if let split = view as? NSSplitView {
          nearestSplit = split
          break
        }
        ancestor = view.superview
      }
      guard let split = nearestSplit else {
        detach()
        return
      }

      if boundSplit !== split {
        detach()
        let constraint = split.widthAnchor.constraint(equalToConstant: requestedWidth)
        constraint.identifier = "MchoseHub.WindowViewportWidth"
        boundSplit = split
        widthConstraint = constraint
        constraint.isActive = true
      } else if widthConstraint?.constant != requestedWidth {
        widthConstraint?.constant = requestedWidth
      }
    }

    func invalidate() {
      isInvalidated = true
      detach()
    }

    private func detach() {
      widthConstraint?.isActive = false
      widthConstraint = nil
      boundSplit = nil
    }
  }
}
