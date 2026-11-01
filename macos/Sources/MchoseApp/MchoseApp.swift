import AppKit
import MchoseCore
import MchoseHID
import SwiftUI
import UniformTypeIdentifiers

@main struct MchoseApp: App {
  @State private var model = AppModel()
  var body: some Scene {
    WindowGroup("MCHOSE HUB") { MainView(model: model).frame(minWidth: 960, minHeight: 650) }
      .defaultSize(width: 1140, height: 760)
    Settings {
      VStack(alignment: .leading, spacing: 12) {
        Text("MCHOSE HUB · macOS 开发预览").font(.headline)
        Text("Swift 原生键盘配置、宏与音频 HID 客户端。配置不含完整宏库。")
        Text("USB、2.4G 和各型号的实机验证范围见随包验证记录。")
      }.padding(24).frame(width: 430)
    }
  }
}

@Observable @MainActor final class AppModel {
  let service = ConfigurationService()
  var schema: K87Schema?
  var devices: [HIDDeviceInfo] = []
  var selection: UInt64?
  var profile: KeyboardProfile?
  var source = "独立预览"
  var message = "预览数据保存在内存，可编辑并导出为本地配置。"
  var busy = false
  var layer = 0
  var selectedKey: String?
  var captured: DeviceBackup?
  var errorMessage: String?
  var token = CancellationToken()
  var generation = UUID()
  var canEdit: Bool { profile != nil && !busy }
  var catalog: FunctionCatalog?
  var page = "按键"
  var profileName = ""
  var savedProfiles: [String] = []
  var macros: [KeyboardMacro] = [
    KeyboardMacro(
      name: "示例 A", steps: [MacroStep(key: "a", down: true), MacroStep(key: "a", down: false)])
  ]
  var macroIndex = 0
  var recording = false
  var uploadedMacros: [KeyboardMacro]?
  var uploadedOwner: UInt64?
  var log: [String] = []
  var theme = "system"
  var previewMode = true
  var selectedAudio: HIDDeviceInfo? { devices.first { $0.id == selection && $0.isAudio } }

