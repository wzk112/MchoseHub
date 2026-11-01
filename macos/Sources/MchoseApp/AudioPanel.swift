import AppKit
import MchoseCore
import MchoseHID
import SwiftUI

struct AudioPanel: View {
  let device: HIDDeviceInfo
  @Binding var busy: Bool
  @State private var result: AudioResult?
  @State private var error: String?
  @State private var mode = 0
  @State private var brightness = 50.0
  @State private var cycle = 5.0
  @State private var eq = 0
  @State private var voice = false
  @State private var color = Color.white
  @State private var reportID = "BC"
  @State private var hex = "11 00"
  @State private var token = CancellationToken()
  @State private var requestID = UUID()
  @State private var isVisible = false
  @State private var diagnosticsExpanded = false
  private let service = AudioService()

  var body: some View {
    VStack(alignment: .leading, spacing: 28) {
      statusSection
      lightingSection
      soundSection
      diagnosticsSection
    }
    .textFieldStyle(.roundedBorder)
    .disabled(busy)
    .onAppear { isVisible = true }
    .onDisappear {
      isVisible = false
      requestID = UUID()
      token.cancel()
      busy = false
    }
    .alert("操作未完成", isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
      Button("好") { error = nil }
    } message: {
      Text(error ?? "")
    }
  }

  private var statusSection: some View {
    HubSection("设备状态", symbol: "headphones") {
      HStack(spacing: 12) {
        Label(statusText, systemImage: result == nil ? "circle.dashed" : "checkmark.circle")
          .font(.callout).foregroundStyle(.secondary)
        Spacer(minLength: 12)
        if busy { ProgressView().controlSize(.small) }
        Button("读取状态", systemImage: "arrow.clockwise") { run(.read) }
        if device.reports.contains(where: { $0.type == "output" && $0.id == 0x55 }) {
          Button("耳机信息") {
            run(.raw(id: 0x55, payload: Data([0x65, 1]) + Data(repeating: 0, count: 61)))
          }
        }
      }
      if let state = result?.state {
        Grid(alignment: .leading, horizontalSpacing: 24, verticalSpacing: 6) {
          GridRow {
            stateValue("工作模式", value: String(state.workMode))
            stateValue("EQ 预设", value: String(state.eq))
            stateValue("扬声器", value: state.speaker ? "开启" : "关闭")
            stateValue("麦克风", value: state.mic ? "开启" : "关闭")
          }
        }
      } else if result?.structured == true {
        Text("当前设备返回第二代协议帧，暂仅提供原始回包，尚不能解析状态字段。")
          .font(.caption).foregroundStyle(.secondary)
      }
    }
  }

  private var lightingSection: some View {
    HubSection("灯光", subtitle: "调整后点击应用，将当前灯效、亮度、周期和颜色写入设备。", symbol: "lightbulb") {
      HubFormRow(title: "灯光效果") {
        Picker("灯光效果", selection: $mode) {
          Text("常亮").tag(0)
          Text("呼吸").tag(1)
          Text("流光").tag(2)
          Text("音乐律动").tag(3)
          Text("关闭").tag(4)
          if !(0...4).contains(mode) { Text("设备值 \(mode)").tag(mode) }
        }
      }
      HubFormRow(title: "亮度") {
        HStack(spacing: 12) {
          Slider(value: $brightness, in: 0...100)
            .accessibilityLabel("亮度")
          Text("\(Int(brightness.rounded()))")
            .monospacedDigit().foregroundStyle(.secondary).frame(width: 30, alignment: .trailing)
        }
      }
      HubFormRow(title: "变化周期") {
        HStack(spacing: 12) {
          Slider(value: $cycle, in: 0...255)
            .accessibilityLabel("变化周期")
          Text("\(Int(cycle.rounded()))")
            .monospacedDigit().foregroundStyle(.secondary).frame(width: 30, alignment: .trailing)
        }
      }
      HubFormRow(title: "目标颜色") {
        ColorPicker("目标颜色", selection: $color, supportsOpacity: false)
      }
      HStack {
        Spacer()
        Button("应用灯光…") { applyLighting() }.hubProminent()
      }
    }
  }

  private var soundSection: some View {
    HubSection("声音", symbol: "speaker.wave.2") {
      HubFormRow(title: "EQ 预设") {
        HStack(spacing: 12) {
          Text("\(eq)").monospacedDigit().frame(minWidth: 34, alignment: .leading)
          Stepper("EQ 预设", value: $eq, in: 0...255).labelsHidden().fixedSize()
          Spacer(minLength: 0)
          Button("应用…") { run(.eq(UInt8(eq)), confirm: "设置设备 EQ 预设？") }
            .accessibilityLabel("应用 EQ 预设")
        }
      }
      HubFormRow(title: "语音播报") {
        HStack(spacing: 12) {
          Toggle("语音播报", isOn: $voice).toggleStyle(.switch)
          Spacer(minLength: 0)
          Button("应用…") { run(.voice(voice), confirm: "修改语音播报？") }
            .accessibilityLabel("应用语音播报设置")
        }
      }
    }
  }

