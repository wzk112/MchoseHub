import AppKit
import MchoseCore
import MchoseHID
import SwiftUI

struct AudioPanel: View {
  let device: HIDDeviceInfo
  @State var result: AudioResult?
  @State var busy = false
  @State var error: String?
  @State var mode = 0
  @State var brightness = 50.0
  @State var cycle = 5.0
  @State var eq = 0
  @State var voice = false
  @State var color = Color.white
  @State var reportID = "BC"
  @State var hex = "11 00"
  @State var token = CancellationToken()
  let service = AudioService()
  var body: some View {
    VStack(alignment: .leading, spacing: 18) {
      Text(device.product).font(.largeTitle.bold())
      Button("读取状态") { run(.read) }.disabled(busy)
      if device.reports.contains(where: { $0.type == "output" && $0.id == 0x55 }) {
        Button("读取耳机信息") {
          run(.raw(id: 0x55, payload: Data([0x65, 1]) + Data(repeating: 0, count: 61)))
        }.disabled(busy)
      }
      if busy { ProgressView() }
      if let result {
        Text(result.structured ? "第二代结构化帧；状态字段布局与 Windows 版一样尚未实现，显示原始回包。" : "第一代音频 HID 协议")
        if let state = result.state {
          Text(
            "工作模式 \(state.workMode) · EQ \(state.eq) · 扬声器 \(state.speaker ? "开" : "关") · 麦克风 \(state.mic ? "开" : "关")"
          )
        }
      }
      Picker("灯效", selection: $mode) {
        Text("常亮").tag(0)
        Text("呼吸").tag(1)
        Text("流光").tag(2)
        Text("音乐律动").tag(3)
        Text("关闭").tag(4)
      }
      Text("亮度：\(Int(brightness))")
      Slider(value: $brightness, in: 0...100, step: 1)
      Text("周期：\(Int(cycle))")
      Slider(value: $cycle, in: 0...255, step: 1)
      ColorPicker("颜色", selection: $color, supportsOpacity: false)
      Button("应用灯光…") {
        guard let c = NSColor(color).usingColorSpace(.sRGB) else { return }
        run(
          .lighting(
            mode: UInt8(mode), brightness: UInt8(brightness), cycle: UInt8(cycle), index: 0,
            r: UInt8(c.redComponent * 255), g: UInt8(c.greenComponent * 255),
            b: UInt8(c.blueComponent * 255)), confirm: "应用音频设备灯光？")
      }.disabled(busy)
      HStack {
        Stepper("EQ 预设：\(eq)", value: $eq, in: 0...255)
        Button("设置 EQ…") { run(.eq(UInt8(eq)), confirm: "设置设备 EQ 预设？") }
        Toggle("语音播报", isOn: $voice)
        Button("设置播报…") { run(.voice(voice), confirm: "修改语音播报？") }
      }
      Button("恢复设备默认…") { run(.restore, confirm: "恢复音频设备默认设置？此操作会清除设备设置。") }.disabled(busy)
      DisclosureGroup("诊断 · 手工 output 报告") {
        HStack {
          TextField("Report ID (hex)", text: $reportID).frame(width: 80)
          TextField("十六进制 payload", text: $hex)
          Button("发送…") { raw() }.disabled(busy)
        }
      }
      if let result {
        Text(
          result.reports.map { $0.map { String(format: "%02X", $0) }.joined(separator: " ") }
            .joined(separator: "\n")
        ).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
      }
      Text("C-Media / THX 的内核 DSP、APO 效果不属于 Windows 客户端已实现的 HID 功能；这里保留相同能力范围。").font(.caption)
        .foregroundStyle(.secondary)
    }.onDisappear { token.cancel() }
      .alert("操作未完成", isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } }))
    {
      Button("好") { error = nil }
    } message: {
      Text(error ?? "")
    }
  }
  func raw() {
    do {
      guard let id = UInt8(reportID.replacingOccurrences(of: "0x", with: ""), radix: 16) else {
        throw MchoseError.invalid("Report ID 无效。")
      }
      let payload = try hex.split(whereSeparator: { " ,\t\n-".contains($0) }).map { part -> UInt8 in
        guard let value = UInt8(part.replacingOccurrences(of: "0x", with: ""), radix: 16) else {
          throw MchoseError.invalid("十六进制格式无效。")
        }
        return value
      }
      run(.raw(id: id, payload: Data(payload)), confirm: "发送手工报告？仅在了解协议和报文影响时使用。")
    } catch { self.error = error.localizedDescription }
  }
  func run(_ action: AudioAction, confirm: String? = nil) {
    if let confirm {
      let alert = NSAlert()
      alert.messageText = confirm
      alert.addButton(withTitle: "继续")
      alert.addButton(withTitle: "取消")
      guard alert.runModal() == .alertFirstButtonReturn else { return }
    }
    token = CancellationToken()
    let current = token
    busy = true
    Task {
      do {
        let value = try await service.perform(deviceID: device.id, action: action, token: current)
        try current.check()
        result = value
        if let s = value.state {
          mode = s.lightMode
          brightness = Double(s.brightness)
          cycle = Double(s.cycle)
          eq = s.eq
          voice = s.voice
        }
      } catch let failure { error = failure.localizedDescription }
      busy = false
    }
  }
}
