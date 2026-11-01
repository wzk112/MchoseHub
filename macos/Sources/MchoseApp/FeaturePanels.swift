import AppKit
import MchoseCore
import MchoseHID
import SwiftUI
import UniformTypeIdentifiers

extension AppModel {
  func diagnostic(name: String? = nil, hex: String = "") {
    guard let selection, !busy, !recording else { return }
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
          captured = nil
          uploadedMacros = nil
          uploadedOwner = nil
          message = "报文已发送；设备状态需重新读取验证，当前草稿仍可导出。"
        }
      } catch {
        guard current == generation else { return }
        errorMessage = error.localizedDescription
        captured = nil
        uploadedMacros = nil
        uploadedOwner = nil
        message = "诊断请求失败；设备状态需重新读取验证，当前草稿仍可导出。"
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
    guard !busy, !recording else { return }
    do {
      if let profile {
        try library.save(name: profileName, profile: profile)
        savedProfile = profile
        refreshLibrary()
        message = "已保存本地配置：\(profileName)"
      }
    } catch { errorMessage = error.localizedDescription }
  }
  func loadNamedProfile(_ name: String) {
    guard !busy, !recording, confirmDiscard() else { return }
    do {
      profile = try library.load(model: schema?.model ?? "K87", name: name)
      savedProfile = profile
      selectedKey = nil
      profileName = name
      message = "已载入 \(name)，尚未应用到设备。"
    } catch { errorMessage = error.localizedDescription }
  }
  func deleteNamedProfile(_ name: String) {
    guard !busy, !recording else { return }
    let alert = NSAlert()
    alert.messageText = "删除本地配置“\(name)”？"
    alert.informativeText = "当前草稿和设备设置不受影响。"
    alert.addButton(withTitle: "取消")
    alert.addButton(withTitle: "删除配置")
    guard alert.runModal() == .alertSecondButtonReturn else { return }
    do {
      try library.delete(model: schema?.model ?? "K87", name: name)
      refreshLibrary()
    } catch { errorMessage = error.localizedDescription }
  }
  func confirmApply() {
    guard let selection, let target = profile, let baseline = captured?.profile, canEdit else {
      return
    }
    let alert = NSAlert()
    alert.messageText = "应用当前配置到键盘？"
    alert.informativeText = "将先读取并保存当前完整配置，再写入已改变的配置块并回读比对。失败会停止；宏库不包含在配置备份中。"
    alert.addButton(withTitle: "应用")
    alert.addButton(withTitle: "取消")
    guard alert.runModal() == .alertFirstButtonReturn else { return }
    let current = generation
    let token = token
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
        captured = nil
        uploadedMacros = nil
        errorMessage = error.localizedDescription
        message = "操作失败，设备会话未验证；草稿仍可导出。请重新读取设备后再应用。"
        log.append(error.localizedDescription)
      }
    }
  }
  func factoryDraft() {
    guard canEdit, confirmDiscard() else { return }
    do {
      if let schema {
        try profile?.restoreFactory(schema: schema)
        message = "已生成默认配置草稿，保留固件保留槽。点击应用后才写入设备。"
      }
    } catch { errorMessage = error.localizedDescription }
  }
  func repairDraft() {
    guard canEdit else { return }
    do {
      if let schema {
        try profile?.repairLighting(schema: schema)
        message = "已生成灯效参数修复草稿，保留当前性能选项。点击应用后才写入设备。"
      }
    } catch { errorMessage = error.localizedDescription }
  }
  func uploadMacroLibrary() {
    guard let selection, let catalog, !busy, !recording else { return }
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
        uploadedOwner = nil
        captured = nil
        message = "宏上传未完成；设备状态需重新读取验证，当前配置草稿仍可导出。"
      }
      if current == generation { busy = false }
    }
  }
  var canBindMacro: Bool {
    !busy && !recording && selectedKey != nil && profile != nil
      && macros.indices.contains(macroIndex)
      && schema?.reservedKeys.contains(selectedKey ?? "") == false
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
      guard let catalog, !busy, !recording else { return }
      if !save && hasUnsavedMacros {
        let alert = NSAlert()
        alert.messageText = "替换当前宏库？"
        alert.informativeText = "当前宏库有未保存的修改，可以取消并先另存宏文件。"
        alert.addButton(withTitle: "取消")
        alert.addButton(withTitle: "继续导入")
        guard alert.runModal() == .alertSecondButtonReturn else { return }
      }
      if save {
        _ = try MacroCodec.bank(macros, catalog: catalog)
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.json]
        panel.nameFieldStringValue = "macros.json"
        if panel.runModal() == .OK, let url = panel.url {
          let encoder = JSONEncoder()
          encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
          try encoder.encode(macros).write(to: url, options: .atomic)
          savedMacros = macros
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
          savedMacros = values
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
    guard canEdit, let schema, let color = NSColor(color).usingColorSpace(.sRGB) else { return }
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
  @State private var color = Color.white
  @State private var manualKey = "a"
  @State private var manualDown = true
  @State private var manualMouse = false
  @State private var manualDelay = 50
  @State private var rawHex = "87 00 00 01 00 02 00"
  @State private var selectedProfile: String?

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
    .textFieldStyle(.roundedBorder)
    .buttonStyle(.bordered)
    .disabled(model.busy)
  }

  private var lighting: some View {
    VStack(alignment: .leading, spacing: 24) {
      if let schema = model.schema {
        let mode = model.number("lightMode")
        let effect = KeyboardEffect.all(schema: schema).first { $0.id == mode }
        HubSection("灯光效果") {
          Grid(alignment: .leading, horizontalSpacing: 24, verticalSpacing: 16) {
            GridRow {
              fieldLabel("效果")
              Picker(
                "灯光效果",
                selection: Binding(
                  get: { model.number("lightMode") },
                  set: { value in
                    guard model.canEdit else { return }
                    do { try model.profile?.setEffect(value, schema: schema) } catch {
                      model.errorMessage = error.localizedDescription
                    }
                  })
              ) {
                ForEach(KeyboardEffect.all(schema: schema)) { Text($0.name).tag($0.id) }
              }.labelsHidden().frame(maxWidth: 280)
            }
            GridRow {
              fieldLabel("参数")
              HStack(alignment: .top, spacing: 28) {
                lightSlider(
                  "亮度", key: "brightness\(mode)", range: 0...100,
                  suffix: "%", disabled: effect?.brightnessDisabled ?? true)
                lightSlider(
                  "速度", key: "speed\(mode)", range: 0...4,
                  suffix: "", disabled: effect?.speedDisabled ?? true)
              }.frame(maxWidth: .infinity)
            }
            if model.profile?.performance.numbers["multiColor\(mode)"] != nil,
              effect?.colorDisabled == false
            {
              GridRow {
                fieldLabel("色彩")
                Toggle(
                  "多彩模式",
                  isOn: Binding(
                    get: { model.number("multiColor\(mode)") == 7 },
                    set: { model.setNumber("multiColor\(mode)", $0 ? 7 : 0) })
                )
                .toggleStyle(.checkbox)
              }
            }
          }.disabled(!model.canEdit)
        }

        if mode == schema.customLightMode {
          HubSection("逐键颜色") {
            KeyboardDiagram(keys: schema.board, selected: model.selectedKey, colors: diyColors) {
              model.selectedKey = $0
            }.frame(height: 250).disabled(!model.canEdit)
            HStack(spacing: 12) {
              ColorPicker("颜色", selection: $color, supportsOpacity: false)
                .fixedSize().disabled(!model.canEdit)
              Text(model.selectedKey.map { "所选：\($0)" } ?? "请选择一个按键")
                .font(.callout).foregroundStyle(.secondary)
              Spacer(minLength: 12)
              Button("设置所选键") { model.changeColor(color, perKey: true) }
                .disabled(model.selectedKey == nil || !model.canEdit)
              Button("填充全部") { model.changeColor(color, fill: true) }
                .disabled(!model.canEdit)
            }
          }
        } else if effect?.colorDisabled == false {
          HubSection("灯效颜色") {
            HStack(spacing: 16) {
              ColorPicker("颜色", selection: $color, supportsOpacity: false).fixedSize()
              Button("设置颜色") { model.changeColor(color) }
              Spacer()
            }.disabled(!model.canEdit)
          }
        }

        DisclosureGroup("修复与强制点亮") {
          VStack(alignment: .leading, spacing: 10) {
            Text("生成可检查的草稿，应用后才会更改设备。")
              .font(.caption).foregroundStyle(.secondary)
            HStack(spacing: 10) {
              Button("修复 K87 灯效参数") { model.repairDraft() }
                .disabled(!model.canEdit || schema.model != "K87")
              Button("生成白光常亮草稿") {
                guard model.canEdit else { return }
                do {
                  try model.profile?.setEffect(1, schema: schema)
                  model.setNumber("brightness1", 100)
                  model.setNumber("speed1", 0)
                  try model.profile?.setColor([255, 255, 255], schema: schema)
                } catch { model.errorMessage = error.localizedDescription }
              }.disabled(!model.canEdit)
            }
          }.padding(.top, 8)
        }.font(.callout)
      }
    }
    .onAppear { syncColor() }
    .onChange(of: model.number("lightMode")) { syncColor() }
    .onChange(of: model.selectedKey) { syncColor() }
    .onChange(of: model.generation) { syncColor() }
    .onChange(of: model.profile?.lighting) { syncColor() }
    .onChange(of: model.profile?.diy) { syncColor() }
  }

  private func lightSlider(
    _ title: String, key: String, range: ClosedRange<Double>, suffix: String, disabled: Bool
  ) -> some View {
    VStack(alignment: .leading, spacing: 8) {
      HStack {
        Text(title)
        Spacer()
        Text(disabled ? "—" : "\(model.number(key))\(suffix)")
          .monospacedDigit().foregroundStyle(.secondary)
      }.font(.callout)
      Slider(
        value: Binding(
          get: { Double(model.number(key)) },
          set: { model.setNumber(key, Int($0.rounded())) }), in: range
      )
      .accessibilityLabel(title)
      .disabled(!model.canEdit || disabled || model.profile?.performance.numbers[key] == nil)
    }.frame(maxWidth: .infinity)
  }

  private var diyColors: [String: Color] {
    guard let schema = model.schema, let profile = model.profile,
      let red = profile.diy.buffers["rTable"], let green = profile.diy.buffers["gTable"],
      let blue = profile.diy.buffers["bTable"]
    else { return [:] }
    var result: [String: Color] = [:]
    for (index, key) in schema.keyList.enumerated()
    where index < red.count && index < green.count && index < blue.count {
      result[key.n] = Color(
        red: Double(red[index]) / 255,
        green: Double(green[index]) / 255, blue: Double(blue[index]) / 255)
    }
    return result
  }

  private func syncColor() {
    if model.number("lightMode") == model.schema?.customLightMode {
      color = model.selectedKey.flatMap { diyColors[$0] } ?? .white
    } else if let bytes = model.profile?.lighting.buffers[
      "lightModeColor\(model.number("lightMode"))"],
      bytes.count == 3
    {
      color = Color(
        red: Double(bytes[0]) / 255, green: Double(bytes[1]) / 255,
        blue: Double(bytes[2]) / 255)
    } else {
      color = .white
    }
  }

  private var performance: some View {
    ViewThatFits(in: .horizontal) {
      HStack(alignment: .top, spacing: 32) {
        connectionSettings.frame(minWidth: 280, maxWidth: .infinity, alignment: .topLeading)
        systemKeySettings.frame(minWidth: 240, maxWidth: .infinity, alignment: .topLeading)
      }
      VStack(alignment: .leading, spacing: 24) {
        connectionSettings
        systemKeySettings
      }
    }.disabled(!model.canEdit)
  }

  private var connectionSettings: some View {
    HubSection("连接与响应") {
      Grid(alignment: .leading, horizontalSpacing: 16, verticalSpacing: 18) {
        if model.profile?.performance.numbers["rateVal"] != nil {
          GridRow {
            fieldLabel("回报率")
            Picker(
              "回报率",
              selection: Binding(
                get: { model.number("rateVal") }, set: { model.setNumber("rateVal", $0) })
            ) {
              Text("250 Hz").tag(1)
              Text("500 Hz").tag(2)
              Text("1000 Hz").tag(3)
            }.labelsHidden().frame(minWidth: 150, idealWidth: 190, maxWidth: 240)
          }
        }
        if let schema = model.schema {
          GridRow {
            fieldLabel("延迟模式")
            Picker(
              "延迟模式",
              selection: Binding(
                get: {
                  model.number("latencySwitch") == 0
                    ? 0
                    : schema.model == "Z75" && model.number("latencySwitch") == 1 ? 1 : 2
                },
                set: { value in
                  guard model.canEdit else { return }
                  do { try model.profile?.setLatency(value, schema: schema) } catch {
                    model.errorMessage = error.localizedDescription
                  }
                })
            ) {
              Text("速度模式").tag(0)
              if schema.model == "Z75" { Text("标准低延迟").tag(1) }
              Text("标准模式").tag(2)
            }.labelsHidden().frame(minWidth: 150, idealWidth: 190, maxWidth: 240)
          }
        }
        GridRow {
          fieldLabel("自动休眠")
          Picker(
            "自动休眠",
            selection: Binding(
              get: { model.number("sleepTimeVal") }, set: { model.setNumber("sleepTimeVal", $0) })
          ) {
            ForEach(
              Array(Set([0, 2, 4, 6, 10, 20, 30, 60, model.number("sleepTimeVal")])).sorted(),
              id: \.self
            ) {
              Text($0 == 0 ? "不休眠" : "\(Double($0) / 2, specifier: "%g") 分钟").tag($0)
            }
          }.labelsHidden().frame(minWidth: 150, idealWidth: 190, maxWidth: 240)
        }
      }
    }
  }

  @ViewBuilder private var systemKeySettings: some View {
    if let schema = model.schema {
      HubSection("系统按键") {
        VStack(alignment: .leading, spacing: 16) {
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
          )
          .disabled(model.number("macSwitch") == schema.macSwitchOn)
          Text("Mac 模式下不启用 Win 键锁定。")
            .font(.caption).foregroundStyle(.secondary)
        }.toggleStyle(.checkbox)
      }
    }
  }

  private var macroEditor: some View {
    VStack(alignment: .leading, spacing: 20) {
      HStack(spacing: 8) {
        Picker("当前宏", selection: $model.macroIndex) {
          if model.macros.isEmpty { Text("没有宏").tag(0) }
          ForEach(model.macros.indices, id: \.self) { Text(model.macros[$0].name).tag($0) }
        }.frame(maxWidth: 310).disabled(model.recording || model.macros.isEmpty)
        Button("新建", systemImage: "plus") { addMacro() }.disabled(model.recording)
        Button("删除", systemImage: "minus") { deleteMacro() }
          .disabled(model.recording || !model.macros.indices.contains(model.macroIndex))
        Spacer()
        if model.hasUnsavedMacros {
          Label("未保存", systemImage: "circle.fill").font(.caption).foregroundStyle(.secondary)
        }
      }

      if model.macros.indices.contains(model.macroIndex) {
        let index = model.macroIndex
        let steps = model.macros[index].steps
        Grid(alignment: .leading, horizontalSpacing: 16, verticalSpacing: 12) {
          GridRow {
            fieldLabel("名称")
            TextField("宏名称", text: macroBinding(index, \.name, fallback: ""))
              .frame(maxWidth: 420)
          }
          GridRow {
            fieldLabel("触发方式")
            Picker("触发方式", selection: macroBinding(index, \.trigger, fallback: .once)) {
              Text("按住").tag(MacroTrigger.hold)
              Text("切换").tag(MacroTrigger.toggle)
              Text("一次").tag(MacroTrigger.once)
            }.labelsHidden().pickerStyle(.segmented).frame(maxWidth: 300)
          }
        }.disabled(model.recording)

        HubSection("事件序列") {
          VStack(spacing: 0) {
            macroColumns
              .padding(.horizontal, 12).padding(.vertical, 8)
              .background(Color(nsColor: .controlBackgroundColor))
            Divider()
            ScrollView {
              LazyVStack(spacing: 0) {
                ForEach(steps.indices, id: \.self) { step in
                  macroRow(macro: index, step: step)
                    .padding(.horizontal, 12).padding(.vertical, 5)
                  if step + 1 < steps.count { Divider() }
                }
                if steps.isEmpty {
                  Text("还没有事件。添加一项，或开始录制。")
                    .foregroundStyle(.secondary).padding(20)
                }
              }
            }.frame(height: min(192, max(74, CGFloat(steps.count) * 37)))
          }
          .background(Color(nsColor: .textBackgroundColor))
          .clipShape(RoundedRectangle(cornerRadius: 6))
          .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(.separator.opacity(0.5)))
          .disabled(model.recording)

          HStack(spacing: 12) {
            TextField("键名", text: $manualKey).frame(minWidth: 90, maxWidth: .infinity)
            Toggle("按下", isOn: $manualDown).toggleStyle(.checkbox).frame(width: 62)
            Toggle("鼠标", isOn: $manualMouse).toggleStyle(.checkbox).frame(width: 62)
            HStack(spacing: 4) {
              TextField("延时", value: $manualDelay, format: .number).frame(width: 64)
              Text("ms").foregroundStyle(.secondary)
            }
            Button("添加事件", systemImage: "plus") {
              guard model.macros.indices.contains(index) else { return }
              model.macros[index].steps.append(
                MacroStep(
                  key: manualKey, down: manualDown,
                  delayMs: manualDelay, mouse: manualMouse))
            }.disabled(manualKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
          }.disabled(model.recording)
        }

        VStack(alignment: .leading, spacing: 8) {
          HStack(spacing: 12) {
            Button(
              model.recording ? "停止录制" : "开始录制",
              systemImage: model.recording ? "stop.circle" : "record.circle"
            ) { model.recording.toggle() }
            Text("在下方区域输入；离开区域会结束录制。")
              .font(.caption).foregroundStyle(.secondary)
          }
          MacroRecorder(recording: $model.recording) { steps in
            if !steps.isEmpty {
              model.macros.append(
                KeyboardMacro(name: "录制 \(UUID().uuidString.prefix(6))", steps: steps))
              model.macroIndex = model.macros.count - 1
            }
          }.frame(height: 48)
        }
      } else {
        ContentUnavailableView(
          "新建一个宏", systemImage: "record.circle",
          description: Text("添加键盘、鼠标事件，或录制输入序列。")
        )
        .frame(height: 180)
      }

      Divider()
      HStack(spacing: 10) {
        Button("导入…") { model.macroFile(save: false) }
        Button("另存宏库…") { model.macroFile(save: true) }
        Spacer()
        Button("绑定所选按键") { model.bindSelectedMacro() }.disabled(!model.canBindMacro)
        Button("上传宏库…") { model.uploadMacroLibrary() }
          .disabled(model.selection == nil || model.recording)
      }.disabled(model.recording)
      Text(model.macroStatus).font(.caption).foregroundStyle(.secondary)
      Text("上传将覆盖设备宏库，配置备份不含宏。修改宏后需重新上传，再绑定到按键。")
        .font(.caption).foregroundStyle(.secondary)
    }.onDisappear { model.recording = false }
  }

  private var macroColumns: some View {
    HStack(spacing: 12) {
      Text("#").frame(width: 24)
      Text("键名").frame(minWidth: 90, maxWidth: .infinity, alignment: .leading)
      Text("按下").frame(width: 42)
      Text("鼠标").frame(width: 42)
      Text("延时 · ms").frame(width: 80)
      Color.clear.frame(width: 28, height: 1)
    }.font(.caption).foregroundStyle(.secondary)
  }

  private func macroRow(macro: Int, step: Int) -> some View {
    HStack(spacing: 12) {
      Text("\(step + 1)").monospacedDigit().foregroundStyle(.secondary).frame(width: 24)
      TextField("键名", text: stepBinding(macro, step, \.key, fallback: ""))
        .frame(minWidth: 90, maxWidth: .infinity)
        .accessibilityLabel("事件 \(step + 1) 键名")
      Toggle("按下", isOn: stepBinding(macro, step, \.down, fallback: false))
        .labelsHidden().toggleStyle(.checkbox).frame(width: 42)
        .accessibilityLabel("事件 \(step + 1) 按下")
      Toggle("鼠标", isOn: stepBinding(macro, step, \.mouse, fallback: false))
        .labelsHidden().toggleStyle(.checkbox).frame(width: 42)
        .accessibilityLabel("事件 \(step + 1) 鼠标")
      TextField("延时", value: stepBinding(macro, step, \.delayMs, fallback: 0), format: .number)
        .frame(width: 80).accessibilityLabel("事件 \(step + 1) 延时毫秒")
      Button {
        guard model.macros.indices.contains(macro), model.macros[macro].steps.indices.contains(step)
        else { return }
        model.macros[macro].steps.remove(at: step)
      } label: {
        Image(systemName: "minus.circle")
      }
      .buttonStyle(.borderless).frame(width: 28).help("移除事件 \(step + 1)")
    }
  }

  private func macroBinding<T>(
    _ index: Int, _ keyPath: WritableKeyPath<KeyboardMacro, T>, fallback: T
  ) -> Binding<T> {
    Binding(
      get: {
        guard model.macros.indices.contains(index) else { return fallback }
        return model.macros[index][keyPath: keyPath]
      },
      set: { value in
        guard !model.recording, model.macros.indices.contains(index) else { return }
        model.macros[index][keyPath: keyPath] = value
      })
  }

  private func stepBinding<T>(
    _ macro: Int, _ step: Int, _ keyPath: WritableKeyPath<MacroStep, T>, fallback: T
  ) -> Binding<T> {
    Binding(
      get: {
        guard model.macros.indices.contains(macro), model.macros[macro].steps.indices.contains(step)
        else { return fallback }
        return model.macros[macro].steps[step][keyPath: keyPath]
      },
      set: { value in
        guard !model.recording, model.macros.indices.contains(macro),
          model.macros[macro].steps.indices.contains(step)
        else { return }
        model.macros[macro].steps[step][keyPath: keyPath] = value
      })
  }

  private func addMacro() {
    model.macros.append(
      KeyboardMacro(
        name: "宏 \(model.macros.count + 1)",
        steps: [MacroStep(key: "a", down: true), MacroStep(key: "a", down: false)]))
    model.macroIndex = model.macros.count - 1
  }

  private func deleteMacro() {
    guard model.macros.indices.contains(model.macroIndex) else { return }
    model.macros.remove(at: model.macroIndex)
    model.macroIndex = max(0, min(model.macroIndex, model.macros.count - 1))
  }

  private var profiles: some View {
    VStack(alignment: .leading, spacing: 24) {
      HubSection("保存当前草稿") {
        HStack(spacing: 10) {
          TextField("配置名称", text: $model.profileName).frame(maxWidth: 380)
          Button("保存") { model.saveNamedProfile() }
            .disabled(
              model.profile == nil
                || model.profileName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
          Spacer()
        }
      }
      HubSection(
        "已保存配置",
        subtitle: "\(model.schema?.model ?? "K87") · \(model.previewMode ? "预览配置库" : "设备配置库")"
      ) {
        if model.savedProfiles.isEmpty {
          HStack(spacing: 10) {
            Image(systemName: "tray").foregroundStyle(.secondary)
            Text("此配置库暂无文件").foregroundStyle(.secondary)
          }.frame(maxWidth: .infinity, minHeight: 72, alignment: .center)
        } else {
          List(selection: $selectedProfile) {
            ForEach(model.savedProfiles, id: \.self) { name in
              Label(name, systemImage: "doc.text").tag(name)
                .padding(.vertical, 4)
            }
          }.listStyle(.inset).frame(
            height: min(220, max(112, CGFloat(model.savedProfiles.count) * 36))
          )
          .clipShape(RoundedRectangle(cornerRadius: 6))
          .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(.separator.opacity(0.5)))
        }
        HStack(spacing: 10) {
          Button("载入所选配置") {
            if let selectedProfile { model.loadNamedProfile(selectedProfile) }
          }.disabled(selectedProfile == nil)
          Button("删除…", role: .destructive) {
            if let selectedProfile { model.deleteNamedProfile(selectedProfile) }
          }.disabled(selectedProfile == nil)
          Spacer()
          Button("导入 JSON…") { model.importProfile() }
          Button("导出 JSON…") { model.exportProfile() }.disabled(model.profile == nil)
        }
      }
      DisclosureGroup("恢复默认配置") {
        HStack(spacing: 16) {
          Text("生成默认草稿；查看并应用后才会写入设备。")
            .font(.caption).foregroundStyle(.secondary)
          Spacer()
          Button("生成默认草稿") { model.factoryDraft() }.disabled(!model.canEdit)
        }.padding(.top, 8)
      }
    }
    .onAppear { model.refreshLibrary() }
    .onChange(of: model.savedProfiles) {
      if let selectedProfile, !model.savedProfiles.contains(selectedProfile) {
        self.selectedProfile = nil
      }
    }
  }

  private var diagnostics: some View {
    VStack(alignment: .leading, spacing: 24) {
      HubSection("连接信息") {
        Grid(alignment: .leading, horizontalSpacing: 24, verticalSpacing: 12) {
          diagnosticRow(
            "型号",
            model.selection == nil
              ? model.profile?.model ?? model.schema?.model ?? "未知"
              : model.captured?.profile.model ?? "待读取确认")
          diagnosticRow(
            "通道",
            model.selection == nil
              ? "离线工作空间"
              : model.devices.first(where: { $0.id == model.selection })?.isWireless == true
                ? "2.4G 无线" : "USB 有线")
          diagnosticRow("输入报告权限", MacHID.inputAccessDescription)
          diagnosticRow("配置状态", model.captured == nil ? "尚未建立设备基线" : "已完成双次全量读取")
          if let result = model.captured {
            diagnosticRow("电量", result.battery.map { "\($0)%" } ?? "未知")
          }
        }
        HStack(spacing: 10) {
          Button("输入监控设置…") { openInputMonitoring() }
          Link("官方驱动网站", destination: URL(string: "https://www.mchose.com.cn/")!)
          Spacer()
        }
      }
      HubSection("通信记录") {
        HStack(spacing: 10) {
          Menu("读取配置块") {
            ForEach(ReadSection.allCases, id: \.self) { section in
              Button(section.rawValue) { model.diagnostic(name: section.rawValue) }
            }
            Divider()
            Button("宏目录") { model.diagnostic(name: "macro") }
            Button("型号 / 密码信息") { model.diagnostic(name: "password") }
          }.fixedSize().disabled(model.selection == nil)
          Spacer()
          Button("清空日志") { model.log = [] }.disabled(model.log.isEmpty)
        }
        if diagnosticLog.isEmpty {
          Text("读取设备后，配置报告与操作记录会显示在这里。")
            .font(.callout).foregroundStyle(.secondary).padding(.vertical, 12)
        } else {
          ScrollView([.vertical, .horizontal]) {
            Text(diagnosticLog).font(.system(.caption, design: .monospaced))
              .textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).padding(12)
          }.frame(height: 220)
            .background(Color(nsColor: .textBackgroundColor))
            .clipShape(RoundedRectangle(cornerRadius: 6))
            .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(.separator.opacity(0.5)))
        }
      }
      DisclosureGroup("高级：手工 HID 报文") {
        VStack(alignment: .leading, spacing: 10) {
          Text("手工报文可能改变设备设置。发送前会要求确认，随后需要重新读取配置。")
            .font(.caption).foregroundStyle(.secondary)
          HStack(alignment: .top, spacing: 10) {
            TextField("十六进制 payload", text: $rawHex, axis: .vertical)
              .font(.system(.callout, design: .monospaced)).lineLimit(2...4)
            Button("发送…") { model.diagnostic(hex: rawHex) }.disabled(model.selection == nil)
          }
        }.padding(.top, 8)
      }
    }
  }

  private var diagnosticLog: String {
    var lines = model.log
    if let captured = model.captured {
      lines += captured.exchanges.map {
        "\($0.section) RX \($0.response.count) bytes: "
          + $0.response.prefix(32).map { String(format: "%02X", $0) }.joined(separator: " ")
      }
    }
    return lines.joined(separator: "\n")
  }

  private func diagnosticRow(_ title: String, _ value: String) -> some View {
    GridRow {
      fieldLabel(title)
      Text(value).textSelection(.enabled).foregroundStyle(.secondary)
    }
  }

  private func fieldLabel(_ title: String) -> some View {
    Text(title).frame(width: 104, alignment: .leading)
  }
}
