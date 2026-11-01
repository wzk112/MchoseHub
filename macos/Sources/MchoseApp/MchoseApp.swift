import AppKit
import MchoseCore
import MchoseHID
import SwiftUI
import UniformTypeIdentifiers

@main struct MchoseApp: App {
  @NSApplicationDelegateAdaptor(HubAppDelegate.self) private var delegate
  @State private var model = AppModel()
  var body: some Scene {
    Window("MCHOSE HUB", id: "main") {
      MainView(model: model).frame(minWidth: 1000, minHeight: 700)
        .onAppear { delegate.model = model }
    }
    .defaultSize(width: 1140, height: 760)
    .commands {
      CommandGroup(after: .newItem) {
        Button("刷新配置与设备") { model.refresh() }
          .keyboardShortcut("r").disabled(model.busy || model.recording)
        Button("导出当前配置…") { model.exportProfile() }
          .keyboardShortcut("s").disabled(model.profile == nil || model.busy)
      }
    }
    Settings { HubSettings(model: model) }
  }
}

enum SidebarDestination: Hashable {
  case local
  case device(UInt64)
}

@Observable @MainActor final class AppModel {
  let service: ConfigurationService
  private let scanner: @Sendable () async throws -> [HIDDeviceInfo]
  var schema: K87Schema?
  var devices: [HIDDeviceInfo] = []
  var selection: UInt64?
  var profile: KeyboardProfile?
  var savedProfile: KeyboardProfile?
  var scanMessage: String?
  var hasUnsavedChanges: Bool { profile != nil && profile != savedProfile }
  var hasDeviceChanges: Bool { captured != nil && profile != captured?.profile }
  var source = "独立预览"
  var message = "预览数据保存在内存，可编辑并导出为本地配置。"
  var busy = false
  var layer = 0
  var selectedKey: String?
  var captured: DeviceBackup?
  var errorMessage: String?
  var token = CancellationToken()
  var generation = UUID()
  var canEdit: Bool { profile != nil && !busy && !recording }
  var catalog: FunctionCatalog?
  var page = "按键"
  var profileName = ""
  var savedProfiles: [String] = []
  var macros: [KeyboardMacro] = [
    KeyboardMacro(
      name: "示例 A", steps: [MacroStep(key: "a", down: true), MacroStep(key: "a", down: false)])
  ]
  var savedMacros: [KeyboardMacro] = []
  var hasUnsavedMacros: Bool { macros != savedMacros }
  var macroIndex = 0
  var recording = false
  var uploadedMacros: [KeyboardMacro]?
  var uploadedOwner: UInt64?
  var log: [String] = []
  var theme = "system"
  var previewMode = true
  var selectedAudio: HIDDeviceInfo? { devices.first { $0.id == selection && $0.isAudio } }