  private var diagnosticsSection: some View {
    DisclosureGroup("高级与诊断", isExpanded: $diagnosticsExpanded) {
      VStack(alignment: .leading, spacing: 20) {
        HubSection("手工报告", subtitle: "输入十六进制报文，发送前会再次确认。") {
          HStack(alignment: .top, spacing: 12) {
            VStack(alignment: .leading, spacing: 6) {
              Text("报告 ID").font(.caption).foregroundStyle(.secondary)
              TextField("BC", text: $reportID).font(.system(.body, design: .monospaced))
                .frame(width: 72).accessibilityLabel("报告 ID，十六进制")
            }
            VStack(alignment: .leading, spacing: 6) {
              Text("Payload").font(.caption).foregroundStyle(.secondary)
              TextField("11 00", text: $hex, axis: .vertical)
                .font(.system(.body, design: .monospaced)).lineLimit(2...5)
                .accessibilityLabel("十六进制 payload")
            }
            Button("发送…") { raw() }.padding(.top, 22)
          }
        }
        if let result {
          HubSection("原始回包", subtitle: "\(result.reports.count) 份报告") {
            if result.reports.isEmpty {
              Text("设备未返回报告。").foregroundStyle(.secondary)
            } else {
              ScrollView([.horizontal, .vertical]) {
                Text(reportText(result))
                  .font(.system(.caption, design: .monospaced))
                  .textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
              }.frame(height: 140)
            }
          }
        }
        Divider()
        HStack {
          VStack(alignment: .leading, spacing: 4) {
            Text("恢复设备默认").font(.callout)
            Text("清除音频设备当前设置。")
              .font(.caption).foregroundStyle(.secondary)
          }
          Spacer()
          Button("恢复默认…", role: .destructive) {
            run(.restore, confirm: "恢复音频设备默认设置？此操作会清除设备设置。")
          }
        }
        Text("C-Media / THX 内核 DSP、APO 效果尚未包含在当前 Windows 客户端的 HID 功能中。")
          .font(.caption).foregroundStyle(.secondary)
      }.padding(.top, 16)
    }
  }

  private var statusText: String {
    if busy { return "正在与设备通信…" }
    guard let result else { return "尚未读取状态" }
    if result.state != nil { return "已读取第一代设备状态" }
    if result.structured { return "已收到第二代协议帧" }
    return result.reports.isEmpty ? "请求已发送，未收到回包" : "已收到 \(result.reports.count) 份原始回包"
  }

  private func stateValue(_ title: String, value: String) -> some View {
    VStack(alignment: .leading, spacing: 5) {
      Text(title).font(.caption).foregroundStyle(.secondary)
      Text(value).font(.callout.weight(.medium)).monospacedDigit()
    }.frame(maxWidth: .infinity, alignment: .leading)
  }

  private func reportText(_ result: AudioResult) -> String {
    result.reports.enumerated().map { index, report in
      "\(index + 1)  " + report.map { String(format: "%02X", $0) }.joined(separator: " ")
    }.joined(separator: "\n")
  }

  private func applyLighting() {
    guard let c = NSColor(color).usingColorSpace(.sRGB) else { return }
    func channel(_ value: CGFloat) -> UInt8 { UInt8(clamping: Int((value * 255).rounded())) }
    run(
      .lighting(
        mode: UInt8(clamping: mode), brightness: UInt8(clamping: Int(brightness.rounded())),
        cycle: UInt8(clamping: Int(cycle.rounded())), index: 0,
        r: channel(c.redComponent), g: channel(c.greenComponent), b: channel(c.blueComponent)),
      confirm: "应用音频设备灯光？")
  }

  private func raw() {
    do {
      let idText = reportID.trimmingCharacters(in: .whitespacesAndNewlines)
        .lowercased().replacingOccurrences(of: "0x", with: "")
      guard let id = UInt8(idText, radix: 16) else {
        throw MchoseError.invalid("Report ID 无效。")
      }
      let payload = try hex.split(whereSeparator: { " ,\t\n-".contains($0) }).map { part -> UInt8 in
        guard
          let value = UInt8(part.lowercased().replacingOccurrences(of: "0x", with: ""), radix: 16)
        else {
          throw MchoseError.invalid("十六进制格式无效。")
        }
        return value
      }
      guard !payload.isEmpty else { throw MchoseError.invalid("请输入要发送的 payload。") }
      run(.raw(id: id, payload: Data(payload)), confirm: "发送手工报告？仅在了解协议和报文影响时使用。")
    } catch { self.error = error.localizedDescription }
  }

  private func run(_ action: AudioAction, confirm: String? = nil) {
    guard !busy else { return }
    if let confirm {
      let alert = NSAlert()
      alert.messageText = confirm
      alert.addButton(withTitle: "继续")
      alert.addButton(withTitle: "取消")
      guard alert.runModal() == .alertFirstButtonReturn else { return }
    }
    token = CancellationToken()
    let current = token
    let operationID = UUID()
    requestID = operationID
    error = nil
    busy = true
    Task {
      do {
        let value = try await service.perform(deviceID: device.id, action: action, token: current)
        try current.check()
        guard isVisible, requestID == operationID else { return }
        result = value
        if let state = value.state {
          mode = state.lightMode
          brightness = Double(state.brightness)
          cycle = Double(state.cycle)
          eq = state.eq
          voice = state.voice
        }
        if value.state == nil { diagnosticsExpanded = true }
      } catch let failure {
        guard isVisible, requestID == operationID else { return }
        result = nil
        if (try? current.check()) != nil { error = failure.localizedDescription }
      }
      guard isVisible, requestID == operationID else { return }
      busy = false
    }
  }
}
