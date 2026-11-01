import AppKit
import MchoseCore
import MchoseHID
import SwiftUI
import UniformTypeIdentifiers

extension AppModel {
  func diagnostic(name: String? = nil, hex: String = "") {
    guard let selection, !busy else { return }
    var payload = Data()
    if name == nil {
      do {
        payload = Data(
          try hex.split(whereSeparator: { " ,\n\t-".contains($0) }).map { part in
            guard let value = UInt8(part.replacingOccurrences(of: "0x", with: ""), radix: 16) else {
              throw MchoseError.invalid("十六进制格式无效。")
            }
            return value
          })
        guard (1...519).contains(payload.count) else {
          throw MchoseError.invalid("手工 payload 应为 1–519 字节。")
        }
      } catch {
        errorMessage = error.localizedDescription
        return
      }
      let alert = NSAlert()
      alert.messageText = "发送手工报文？"
      alert.informativeText = "此入口直接发送已输入的报文，可能改变设备设置。发送后会清除缓存，必须重新读取。"
      alert.addButton(withTitle: "发送")
      alert.addButton(withTitle: "取消")
      guard alert.runModal() == .alertFirstButtonReturn else { return }
    }
    let token = token
    let current = generation
    busy = true
    Task {
      do {
        let response: Data
        if let name {
          response = try await service.diagnostic(deviceID: selection, name: name, token: token)
        } else {
          response = try await service.raw(deviceID: selection, payload: payload, token: token)
        }
        guard current == generation else { return }
        log.append(
          "\(name ?? "raw") RX \(response.count) bytes: "
            + response.map { String(format: "%02X", $0) }.joined(separator: " "))
        if name == nil {
          profile = nil
          captured = nil
          uploadedMacros = nil
        }
      } catch {
        guard current == generation else { return }
        errorMessage = error.localizedDescription
        profile = nil
        captured = nil
      }
      if current == generation { busy = false }
    }
  }
  var library: ProfileLibrary {
    let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
      .appendingPathComponent("MchoseHub")
    return ProfileLibrary(
      root: base.appendingPathComponent(previewMode ? "preview-profiles" : "profiles"))
  }
  func refreshLibrary() {
    do { savedProfiles = try library.list(model: schema?.model ?? "K87") } catch {
      errorMessage = error.localizedDescription
    }
  }
  func saveNamedProfile() {
    do {
      if let profile {
        try library.save(name: profileName, profile: profile)
        refreshLibrary()
        message = "已保存本地配置：\(profileName)"
      }
    } catch { errorMessage = error.localizedDescription }
  }
  func loadNamedProfile(_ name: String) {
    do {
      profile = try library.load(model: schema?.model ?? "K87", name: name)
      profileName = name
      message = "已载入 \(name)，尚未应用到设备。"
    } catch { errorMessage = error.localizedDescription }
  }
  func deleteNamedProfile(_ name: String) {
    do {
      try library.delete(model: schema?.model ?? "K87", name: name)
      refreshLibrary()
    } catch { errorMessage = error.localizedDescription }
  }
  func confirmApply() {
    guard let selection, let target = profile, !busy else { return }
    let alert = NSAlert()
    alert.messageText = "应用当前配置到键盘？"
    alert.informativeText = "将先读取并保存当前完整配置，再写入已改变的配置块并回读比对。失败会停止；宏库不包含在配置备份中。"
    alert.addButton(withTitle: "应用")
    alert.addButton(withTitle: "取消")
    guard alert.runModal() == .alertFirstButtonReturn else { return }
    let current = generation
    let token = token
    let baseline = captured?.profile
    busy = true
    Task {
      do {
        let root = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[
          0
        ].appendingPathComponent("MchoseHub/backups")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let url = root.appendingPathComponent("before-\(UUID().uuidString).json")
        try await service.apply(
          deviceID: selection, target: target, baseline: baseline, backupURL: url, token: token)
        guard current == generation else { return }
        busy = false
        message = "已写入并回读验证，恢复前备份：\(url.lastPathComponent)"
        log.append(message)
        await read()
      } catch {
        guard current == generation else { return }
        busy = false
        profile = nil
        captured = nil
        uploadedMacros = nil
        errorMessage = error.localizedDescription
        message = "操作失败，已清除旧配置缓存。请重新读取。"
        log.append(error.localizedDescription)
      }
    }
  }
  func factoryDraft() {
    do {
      if let schema {
        try profile?.restoreFactory(schema: schema)
        message = "已生成默认配置草稿，保留保留槽。点击应用后才写入设备。"
      }
    } catch { errorMessage = error.localizedDescription }
  }
  func repairDraft() {
    do {
      if let schema {
        try profile?.repairLighting(schema: schema)
        message = "已生成灯效参数修复草稿，保留当前性能选项。点击应用后才写入设备。"
      }
    } catch { errorMessage = error.localizedDescription }
  }
  func uploadMacroLibrary() {
    guard let selection, let catalog, !busy else { return }
    do { _ = try MacroCodec.bank(macros, catalog: catalog) } catch {
      errorMessage = error.localizedDescription
      return
    }
    let alert = NSAlert()
    alert.messageText = "覆盖键盘完整宏库？"
    alert.informativeText = "设备协议不能备份完整宏库。请先另存本地宏文件；上传会覆盖原有宏，回读只能核对首个目录项。"
    alert.addButton(withTitle: "上传完整宏库")
    alert.addButton(withTitle: "取消")
    guard alert.runModal() == .alertFirstButtonReturn else { return }
    let copy = macros
    let current = generation
    let token = token
    busy = true
    uploadedMacros = nil
    Task {
      do {
        try await service.uploadMacros(deviceID: selection, macros: copy, token: token)
        guard current == generation else { return }
        uploadedMacros = copy
        uploadedOwner = selection
        message = "宏库已上传，首个目录项一致；完整事件与触发行为需实测。"
      } catch {
        guard current == generation else { return }
        errorMessage = error.localizedDescription
        uploadedMacros = nil
        profile = nil
        captured = nil
      }
      if current == generation { busy = false }
    }
  }
  var canBindMacro: Bool {
    !busy && !recording && selectedKey != nil && profile != nil
      && (selection == nil || (uploadedOwner == selection && uploadedMacros == macros))
  }
  func bindSelectedMacro() {
    do {
      guard canBindMacro, let schema, let selectedKey, macros.indices.contains(macroIndex) else {
        return
      }
      let binding = try MacroCodec.binding(macros[macroIndex].trigger, index: macroIndex)
      try profile?.remap(key: selectedKey, layer: layer, value: binding, schema: schema)
      message = "已在当前层的 \(selectedKey) 草稿中绑定宏；应用配置后生效。"
    } catch { errorMessage = error.localizedDescription }
  }
  func macroFile(save: Bool) {
    do {
      guard let catalog else { return }
      if save {
        _ = try MacroCodec.bank(macros, catalog: catalog)
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.json]
        panel.nameFieldStringValue = "macros.json"
        if panel.runModal() == .OK, let url = panel.url {
          let encoder = JSONEncoder()
          encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
          try encoder.encode(macros).write(to: url, options: .atomic)
        }
      } else {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.json]
        if panel.runModal() == .OK, let url = panel.url {
          let data = try Data(contentsOf: url)
          guard data.count <= 2_000_000 else { throw MchoseError.invalid("宏文件超过 2 MB。") }
          let values = try JSONDecoder().decode([KeyboardMacro].self, from: data)
          _ = try MacroCodec.bank(values, catalog: catalog)
          macros = values
          macroIndex = 0
        }
      }
    } catch { errorMessage = error.localizedDescription }
  }
  var macroStatus: String {
    guard let catalog else { return "码表未加载" }
    do {
      let size = try MacroCodec.bank(macros, catalog: catalog).count
      return "\(macros.count) 个宏 · \(size) 字节 · 有线上限 65535 / 无线上限 3570"
    } catch { return error.localizedDescription }
  }
  func changeColor(_ color: Color, fill: Bool = false, perKey: Bool = false) {
    guard let schema, let color = NSColor(color).usingColorSpace(.sRGB) else { return }
    let rgb = [color.redComponent, color.greenComponent, color.blueComponent].map {
      UInt8(max(0, min(255, ($0 * 255).rounded())))
    }
    do {
      try profile?.setColor(rgb, key: perKey ? selectedKey : nil, fill: fill, schema: schema)
    } catch { errorMessage = error.localizedDescription }
  }
}