  init(scanner: (@Sendable () async throws -> [HIDDeviceInfo])? = nil) {
    let service = ConfigurationService()
    self.service = service
    self.scanner = scanner ?? { try await service.scan() }
    do {
      let schema = try K87Schema.load()
      self.schema = schema
      profile = try PreviewProfile.make(schema: schema)
      savedProfile = profile
      catalog = try FunctionCatalog.load()
      savedMacros = macros
      refreshLibrary()
    } catch { errorMessage = error.localizedDescription }
  }
  func preview() {
    guard !busy, !recording, confirmDiscard() else { return }
    reset()
    selection = nil
    source = "独立预览"
    previewMode = true
    do {
      let schema = try KeyboardSchema.load(model: "K87")
      self.schema = schema
      profile = try PreviewProfile.make(schema: schema)
      savedProfile = profile
      refreshLibrary()
    } catch {
      errorMessage = error.localizedDescription
    }
    message = "预览数据保存在内存，可编辑并导出为本地配置。"
  }
  func reset() {
    token.cancel()
    token = CancellationToken()
    generation = UUID()
    profile = nil
    savedProfile = nil
    captured = nil
    selectedKey = nil
    layer = 0
    busy = false
    uploadedMacros = nil
    uploadedOwner = nil
    recording = false
  }
  var sidebarSelection: SidebarDestination { selection.map(SidebarDestination.device) ?? .local }
  func chooseSidebar(_ destination: SidebarDestination?) {
    guard let destination, destination != sidebarSelection, !busy, !recording else { return }
    switch destination {
    case .local: preview()
    case .device(let id): if let device = devices.first(where: { $0.id == id }) { select(device) }
    }
  }
  func select(_ device: HIDDeviceInfo) {
    guard !busy, !recording, device.id != selection, confirmDiscard() else { return }
    reset()
    selection = device.id
    source = device.product
    previewMode = false
    message = device.rejection ?? "接口已通过描述符检查。点击读取，建立完整备份。"
  }
  func confirmDiscard() -> Bool {
    guard hasUnsavedChanges else { return true }
    let alert = NSAlert()
    alert.messageText = "保留当前草稿？"
    alert.informativeText = "当前修改尚未保存。继续将替换草稿；也可以先导出配置。"
    alert.addButton(withTitle: "取消")
    alert.addButton(withTitle: "放弃草稿并继续")
    return alert.runModal() == .alertSecondButtonReturn
  }
  func refresh() {
    guard !busy, !recording else { return }
    if selection != nil && selectedAudio == nil {
      guard confirmDiscard() else { return }
      Task { await read() }
    } else {
      Task { await scanDevices() }
    }
  }
  func disconnect() {
    // Keep an edited draft available for export after a physical disconnect.
    let draft = profile
    let saved = savedProfile
    reset()
    selection = nil
    profile = draft
    savedProfile = saved
    source = draft == nil ? "设备已断开" : "设备已断开 · 本地草稿"
    message = "设备会话已取消。已有草稿仍可编辑和导出，重新连接后请读取设备。"
  }
  func scanDevices() async {
    do {
      let found = try await scanner().filter(\.isMchose)
      guard !Task.isCancelled else { return }
      let updated = Dictionary(grouping: found, by: \.physicalID).values.map { collections in
        collections.first(where: \.isSupported) ?? collections[0]
      }.sorted { $0.id < $1.id }
      if updated != devices { devices = updated }
      if scanMessage != nil { scanMessage = nil }
      if let selection, !devices.contains(where: { $0.id == selection }) { disconnect() }
    } catch is CancellationError { return } catch {
      let detail = "设备扫描失败，将自动重试：\(error.localizedDescription)"
      if scanMessage != detail {
        log.append(detail)
        scanMessage = detail
      }
    }
  }
  func monitor() async {
    while !Task.isCancelled {
      await scanDevices()
      do { try await Task.sleep(for: .seconds(1)) } catch { return }
    }
  }
  func read() async {
    guard let selection, !busy, !recording else { return }
    let current = generation
    let token = token
    busy = true
    captured = nil
    message = "正在读取并比较两份完整配置…"
    do {
      let result = try await service.backup(deviceID: selection, token: token)
      guard current == generation else { return }
      profile = result.profile
      savedProfile = result.profile
      captured = result
      schema = try KeyboardSchema.load(model: result.profile.model)
      refreshLibrary()
      message = "两次完整读取一致。\(result.battery.map { "电量 \($0)%。" } ?? "")可导出备份；不含完整宏库。"
    } catch {
      guard current == generation else { return }
      captured = nil
      errorMessage = error.localizedDescription
      message = "读取失败，设备会话未验证；已有草稿仍可导出。成功读取后才能应用。"
    }
    if current == generation { busy = false }
  }
  func makeDraft() {
    guard let copy = profile, !busy, !recording else { return }
    let saved = savedProfile
    reset()
    selection = nil
    profile = copy
    savedProfile = saved
    source = "本地草稿"
    message = "草稿与设备会话分离；编辑后可导出，不会自动写入设备。"
  }
  func importProfile() {
    guard !busy, !recording, confirmDiscard() else { return }
    let panel = NSOpenPanel()
    panel.allowedContentTypes = [.json]
    panel.allowsMultipleSelection = false
    guard panel.runModal() == .OK, let url = panel.url else { return }
    do {
      let loaded = try KeyboardProfile.read(from: url)
      if selection != nil && captured == nil {
        throw MchoseError.invalid("请先成功读取设备配置以确认型号，再导入配置。")
      }
      if selection != nil, loaded.model != captured?.profile.model {
        throw MchoseError.invalid("导入配置与当前设备型号不符。")
      }
      schema = try KeyboardSchema.load(model: loaded.model)
      profile = loaded
      savedProfile = loaded
      refreshLibrary()
      selectedKey = nil
      source = "已导入：\(url.deletingPathExtension().lastPathComponent)"
      message = "已校验 \(loaded.model) 配置，可编辑并另存。导入不会写入设备。"
    } catch { errorMessage = error.localizedDescription }
  }
  func exportProfile() {
    guard let profile, let schema else { return }
    let panel = NSSavePanel()
    panel.allowedContentTypes = [.json]
    panel.nameFieldStringValue = "\(profile.model)-profile.json"
    guard panel.runModal() == .OK, let url = panel.url else { return }
    do {
      try profile.save(to: url, schema: schema)
      // Raw report captures are explicitly exported alongside the requested backup.
      if let captured, profile == captured.profile {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let raw = url.deletingPathExtension().appendingPathExtension("reports.json")
        try encoder.encode(captured.exchanges).write(to: raw, options: .atomic)
      }
      savedProfile = profile
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
      schema.keyList.contains(where: { $0.n == selectedKey }),
      !schema.reservedKeys.contains(selectedKey)
    else { return }
    let value =
      target == "default"
      ? Data(repeating: 0, count: 4)
      : catalog?.functions.first(where: { $0.id == target }).map { Data($0.value) }
        ?? schema.board.first(where: { $0.key == target }).map { Data($0.def) }
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
  private let pages = [
    ("按键", "keyboard", "按键映射", "为默认层、Fn 和 Fn2 分配键盘、媒体与系统功能。"),
    ("灯光", "lightbulb", "灯光", "选择灯效，调整亮度，或设置每个按键的颜色。"),
    ("性能", "speedometer", "性能", "设置回报率、休眠和系统按键行为。"),
    ("宏", "record.circle", "宏编辑器", "创建、录制和管理键盘与鼠标事件。"),
    ("配置", "square.stack", "配置库", "按型号保存草稿，导入和导出配置文件。"),
    ("诊断", "waveform.path.ecg", "设备诊断", "查看连接状态、权限与通信记录。"),
  ]
  private var currentPage: (String, String, String, String) {
    pages.first { $0.0 == model.page } ?? pages[0]
  }
  private var selectedDevice: HIDDeviceInfo? { model.devices.first { $0.id == model.selection } }
  var body: some View {
    NavigationSplitView {
      VStack(spacing: 0) {
        deviceMenu.padding(.horizontal, 12).padding(.top, 14).padding(.bottom, 18)
        List(
          selection: Binding<String?>(
            get: { model.page }, set: { if let page = $0 { model.page = page } })
        ) {
          Section("键盘") {
            ForEach(Array(pages.prefix(4)), id: \.0) { item in
              Label(item.2, systemImage: item.1).padding(.vertical, 5).tag(item.0)
            }
          }
          Section("管理") {
            ForEach(Array(pages.suffix(2)), id: \.0) { item in
              Label(item.2, systemImage: item.1).padding(.vertical, 5).tag(item.0)
            }
          }
        }.listStyle(.sidebar).disabled(model.recording || model.selectedAudio != nil)
        Divider().padding(.horizontal, 16)
        HStack {
          SettingsLink { Label("设置", systemImage: "gearshape") }.buttonStyle(.plain)
          Spacer()
          Image(
            systemName: model.scanMessage == nil ? "checkmark.circle" : "exclamationmark.circle"
          )
          .foregroundStyle(model.scanMessage == nil ? Color.secondary : .orange)
          .help(model.scanMessage ?? "自动检测已连接的设备")
        }.font(.callout).foregroundStyle(.secondary).padding(18)
      }.navigationSplitViewColumnWidth(min: 210, ideal: 220, max: 260)
    } detail: {
      ScrollView {
        VStack(alignment: .leading, spacing: 26) {
          VStack(alignment: .leading, spacing: 6) {
            Text(model.selectedAudio == nil ? currentPage.2 : "音频设备")
              .font(.system(size: 24, weight: .semibold))
            Text(model.selectedAudio == nil ? currentPage.3 : "管理灯光、声音预设与语音播报。")
              .font(.callout).foregroundStyle(.secondary)
          }
          if let audio = model.selectedAudio {
            AudioPanel(device: audio, busy: $model.busy).id(audio.id)
          } else if model.profile != nil || ["宏", "配置", "诊断"].contains(model.page) {
            if model.page == "按键" { keyboard } else { FeaturePanels(model: model) }
          } else {
            VStack(spacing: 16) {
              ContentUnavailableView(
                "先读取键盘配置", systemImage: "keyboard",
                description: Text("从设备读取一份完整配置后，即可编辑灯光、性能和按键。"))
              Button("读取配置", systemImage: "arrow.clockwise") { model.refresh() }
                .hubProminent().disabled(model.busy)
              Text("尚未授予输入监控权限时，可从左上角切换到独立预览。")
                .font(.caption).foregroundStyle(.secondary)
            }.frame(maxWidth: .infinity, minHeight: 330)
          }
        }
        .frame(maxWidth: 940, alignment: .leading)
        .padding(28)
        .frame(maxWidth: .infinity, alignment: .topLeading)
        // Resolve the split-view's animated geometry before laying out its descendants.
        .geometryGroup()
      }
      .background(Color(nsColor: .windowBackgroundColor))
      .navigationTitle(
        model.selection == nil
          ? "MCHOSE HUB"
          : (model.captured?.profile.model ?? selectedDevice?.product ?? "MCHOSE HUB")
      )
      .toolbar {
        ToolbarItemGroup(placement: .primaryAction) {
          Button {
            model.refresh()
          } label: {
            Label("刷新", systemImage: "arrow.clockwise")
          }
          .help("刷新设备配置（⌘R）")
          .disabled(model.busy || model.recording || model.selectedAudio != nil)
          Menu {
            Button("导入配置…", systemImage: "square.and.arrow.down") { model.importProfile() }
            Button("导出配置…", systemImage: "square.and.arrow.up") { model.exportProfile() }
              .disabled(model.profile == nil)
            if model.selection != nil && model.profile != nil {
              Divider()
              Button("建立独立草稿", systemImage: "doc.on.doc") { model.makeDraft() }
            }
          } label: {
            Label("文件", systemImage: "doc")
          }
          .disabled(model.busy || model.recording || model.selectedAudio != nil)
          if model.selection != nil && model.selectedAudio == nil {
            Button("应用修改") { model.confirmApply() }.hubProminent()
              .disabled(!model.canEdit || !model.hasDeviceChanges || model.captured == nil)
          }
        }
      }
      .safeAreaInset(edge: .bottom, spacing: 0) { statusBar }
    }
    .navigationSplitViewStyle(.balanced)
    .task { await model.monitor() }
    .preferredColorScheme(model.theme == "dark" ? .dark : model.theme == "light" ? .light : nil)
    .alert(
      "操作未完成",
      isPresented: Binding(
        get: { model.errorMessage != nil }, set: { if !$0 { model.errorMessage = nil } })
    ) {
      Button("好") { model.errorMessage = nil }
      if model.errorMessage?.contains("输入监控") == true {
        Button("打开输入监控设置") { openInputMonitoring() }
      }
    } message: {
      Text(model.errorMessage ?? "")
    }
  }
  var deviceMenu: some View {
    Menu {
      Button {
        model.chooseSidebar(.local)
      } label: {
        Label("独立预览", systemImage: model.selection == nil ? "checkmark" : "keyboard")
      }
      if !model.devices.isEmpty { Divider() }
      ForEach(model.devices) { device in
        Button {
          model.chooseSidebar(.device(device.id))
        } label: {
          Label(
            device.product,
            systemImage: model.selection == device.id
              ? "checkmark" : device.isAudio ? "headphones" : "keyboard")
        }
      }
      Divider()
      Button("导入本地配置…", systemImage: "square.and.arrow.down") { model.importProfile() }
    } label: {
      Label(
        selectedDevice?.product ?? "独立预览 · \(model.profile?.model ?? "K87")",
        systemImage: selectedDevice?.isAudio == true ? "headphones" : "keyboard"
      )
      .font(.callout.weight(.medium)).lineLimit(1)
    }.controlSize(.large).frame(maxWidth: .infinity)
      .disabled(model.busy || model.recording)
      .help("切换设备或使用独立预览")
      .accessibilityLabel("选择设备或独立预览")
  }

  var statusBar: some View {
    HStack(spacing: 8) {
      if model.busy {
        ProgressView().controlSize(.mini)
      } else {
        Image(
          systemName: model.hasUnsavedChanges
            ? "pencil.circle" : model.selection == nil ? "doc" : "cable.connector")
      }
      Text(
        model.busy
          ? "正在处理设备请求…"
          : model.hasUnsavedChanges
            ? "草稿有未保存修改"
            : model.selection == nil
              ? "本地草稿 · 不会自动写入设备"
              : model.captured == nil
                ? "设备已连接 · 尚未读取配置" : model.hasDeviceChanges ? "配置已保存 · 尚未应用到设备" : "设备配置已读取"
      )
      .lineLimit(1)
      Spacer(minLength: 10)
      if let battery = model.captured?.battery {
        Label("\(battery)%", systemImage: "battery.100percent").monospacedDigit()
      }
      Text(
        model.selection == nil
          ? (model.profile?.model ?? "") : (model.captured?.profile.model ?? "待读取确认")
      ).foregroundStyle(.tertiary)
    }.font(.caption).foregroundStyle(.secondary).padding(.horizontal, 22).frame(height: 34)
      .background(.bar).overlay(alignment: .top) { Divider() }.help(model.message)
  }
  var keyboard: some View {
    VStack(alignment: .leading, spacing: 22) {
      HStack {
        Picker("按键层", selection: $model.layer) {
          Text("默认层").tag(0)
          Text("Fn").tag(1)
          Text("Fn2").tag(2)
        }.pickerStyle(.segmented).frame(width: 290).labelsHidden().disabled(!model.canEdit)
          .onChange(of: model.layer) { model.selectedKey = nil }
        Spacer()
        Text("\(model.schema?.board.count ?? 0) 个按键").font(.caption).foregroundStyle(.secondary)
      }
      if let schema = model.schema {
        KeyboardDiagram(keys: schema.board, selected: model.selectedKey) { model.selectedKey = $0 }
          .frame(height: 270).disabled(!model.canEdit)
      }
      Divider()
      HStack(alignment: .center, spacing: 20) {
        VStack(alignment: .leading, spacing: 7) {
          Text(model.selectedKey == nil ? "选择要修改的按键" : "\(model.selectedKey!) 的功能").font(.headline)
          Text(
            model.selectedKey == nil
              ? "点击上方键帽，再从右侧选择功能。" : String(model.selectedValue.split(separator: "·").first ?? "")
          )
          .font(.callout).foregroundStyle(.secondary)
        }
        Spacer()
        mappingMenu
      }.frame(minHeight: 64)
      if model.schema?.reservedKeys.contains(model.selectedKey ?? "") == true {
        Label("此按键由固件保留，不能修改映射。", systemImage: "lock").font(.caption).foregroundStyle(.secondary)
      }
    }
  }
  var mappingMenu: some View {
    Menu("选择功能") {
      Button("使用固件默认映射") { model.remap(to: "default") }
      Divider()
      Menu("键盘按键") {
        ForEach(model.schema?.board ?? []) { key in Button(key.name) { model.remap(to: key.key) } }
      }
      ForEach([("key", "键盘功能"), ("mouse", "鼠标"), ("media", "媒体"), ("system", "系统")], id: \.0) {
        group, title in
        Menu(title) {
          ForEach(model.catalog?.functions.filter { $0.group == group } ?? []) { function in
            Button(function.label) { model.remap(to: function.id) }
          }
        }
      }
    }.fixedSize().disabled(
      !model.canEdit || model.selectedKey == nil
        || model.schema?.reservedKeys.contains(model.selectedKey ?? "") == true)
  }
}

struct KeyboardDiagram: View {
  let keys: [BoardKey]
  let selected: String?
  var colors: [String: Color] = [:]
  let select: (String) -> Void
  var body: some View {
    GeometryReader { geometry in
      let layout = KeyboardLayout(keys: keys)
      let width = max(1, CGFloat(layout.width))
      let height = max(1, CGFloat(layout.height))
      let scale = min(1.1, min(geometry.size.width / width, geometry.size.height / height))
      let offset = max(0, (geometry.size.width - width * scale) / 2)
      HubGlassGroup(spacing: 2) {
        ZStack(alignment: .topLeading) {
          ForEach(layout.placements) { item in
            Button {
              select(item.key.key)
            } label: {
              Text(item.key.name).font(
                .system(size: 10, weight: selected == item.key.key ? .semibold : .regular)
              )
              .lineLimit(1).minimumScaleFactor(0.6)
              .frame(width: CGFloat(item.width), height: CGFloat(item.height))
              .contentShape(RoundedRectangle(cornerRadius: 6))
            }.buttonStyle(.plain)
              .hubGlass(
                cornerRadius: 6,
                tint: selected == item.key.key
                  ? .accentColor.opacity(0.3) : colors[item.key.key]?.opacity(0.2),
                interactive: true
              )
              .overlay(alignment: .bottom) {
                if let color = colors[item.key.key] {
                  RoundedRectangle(cornerRadius: 2).fill(color).frame(height: 3).padding(
                    .horizontal, 5
                  ).padding(.bottom, 3)
                }
              }
              .overlay(
                RoundedRectangle(cornerRadius: 6).strokeBorder(
                  selected == item.key.key ? Color.accentColor : .clear, lineWidth: 2)
              )
              .accessibilityLabel(item.key.name).accessibilityAddTraits(
                selected == item.key.key ? .isSelected : []
              )
              .offset(x: CGFloat(item.x), y: CGFloat(item.y))
          }
        }.frame(width: width, height: height, alignment: .topLeading)
      }
      // Give the glass container the same explicit transform origin as the key canvas.
      .frame(width: width, height: height, alignment: .topLeading)
      // Keep keycaps in one coordinate space while the native sidebar resizes the detail.
      .scaleEffect(scale, anchor: .topLeading)
      .offset(x: offset)
    }
    .geometryGroup()
  }
}
