import AppKit
import SwiftUI

// New SDK builds use the system's Liquid Glass renderer. Older compilers and
// macOS 14–15 keep native material and bordered controls as a compatibility path.
extension View {
  @ViewBuilder func hubGlass(
    cornerRadius: CGFloat = 18, tint: Color? = nil, interactive: Bool = false
  ) -> some View {
    #if compiler(>=6.2)
      if #available(macOS 26.0, *) {
        self.glassEffect(
          .regular.tint(tint).interactive(interactive), in: .rect(cornerRadius: cornerRadius))
      } else {
        self.background(.regularMaterial, in: RoundedRectangle(cornerRadius: cornerRadius))
          .background(tint ?? .clear, in: RoundedRectangle(cornerRadius: cornerRadius))
      }
    #else
      self.background(.regularMaterial, in: RoundedRectangle(cornerRadius: cornerRadius))
        .background(tint ?? .clear, in: RoundedRectangle(cornerRadius: cornerRadius))
    #endif
  }

  @ViewBuilder func hubProminent() -> some View {
    #if compiler(>=6.2)
      if #available(macOS 26.0, *) {
        self.buttonStyle(.glassProminent)
      } else {
        self.buttonStyle(.borderedProminent)
      }
    #else
      self.buttonStyle(.borderedProminent)
    #endif
  }
}

struct HubGlassGroup<Content: View>: View {
  var spacing: CGFloat = 8
  @ViewBuilder var content: () -> Content
  var body: some View {
    #if compiler(>=6.2)
      if #available(macOS 26.0, *) {
        GlassEffectContainer(spacing: spacing) { content() }
      } else {
        content()
      }
    #else
      content()
    #endif
  }
}

struct HubSection<Content: View>: View {
  let title: String
  let subtitle: String
  let symbol: String
  @ViewBuilder let content: () -> Content
  init(
    _ title: String, subtitle: String = "", symbol: String = "",
    @ViewBuilder content: @escaping () -> Content
  ) {
    self.title = title
    self.subtitle = subtitle
    self.symbol = symbol
    self.content = content
  }
  var body: some View {
    VStack(alignment: .leading, spacing: 12) {
      if !title.isEmpty {
        VStack(alignment: .leading, spacing: 4) {
          Text(title).font(.headline)
          if !subtitle.isEmpty { Text(subtitle).font(.caption).foregroundStyle(.secondary) }
        }
      }
      content()
    }.frame(maxWidth: .infinity, alignment: .leading).textFieldStyle(.roundedBorder)
  }
}

@MainActor func openInputMonitoring() {
  if let url = URL(
    string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent")
  {
    NSWorkspace.shared.open(url)
  }
}

struct HubSettings: View {
  @Bindable var model: AppModel
  var body: some View {
    Form {
      Section("外观") {
        Picker("主题", selection: $model.theme) {
          Text("跟随系统").tag("system")
          Text("浅色").tag("light")
          Text("深色").tag("dark")
        }
      }
      Section("设备访问") {
        LabeledContent("输入监控") { Button("系统设置…") { openInputMonitoring() } }
        Text("独立预览与本地编辑无需访问设备。读取键盘配置需要输入监控权限。")
          .font(.caption).foregroundStyle(.secondary)
      }
      Section("快捷键") {
        LabeledContent("刷新配置", value: "⌘R")
        LabeledContent("导出草稿", value: "⌘S")
      }
    }.formStyle(.grouped).frame(width: 480, height: 410)
      .preferredColorScheme(model.theme == "dark" ? .dark : model.theme == "light" ? .light : nil)
  }
}

@MainActor final class HubAppDelegate: NSObject, NSApplicationDelegate {
  weak var model: AppModel?
  func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
  func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
    guard let model else { return .terminateNow }
    if model.busy || model.recording {
      let alert = NSAlert()
      alert.messageText = "请先完成当前操作"
      alert.informativeText = "设备操作或宏录制正在进行。完成后再退出，以保留完整结果。"
      alert.addButton(withTitle: "返回应用")
      alert.runModal()
      return .terminateCancel
    }
    guard model.hasUnsavedChanges || model.hasUnsavedMacros else { return .terminateNow }
    let alert = NSAlert()
    alert.messageText = "退出前保存修改？"
    alert.informativeText = "配置草稿或宏库还有未保存的修改。可以返回应用导出，也可以放弃修改后退出。"
    alert.addButton(withTitle: "返回应用")
    alert.addButton(withTitle: "放弃修改并退出")
    return alert.runModal() == .alertSecondButtonReturn ? .terminateNow : .terminateCancel
  }
}

struct HubFormRow<Content: View>: View {
  let title: String
  @ViewBuilder var content: () -> Content
  var body: some View {
    HStack(alignment: .center, spacing: 16) {
      Text(title).frame(width: 104, alignment: .leading)
      content().labelsHidden().frame(maxWidth: 340, alignment: .leading)
      Spacer(minLength: 0)
    }.frame(minHeight: 32)
  }
}