struct FeaturePanels: View {
  @Bindable var model: AppModel
  @State var color = Color(red: 1, green: 0, blue: 0.7)
  @State var manualKey = "a"
  @State var manualDown = true
  @State var manualMouse = false
  @State var manualDelay = 50
  @State var rawHex = "87 00 00 01 00 02 00"
  var body: some View {
    Group {
      switch model.page {
      case "灯光": lighting
      case "性能": performance
      case "宏": macroEditor
      case "配置": profiles
      default: diagnostics
      }
    }
  }
  var lighting: some View {
    VStack(alignment: .leading, spacing: 18) {
      if let schema = model.schema {
        Picker(
          "灯效",
          selection: Binding(
            get: { model.number("lightMode") },
            set: { value in
              do { try model.profile?.setEffect(value, schema: schema) } catch {
                model.errorMessage = error.localizedDescription
              }
            })
        ) {
          ForEach(KeyboardEffect.all(schema: schema)) { Text($0.name).tag($0.id) }
        }.disabled(!model.canEdit)
        let effect = KeyboardEffect.all(schema: schema).first { $0.id == model.number("lightMode") }
        slider(
          "亮度", key: "brightness\(model.number("lightMode"))", range: 0...100,
          disabled: effect?.brightnessDisabled ?? true)
        slider(
          "速度", key: "speed\(model.number("lightMode"))", range: 0...4,
          disabled: effect?.speedDisabled ?? true)
        Toggle(
          "多彩",
          isOn: Binding(
            get: { model.number("multiColor\(model.number("lightMode"))") == 7 },
            set: { model.setNumber("multiColor\(model.number("lightMode"))", $0 ? 7 : 0) })
        ).disabled(!model.canEdit || effect?.colorDisabled == true)
        ColorPicker("颜色", selection: $color, supportsOpacity: false)
        if model.number("lightMode") == schema.customLightMode {
          KeyboardDiagram(keys: schema.board, selected: model.selectedKey, colors: diyColors) {
            model.selectedKey = $0
          }.frame(height: 285)
          Text("在键盘图上选择目标键，再为该键设置颜色。")
          HStack {
            Button("设置所选键颜色（\(model.selectedKey ?? "未选择")）") {
              model.changeColor(color, perKey: true)
            }.disabled(model.selectedKey == nil || !model.canEdit)
            Button("填充全部 LED") { model.changeColor(color, fill: true) }.disabled(!model.canEdit)
          }
        } else {
          Button("设置当前灯效颜色") { model.changeColor(color) }.disabled(
            !model.canEdit || effect?.colorDisabled == true)
        }
        Button("生成灯效参数修复草稿") { model.repairDraft() }.disabled(
          !model.canEdit || schema.model != "K87")
        Button("生成强制点亮草稿") {
          do {
            try model.profile?.setEffect(1, schema: schema)
            model.setNumber("brightness1", 100)
            model.setNumber("speed1", 0)
            try model.profile?.setColor([255, 255, 255], schema: schema)
          } catch { model.errorMessage = error.localizedDescription }
        }.disabled(!model.canEdit)
      }
    }.padding(12)
  }
  var diyColors: [String: Color] {
    guard let schema = model.schema, let profile = model.profile,
      let red = profile.diy.buffers["rTable"], let green = profile.diy.buffers["gTable"],
      let blue = profile.diy.buffers["bTable"]
    else { return [:] }
    var result: [String: Color] = [:]
    for (i, key) in schema.keyList.enumerated()
    where i < red.count && i < green.count && i < blue.count {
      result[key.n] = Color(
        red: Double(red[i]) / 255, green: Double(green[i]) / 255, blue: Double(blue[i]) / 255)
    }
    return result
  }
  func slider(_ title: String, key: String, range: ClosedRange<Double>, disabled: Bool = false)
    -> some View
  {
    VStack(alignment: .leading) {
      Text("\(title)：\(model.number(key))")
      Slider(
        value: Binding(
          get: { Double(model.number(key)) }, set: { model.setNumber(key, Int($0.rounded())) }),
        in: range, step: 1
      )
      .disabled(!model.canEdit || disabled || model.profile?.performance.numbers[key] == nil)
    }
  }
  var performance: some View {
    Form {
      if model.profile?.performance.numbers["rateVal"] != nil {
        Picker(
          "回报率",
          selection: Binding(
            get: { model.number("rateVal") }, set: { model.setNumber("rateVal", $0) })
        ) {
          Text("250 Hz").tag(1)
          Text("500 Hz").tag(2)
          Text("1000 Hz").tag(3)
        }
      }
      Picker(
        "休眠",
        selection: Binding(
          get: { model.number("sleepTimeVal") / 2 },
          set: { model.setNumber("sleepTimeVal", $0 * 2) })
      ) {
        ForEach([0, 1, 2, 3, 5, 10, 15, 30], id: \.self) {
          Text($0 == 0 ? "不休眠" : "\($0) 分钟").tag($0)
        }
      }
      if let schema = model.schema {
        Picker(
          "延迟模式",
          selection: Binding(
            get: {
              model.number("latencySwitch") == 0
                ? 0 : schema.model == "Z75" && model.number("latencySwitch") == 1 ? 1 : 2
            },
            set: { value in
              do { try model.profile?.setLatency(value, schema: schema) } catch {
                model.errorMessage = error.localizedDescription
              }
            })
        ) {
          Text("速度模式").tag(0)
          if schema.model == "Z75" { Text("标准低延迟").tag(1) }
          Text("标准模式").tag(2)
        }
        Toggle(
          "Mac 模式",
          isOn: Binding(
            get: { model.number("macSwitch") == schema.macSwitchOn },
            set: { on in
              model.setNumber("macSwitch", on ? schema.macSwitchOn : 0)
              if on { model.setNumber("winKeySwitch", 0) }
            }))
        Toggle(
          "锁定 Win 键",
          isOn: Binding(
            get: { model.number("winKeySwitch") == 1 },
            set: { model.setNumber("winKeySwitch", $0 ? 1 : 0) })
        ).disabled(model.number("macSwitch") == schema.macSwitchOn)
      }
    }.formStyle(.grouped).disabled(!model.canEdit)
  }
  var macroEditor: some View {
    VStack(alignment: .leading, spacing: 14) {
      Text(model.macroStatus).font(.caption).foregroundStyle(.secondary)
      HStack {
        Picker("宏", selection: $model.macroIndex) {
          ForEach(model.macros.indices, id: \.self) { Text(model.macros[$0].name).tag($0) }
        }
        Button("新建") {
          model.macros.append(
            KeyboardMacro(
              name: "宏 \(UUID().uuidString.prefix(6))",
              steps: [MacroStep(key: "a", down: true), MacroStep(key: "a", down: false)]))
          model.macroIndex = model.macros.count - 1
        }
        Button("删除") {
          if model.macros.indices.contains(model.macroIndex) {
            model.macros.remove(at: model.macroIndex)
            model.macroIndex = max(0, model.macroIndex - 1)
          }
        }
      }
      if model.macros.indices.contains(model.macroIndex) {
        let index = model.macroIndex
        TextField("名称", text: $model.macros[index].name)
        Picker("触发方式", selection: $model.macros[index].trigger) {
          Text("按住").tag(MacroTrigger.hold)
          Text("切换").tag(MacroTrigger.toggle)
          Text("一次").tag(MacroTrigger.once)
        }
        ScrollView {
          LazyVStack(alignment: .leading) {
            ForEach(model.macros[index].steps.indices, id: \.self) { i in
              HStack {
                Text("\(i+1)").frame(width: 35)
                TextField("键", text: $model.macros[index].steps[i].key).frame(width: 110)
                Toggle("按下", isOn: $model.macros[index].steps[i].down).toggleStyle(.checkbox)
                Toggle("鼠标", isOn: $model.macros[index].steps[i].mouse).toggleStyle(.checkbox)
                TextField("延时 ms", value: $model.macros[index].steps[i].delayMs, format: .number)
                  .frame(width: 80)
                Button("移除", systemImage: "minus.circle") {
                  model.macros[index].steps.remove(at: i)
                }.labelStyle(.iconOnly)
              }
            }
          }
        }.frame(minHeight: 140, maxHeight: 250)
        HStack {
          TextField("事件键", text: $manualKey).frame(width: 100)
          Toggle("按下", isOn: $manualDown).toggleStyle(.checkbox)
          Toggle("鼠标", isOn: $manualMouse).toggleStyle(.checkbox)
          TextField("延时 ms", value: $manualDelay, format: .number).frame(width: 80)
          Button("添加事件") {
            model.macros[index].steps.append(
              MacroStep(key: manualKey, down: manualDown, delayMs: manualDelay, mouse: manualMouse))
          }
        }
        HStack {
          Button(model.recording ? "停止录制" : "开始录制") { model.recording.toggle() }
          Text("录制仅在下方区域有焦点时捕获；离开时补齐释放事件。").font(.caption)
        }
        MacroRecorder(recording: $model.recording) { steps in
          if !steps.isEmpty {
            model.macros.append(
              KeyboardMacro(name: "录制 \(UUID().uuidString.prefix(6))", steps: steps))
            model.macroIndex = model.macros.count - 1
          }
        }.frame(height: 75)
      }
      HStack {
        Button("另存宏库…") { model.macroFile(save: true) }
        Button("导入宏库…") { model.macroFile(save: false) }
        Button("上传完整宏库…") { model.uploadMacroLibrary() }.disabled(
          model.selection == nil || model.busy || model.recording)
        Button("绑定到所选按键") { model.bindSelectedMacro() }.disabled(!model.canBindMacro)
      }
      Text("修改宏后需重新上传才能在设备上绑定。配置备份不包含完整宏库。").font(.caption).foregroundStyle(.secondary)
    }
    .onDisappear { model.recording = false }
  }
  var profiles: some View {
    VStack(alignment: .leading, spacing: 15) {
      HStack {
        TextField("配置名称", text: $model.profileName)
        Button("保存当前草稿") { model.saveNamedProfile() }.disabled(model.profile == nil)
      }
      ForEach(model.savedProfiles, id: \.self) { name in
        HStack {
          Text(name)
          Spacer()
          Button("载入") { model.loadNamedProfile(name) }
          Button("删除") { model.deleteNamedProfile(name) }
        }
      }
      HStack {
        Button("导入 JSON…") { model.importProfile() }
        Button("导出 JSON…") { model.exportProfile() }.disabled(model.profile == nil)
      }
      Button("生成恢复默认草稿") { model.factoryDraft() }.disabled(!model.canEdit)
      Text("本地配置按型号保存，预览库独立。载入和恢复默认草稿均不会立即写设备。").font(.caption).foregroundStyle(.secondary)
    }.onAppear { model.refreshLibrary() }
  }
  var diagnostics: some View {
    VStack(alignment: .leading, spacing: 14) {
      Menu("已知只读命令") {
        ForEach(ReadSection.allCases, id: \.self) { section in
          Button(section.rawValue) { model.diagnostic(name: section.rawValue) }
        }
        Button("宏目录") { model.diagnostic(name: "macro") }
        Button("型号/密码信息") { model.diagnostic(name: "password") }
      }.disabled(model.selection == nil || model.busy)
      HStack {
        TextField("十六进制 payload", text: $rawHex)
        Button("手工发送…") { model.diagnostic(hex: rawHex) }.disabled(
          model.selection == nil || model.busy)
      }
      Button("清空日志") { model.log = [] }
      Picker("外观", selection: $model.theme) {
        Text("跟随系统").tag("system")
        Text("浅色").tag("light")
        Text("深色").tag("dark")
      }
      Link("打开官方网页驱动", destination: URL(string: "https://www.mchose.com.cn/")!)
      Text("输入报告访问：\(MacHID.inputAccessDescription)")
      Button("打开输入监控设置") {
        NSWorkspace.shared.open(
          URL(
            string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent")!)
      }
      Text(
        "型号：\(model.schema?.model ?? "未知") · 通道：\(model.devices.first(where: { $0.id==model.selection })?.isWireless==true ? "2.4G" : model.selection==nil ? "离线" : "USB feature")"
      )
      if let result = model.captured {
        Text("电量：\(result.battery.map { "\($0)%" } ?? "未知") · 已记录 \(result.exchanges.count) 次完整读取")
      }
      ForEach(Array(model.log.enumerated()), id: \.offset) {
        Text($0.element).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
      }
      if let captured = model.captured {
        ScrollView {
          Text(
            captured.exchanges.map {
              "\($0.section) RX \($0.response.count) bytes: "
                + $0.response.prefix(32).map { String(format: "%02X", $0) }.joined(separator: " ")
            }.joined(separator: "\n")
          ).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
        }.frame(height: 220)
      }
    }
  }
}