  init() {
    do {
      let schema = try K87Schema.load()
      self.schema = schema
      profile = try PreviewProfile.make(schema: schema)
      catalog = try FunctionCatalog.load()
      refreshLibrary()
    } catch { errorMessage = error.localizedDescription }
  }
  func preview() {
    reset()
    selection = nil
    source = "独立预览"
    previewMode = true
    do { if let schema { profile = try PreviewProfile.make(schema: schema) } } catch {
      errorMessage = error.localizedDescription
    }
    message = "预览数据保存在内存，可编辑并导出为本地配置。"
  }
  func reset() {
    token.cancel()
    token = CancellationToken()
    generation = UUID()
    profile = nil
    captured = nil
    selectedKey = nil
    layer = 0
    busy = false
    uploadedMacros = nil
    uploadedOwner = nil
    recording = false
  }
  func select(_ device: HIDDeviceInfo) {
    reset()
    selection = device.id
    source = device.product
    previewMode = false
    message = device.rejection ?? "接口已通过描述符检查。点击读取，建立完整备份。"
  }
  func monitor() async {
    while !Task.isCancelled {
      do {
        let found = try await service.scan().filter(\.isMchose)
        devices = Dictionary(grouping: found, by: \.physicalID).values.map { collections in
          collections.first(where: \.isSupported) ?? collections[0]
        }.sorted { $0.id < $1.id }
        if let selection, !devices.contains(where: { $0.id == selection }) {
          reset()
          self.selection = nil
          source = "设备已断开"
          message = "旧会话已取消，请重新选择设备。"
        }
        try await Task.sleep(for: .seconds(1))
      } catch is CancellationError { return } catch {
        errorMessage = error.localizedDescription
        return
      }
    }
  }
  func read() async {
    guard let selection, !busy else { return }
    let current = generation
    let token = token
    busy = true
    profile = nil
    captured = nil
    message = "正在读取并比较两份完整配置…"
    do {
      let result = try await service.backup(deviceID: selection, token: token)
      guard current == generation else { return }
      profile = result.profile
      captured = result
      schema = try KeyboardSchema.load(model: result.profile.model)
      refreshLibrary()
      message = "两次完整读取一致。\(result.battery.map { "电量 \($0)%。" } ?? "")可导出备份；不含完整宏库。"
    } catch {
      guard current == generation else { return }
      profile = nil
      captured = nil
      errorMessage = error.localizedDescription
      message = "读取失败，当前配置已失效。重新读取后再使用。"
    }
    if current == generation { busy = false }
  }
  func makeDraft() {
    guard let copy = profile else { return }
    reset()
    selection = nil
    profile = copy
    source = "本地草稿"
    message = "草稿与设备会话分离；编辑后可导出，不会自动写入设备。"
  }
  func importProfile() {
    let panel = NSOpenPanel()
    panel.allowedContentTypes = [.json]
    panel.allowsMultipleSelection = false
    guard panel.runModal() == .OK, let url = panel.url else { return }
    do {
      let loaded = try KeyboardProfile.read(from: url)
      if selection != nil, loaded.model != schema?.model {
        throw MchoseError.invalid("导入配置与当前设备型号不符。")
      }
      schema = try KeyboardSchema.load(model: loaded.model)
      profile = loaded
      selectedKey = nil
      source = "已导入：\(url.deletingPathExtension().lastPathComponent)"
      message = "已校验 K87 配置，可编辑并另存。导入不会写入设备。"
    } catch { errorMessage = error.localizedDescription }
  }
  func exportProfile() {
    guard let profile, let schema else { return }
    let panel = NSSavePanel()
    panel.allowedContentTypes = [.json]
    panel.nameFieldStringValue = "K87-profile.json"
    guard panel.runModal() == .OK, let url = panel.url else { return }
    do {
      try profile.save(to: url, schema: schema)
      // Raw report captures are explicitly exported alongside the requested backup.
      if let captured {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let raw = url.deletingPathExtension().appendingPathExtension("reports.json")
        try encoder.encode(captured.exchanges).write(to: raw, options: .atomic)
      }
      message = "已导出：\(url.lastPathComponent)。配置不含完整宏库。"
    } catch { errorMessage = error.localizedDescription }
  }
  func number(_ key: String) -> Int { profile?.performance.numbers[key] ?? 0 }
  func setNumber(_ key: String, _ value: Int) {
    guard canEdit, profile?.performance.numbers[key] != nil else { return }
    profile?.performance.numbers[key] = value
  }
  func remap(to target: String) {
    guard canEdit, let schema, let selectedKey,
      let i = schema.keyList.firstIndex(where: { $0.n == selectedKey }),
      !schema.reservedKeys.contains(selectedKey)
    else { return }
    let value =
      target == "default"
      ? Data(repeating: 0, count: 4)
      : catalog?.functions.first(where: { $0.id == target }).map { Data($0.value) }
        ?? schema.board.first(where: { $0.key == target }).map { Data($0.def) }
    _ = i
    do {
      if let value {
        try profile?.remap(key: selectedKey, layer: layer, value: value, schema: schema)
      }
    } catch { errorMessage = error.localizedDescription }
  }
  var selectedValue: String {
    guard let selectedKey, let schema,
      let i = schema.keyList.firstIndex(where: { $0.n == selectedKey }),
      let table = profile?.keyLayers[layer].tables["#keyList"]
    else { return "—" }
    let bytes = table[i]
    if bytes.allSatisfy({ $0 == 0 }) { return "使用固件默认映射 · 00 00 00 00" }
    let name =
      bytes.first == 3
      ? "宏 #\(Int(bytes[3])+1)"
      : catalog?.functions.first(where: { Data($0.value) == bytes })?.label ?? schema.board.first(
        where: { Data($0.def) == bytes })?.name ?? "自定义功能"
    return "\(name) · " + bytes.map { String(format: "%02X", $0) }.joined(separator: " ")
  }
}

struct MainView: View {
  @Bindable var model: AppModel
  var body: some View {
    NavigationSplitView {
      List {
        Section("本地") {
          Button {
            model.preview()
          } label: {
            Label("K87 独立预览", systemImage: "keyboard")
          }.buttonStyle(.plain)
          Button {
            model.importProfile()
          } label: {
            Label("导入配置…", systemImage: "square.and.arrow.down")
          }.buttonStyle(.plain)
        }
        Section("USB 设备") {
          if model.devices.isEmpty { Text("未发现 MCHOSE 设备").foregroundStyle(.secondary) }
          ForEach(model.devices) { device in
            Button {
              model.select(device)
            } label: {
              VStack(alignment: .leading, spacing: 5) {
                Text(device.product).font(.headline)
                Text(device.isSupported ? (device.isWireless ? "2.4G 配置接口" : "USB 配置接口") : "未支持的集合")
                  .font(.caption).foregroundStyle(.secondary)
              }
            }.buttonStyle(.plain).padding(.vertical, 5)
          }
        }
      }.navigationTitle("MCHOSE HUB").navigationSplitViewColumnWidth(min: 220, ideal: 250)
    } detail: {
      ScrollView {
        VStack(alignment: .leading, spacing: 22) {
          if let audio = model.selectedAudio {
            AudioPanel(device: audio).id(audio.id)
          } else {
            HStack {
              VStack(alignment: .leading, spacing: 6) {
                Text(model.schema?.model ?? "MCHOSE").font(.largeTitle.bold())
                Text(model.source).foregroundStyle(.secondary)
              }
              Spacer()
              if model.busy { ProgressView().controlSize(.small) }
              if model.selection != nil {
                Button("读取完整配置") { Task { await model.read() } }.disabled(model.busy)
              }
              Button("导出配置…") { model.exportProfile() }.disabled(model.profile == nil || model.busy)
            }
            Text(model.message).font(.callout).foregroundStyle(.secondary).textSelection(.enabled)
            Picker("功能", selection: $model.page) {
              ForEach(["按键", "灯光", "性能", "宏", "配置", "诊断"], id: \.self) { Text($0).tag($0) }
            }.pickerStyle(.segmented)
            if model.profile != nil {
              if model.selection != nil {
                HStack {
                  Button("应用当前草稿到设备…") { model.confirmApply() }.disabled(model.busy)
                  Button("建立独立本地草稿") { model.makeDraft() }
                }
              }
              if model.page == "按键" { keyboard } else { FeaturePanels(model: model) }
            } else {
              if ["宏", "配置", "诊断"].contains(model.page) {
                FeaturePanels(model: model)
              } else {
                ContentUnavailableView(
                  "连接键盘", systemImage: "cable.connector",
                  description: Text("选择已识别的 USB 或 2.4G 配置接口后读取。也可以使用独立预览。"))
              }
            }
            Text("Swift 原生开发版 · 配置不含完整宏库 · 实机验证范围见验证记录").font(.caption).foregroundStyle(.secondary)
          }
        }.padding(28)
      }.navigationTitle("键盘配置")
    }
    .task { await model.monitor() }
    .preferredColorScheme(model.theme == "dark" ? .dark : model.theme == "light" ? .light : nil)
    .alert(
      "操作未完成",
      isPresented: Binding(
        get: { model.errorMessage != nil }, set: { if !$0 { model.errorMessage = nil } })
    ) {
      Button("好") { model.errorMessage = nil }
      if model.errorMessage?.contains("输入监控") == true {
        Button("打开输入监控设置") {
          NSWorkspace.shared.open(
            URL(
              string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent")!
          )
        }
      }
    } message: {
      Text(model.errorMessage ?? "")
    }
  }
  var keyboard: some View {
    GroupBox {
      VStack(alignment: .leading, spacing: 16) {
        Picker("按键层", selection: $model.layer) {
          Text("Default").tag(0)
          Text("Fn").tag(1)
          Text("Fn2").tag(2)
        }.pickerStyle(.segmented).frame(width: 300)
          .onChange(of: model.layer) { model.selectedKey = nil }
        if let schema = model.schema {
          KeyboardDiagram(keys: schema.board, selected: model.selectedKey) {
            model.selectedKey = $0
          }
          .frame(height: 285)
        }
        HStack {
          VStack(alignment: .leading, spacing: 4) {
            Text(model.selectedKey ?? "选择一个按键").font(.headline)
            Text(model.selectedValue).font(.system(.caption, design: .monospaced)).foregroundStyle(
              .secondary)
          }
          Spacer()
          Menu("修改映射") {
            Button("使用固件默认映射") { model.remap(to: "default") }
            ForEach(model.schema?.board ?? []) { key in
              Button(key.name) { model.remap(to: key.key) }
            }
            ForEach(["key", "mouse", "media", "system"], id: \.self) { group in
              Menu(group) {
                ForEach(model.catalog?.functions.filter { $0.group == group } ?? []) { function in
                  Button(function.label) { model.remap(to: function.id) }
                }
              }
            }
          }.fixedSize().disabled(!model.canEdit || model.selectedKey == nil)
        }
      }.padding(10)
    } label: {
      Label("三层按键", systemImage: "keyboard")
    }
  }
  var settings: some View {
    HStack(alignment: .top, spacing: 18) {
      GroupBox {
        VStack(alignment: .leading, spacing: 14) {
          Text("灯效编号：\(model.number("lightMode"))")
          let key = "brightness\(model.number("lightMode"))"
          if model.profile?.performance.numbers[key] != nil {
            Text("当前亮度：\(model.number(key))%")
            Slider(
              value: Binding(
                get: { Double(model.number(key)) }, set: { model.setNumber(key, Int($0.rounded())) }
              ), in: 0...100, step: 1
            )
            .disabled(!model.canEdit)
          } else {
            Text("此灯效没有独立亮度字段").foregroundStyle(.secondary)
          }
          Text("保留非当前灯效的全部参数与逐键颜色。").font(.caption).foregroundStyle(.secondary)
        }.padding(10).frame(maxWidth: .infinity, alignment: .leading)
      } label: {
        Label("灯光", systemImage: "lightbulb")
      }
      GroupBox {
        VStack(alignment: .leading, spacing: 12) {
          Picker(
            "回报率档位",
            selection: Binding(
              get: { model.number("rateVal") }, set: { model.setNumber("rateVal", $0) })
          ) {
            Text("1").tag(1)
            Text("2").tag(2)
            Text("3").tag(3)
          }.disabled(!model.canEdit)
          Stepper(
            "休眠原始值：\(model.number("sleepTimeVal"))",
            value: Binding(
              get: { model.number("sleepTimeVal") }, set: { model.setNumber("sleepTimeVal", $0) }),
            in: 0...255
          ).disabled(!model.canEdit)
          Text("草稿编辑不会自动应用到设备。").font(.caption).foregroundStyle(.secondary)
        }.padding(10).frame(maxWidth: .infinity, alignment: .leading)
      } label: {
        Label("性能", systemImage: "speedometer")
      }
    }
  }
}

struct KeyboardDiagram: View {
  let keys: [BoardKey]
  let selected: String?
  var colors: [String: Color] = [:]
  let select: (String) -> Void
  var body: some View {
    GeometryReader { geometry in
      let naturalWidth = positions.map { $0.x + CGFloat($0.key.width) }.max() ?? 836
      let scale = min(1, geometry.size.width / naturalWidth)
      ZStack(alignment: .topLeading) {
        ForEach(positions, id: \.key.id) { item in
          Button {
            select(item.key.key)
          } label: {
            Text(item.key.name).font(.system(size: 10)).lineLimit(1).minimumScaleFactor(0.5)
              .frame(width: CGFloat(item.key.width) * scale, height: 36 * scale)
              .background(
                selected == item.key.key
                  ? Color.accentColor.opacity(0.25)
                  : colors[item.key.key]?.opacity(0.35) ?? Color(nsColor: .controlBackgroundColor),
                in: RoundedRectangle(cornerRadius: 5)
              )
              .overlay(
                RoundedRectangle(cornerRadius: 5).stroke(
                  selected == item.key.key ? Color.accentColor : Color.secondary.opacity(0.2)))
          }.buttonStyle(.plain).offset(x: item.x * scale, y: item.y * scale)
        }
      }.frame(width: geometry.size.width, height: geometry.size.height, alignment: .topLeading)
    }
  }
  private var positions: [(key: BoardKey, x: CGFloat, y: CGFloat)] {
    var x: CGFloat = 0
    var y: CGFloat = 0
    return keys.map { key in
      if key.left == 0 && x > 0 {
        x = 0
        y += CGFloat(42 + key.top)
      }
      x += CGFloat(key.left)
      let position = (key, x, y)
      x += CGFloat(key.width + key.right)
      return position
    }
  }
}
