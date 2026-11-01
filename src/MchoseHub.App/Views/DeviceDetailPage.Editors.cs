using System.Text.Json;
using MchoseHub.Core.Protocol;
using MchoseHub.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MchoseHub.App.Views;

public sealed partial class DeviceDetailPage
{
    private bool _syncingMacroUi;
    private long _macroDraftRevision;
    private long _profileDraftRevision;
    private bool _macroMetadataDirty;
    private int _displayedMacroIndex;
    private string _committedMacroName = "";
    private int _committedMacroTriggerIndex = -1;
    private readonly Dictionary<string, Button> _remapButtons = new(StringComparer.OrdinalIgnoreCase);
    private KeyboardProfileLibrary ProfileLibrary => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MchoseHub", _preview ? "preview-profiles" : "profiles"));
    public sealed record MacroStepRow(string Key, string Action, string Delay);
    private bool CurrentSettings(KeyboardSettings settings) => !_unloaded && ReferenceEquals(settings, Settings);

    private void InitializeEditors()
    {
        MacroStepKeyBox.ItemsSource = KeyboardFunctions.MacroKeys.Keys.Concat(new[] { "Mouse:Left", "Mouse:Right", "Mouse:Middle", "Mouse:Forward", "Mouse:Back" }).ToList();
        MacroStepKeyBox.SelectedItem = "a";
        // A focusable non-text control keeps recording separate from text input/IME editing.
        MacroRecorder.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRecordKeyDown), true);
        MacroRecorder.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(OnRecordKeyUp), true);
        MacroRecorder.PointerPressed += (_, _) => MacroRecorder.Focus(FocusState.Pointer);
        MacroNameBox.TextChanged += (_, _) => MacroMetadataChanged();
        MacroTriggerBox.SelectionChanged += (_, _) => MacroMetadataChanged();
        MacroRecorder.LostFocus += (_, _) => PauseMacroRecording();
        ProfileEditor.TextChanged += (_, _) => _profileDraftRevision++;
        Unloaded += (_, _) =>
        {
            PauseMacroRecording();
            _macroDraftRevision++; _profileDraftRevision++;
            _uploadedMacros = null; _macroOwner = null; _uploadedMacroRevision = -1;
        };
        MacroEditor.TextChanged += (_, _) =>
        {
            _macroDraftRevision++;
            _uploadedMacros = null; _macroOwner = null; _uploadedMacroRevision = -1;
            if (_syncingMacroUi) return;
            try { RefreshMacroUi(); }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException)
            {
                MacroStatus.Text = $"宏 JSON 无效：{ex.Message}";
                _syncingMacroUi = true;
                try
                {
                    MacroSelectionBox.ItemsSource = null; MacroStepsList.ItemsSource = null;
                    MacroNameBox.Text = ""; MacroTriggerBox.SelectedIndex = -1;
                    _committedMacroName = ""; _committedMacroTriggerIndex = -1;
                    MacroUploadButton.IsEnabled = MacroBindButton.IsEnabled = false;
                    _macroMetadataDirty = false;
                }
                finally { _syncingMacroUi = false; }
            }
        };
        RefreshMacroUi();
    }

    private void MacroMetadataChanged()
    {
        if (_syncingMacroUi) return;
        bool dirty = MacroMetadataDiffers();
        if (!dirty)
        {
            // XAML can deliver TextChanged after a programmatic refresh has finished.
            // Ignore such notifications and restore actions if the user reverted a draft.
            if (!_macroMetadataDirty) return;
            _macroMetadataDirty = false;
            RefreshMacroActions();
            MacroStatus.Text = "已恢复已提交的宏信息。";
            return;
        }
        _macroMetadataDirty = true; _macroDraftRevision++;
        _uploadedMacros = null; _macroOwner = null; _uploadedMacroRevision = -1;
        MacroUploadButton.IsEnabled = MacroBindButton.IsEnabled = false;
        MacroStatus.Text = "名称或触发方式有未应用修改，请先点击更新宏信息。";
    }
    private void RequireCommittedMacroMetadata()
    {
        if (MacroMetadataDiffers()) throw new InvalidOperationException("请先点击更新宏信息，保留名称或触发方式的修改。");
    }
    private bool MacroMetadataDiffers() => !string.Equals(MacroNameBox.Text, _committedMacroName, StringComparison.Ordinal)
        || MacroTriggerBox.SelectedIndex != _committedMacroTriggerIndex;
    private void RefreshMacroActions()
    {
        if (_unloaded) return;
        bool valid;
        try { ParseMacroJson(MacroEditor.Text); valid = true; }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException) { valid = false; }
        bool ready = valid && !_advancedBusy && !MacroMetadataDiffers() && !_recording && _recordingBuffer.Steps.Count == 0;
        MacroUploadButton.IsEnabled = ready;
        MacroBindButton.IsEnabled = ready && _uploadedMacros is not null && ReferenceEquals(Settings, _macroOwner) && _uploadedMacroRevision == _macroDraftRevision;
    }
    private List<KeyboardMacro> EditorMacros()
    {
        RequireCommittedMacroMetadata();
        return ParseMacroEditor().ToList();
    }
    private void RefreshMacroUi(int? select = null)
    {
        if (_syncingMacroUi) return;
        var macros = ParseMacroEditor().ToList();
        int index = Math.Clamp(select ?? MacroSelectionBox.SelectedIndex, 0, macros.Count - 1);
        _syncingMacroUi = true;
        try
        {
            var macro = macros[index];
            _committedMacroName = macro.Name;
            _committedMacroTriggerIndex = macro.Trigger switch { MacroTrigger.Once => 0, MacroTrigger.Hold => 1, _ => 2 };
            MacroSelectionBox.ItemsSource = macros.Select((m, i) => $"{i + 1:00}   {m.Name}").ToList();
            MacroSelectionBox.SelectedIndex = index;
            _displayedMacroIndex = index;
            MacroNameBox.Text = macro.Name;
            MacroTriggerBox.SelectedIndex = _committedMacroTriggerIndex;
            MacroStepsList.ItemsSource = macro.Steps.Select(s => new MacroStepRow(s.Mouse ? $"鼠标 {s.Key}" : s.Key, s.Down ? "按下" : "抬起", $"{s.DelayMs} ms")).ToList();
            MacroIndexBox.Value = index + 1;
            _macroMetadataDirty = false;
            RefreshMacroActions();
        }
        finally { _syncingMacroUi = false; }
    }
    private void SetEditorMacros(IReadOnlyList<KeyboardMacro> macros, int index)
    {
        KeyboardMacroCodec.EncodeBank(macros);
        _syncingMacroUi = true;
        try { MacroEditor.Text = JsonSerializer.Serialize(macros, EditorJson); }
        finally { _syncingMacroUi = false; }
        RefreshMacroUi(index);
        MacroStatus.Text = $"已更新本地草稿 · {macros.Count} 个宏。上传后才会写入键盘。";
    }
    private void OnMacroSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingMacroUi || MacroSelectionBox.SelectedIndex == _displayedMacroIndex) return;
        if (MacroMetadataDiffers())
        {
            _syncingMacroUi = true;
            try { MacroSelectionBox.SelectedIndex = _displayedMacroIndex; }
            finally { _syncingMacroUi = false; }
            MacroStatus.Text = "请先更新宏信息，再切换宏；当前输入已保留。";
            return;
        }
        if (MacroSelectionBox.SelectedIndex >= 0) Run(() => { RefreshMacroUi(); return Task.CompletedTask; }, "宏加载失败");
    }
    private int SelectedMacroIndex(List<KeyboardMacro> macros) => Math.Clamp(MacroSelectionBox.SelectedIndex, 0, macros.Count - 1);
    private void OnUpdateMacroMetadata(object sender, RoutedEventArgs e) => Run(() =>
    {
        var macros = ParseMacroEditor().ToList(); int i = SelectedMacroIndex(macros);
        macros[i] = macros[i] with { Name = MacroNameBox.Text.Trim(), Trigger = MacroTriggerBox.SelectedIndex switch { 1 => MacroTrigger.Hold, 2 => MacroTrigger.Toggle, _ => MacroTrigger.Once } };
        SetEditorMacros(macros, i); return Task.CompletedTask;
    }, "宏设置无效");
    private static int ValidDelay(double value)
    {
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < 0 || value > 60000) throw new ArgumentException("延时须为 0–60000 的整数毫秒。");
        return (int)value;
    }
    private void OnApplyMacroDelay(object sender, RoutedEventArgs e) => Run(() =>
    {
        var macros = EditorMacros(); int i = SelectedMacroIndex(macros); int delay = ValidDelay(MacroDelayBox.Value);
        macros[i] = macros[i] with { Steps = macros[i].Steps.Select(s => s with { DelayMs = delay }).ToArray() };
        SetEditorMacros(macros, i); return Task.CompletedTask;
    }, "修改延时失败");
    private void OnNewMacro(object sender, RoutedEventArgs e) => Run(() =>
    {
        var macros = EditorMacros(); int number = 1;
        while (macros.Any(m => m.Name == $"新宏 {number}")) number++;
        macros.Add(new KeyboardMacro($"新宏 {number}", MacroTrigger.Once, new[] { new MacroStep("a", true), new MacroStep("a", false) }));
        SetEditorMacros(macros, macros.Count - 1); return Task.CompletedTask;
    }, "新建宏失败");
    private void OnDeleteMacro(object sender, RoutedEventArgs e) => Run(() =>
    {
        var macros = EditorMacros(); int i = SelectedMacroIndex(macros);
        if (macros.Count == 1) throw new InvalidOperationException("宏库需保留至少一个宏。可修改当前宏的事件。");
        macros.RemoveAt(i); SetEditorMacros(macros, Math.Min(i, macros.Count - 1)); return Task.CompletedTask;
    }, "删除宏失败");
    private void OnAddMacroStep(object sender, RoutedEventArgs e) => Run(() =>
    {
        var macros = EditorMacros(); int i = SelectedMacroIndex(macros);
        string key = MacroStepKeyBox.IsEditable ? MacroStepKeyBox.Text : MacroStepKeyBox.SelectedItem as string ?? "";
        bool mouse = key.StartsWith("Mouse:", StringComparison.Ordinal);
        var steps = macros[i].Steps.ToList();
        var step = new MacroStep(mouse ? key[6..] : key, MacroStepActionBox.SelectedIndex != 1, ValidDelay(MacroStepDelayBox.Value), mouse);
        int insertion = MacroStepsList.SelectedIndex < 0 ? steps.Count : MacroStepsList.SelectedIndex + 1;
        steps.Insert(insertion, step); macros[i] = macros[i] with { Steps = steps };
        SetEditorMacros(macros, i); MacroStepsList.SelectedIndex = insertion; return Task.CompletedTask;
    }, "添加事件失败");
    private void OnRemoveMacroStep(object sender, RoutedEventArgs e) => Run(() =>
    {
        var macros = EditorMacros(); int i = SelectedMacroIndex(macros); int step = MacroStepsList.SelectedIndex;
        if (step < 0) throw new InvalidOperationException("请先选择要移除的事件。");
        var steps = macros[i].Steps.ToList(); steps.RemoveAt(step); macros[i] = macros[i] with { Steps = steps };
        SetEditorMacros(macros, i); return Task.CompletedTask;
    }, "移除事件失败");

    private void BuildRemapBoard(IReadOnlyList<KeyRow> rows)
    {
        RemapBoard.Children.Clear(); _remapButtons.Clear(); RemapBoard.Width = 0;
        double x = 0, row = 0;
        foreach (var boardKey in Settings?.Model.Board ?? [])
        {
            if (boardKey.Key is "backquote" or "Tab" or "Caps" or "Left_Shift" or "Left_Ctrl") { row++; x = 0; }
            x += boardKey.MarginLeft / 46.0;
            double width = (boardKey.Width + 4) / 46.0;
            var mapping = rows.FirstOrDefault(r => r.Key == boardKey.Key);
            var button = new Button { Content = ShortLabel(boardKey.Key), Width = width * KeyUnit - KeyGap,
                Height = boardKey.Height / 42.0 * KeyUnit - KeyGap, FontSize = 10, Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6), Tag = mapping, IsEnabled = mapping is not null,
                Style = (Style)Application.Current.Resources[mapping?.Changed == true ? "HubChangedKeyStyle" : "HubKeyStyle"] };
            AutomationProperties.SetName(button, $"{boardKey.Key}：{mapping?.Value ?? "不可改键"}");
            ToolTipService.SetToolTip(button, $"{boardKey.Key} · {mapping?.Value ?? "不可改键"}");
            button.Click += (_, _) => { if (button.Tag is KeyRow item) SelectRemapKey(item); };
            Canvas.SetLeft(button, x * KeyUnit); Canvas.SetTop(button, row * KeyUnit);
            RemapBoard.Children.Add(button); _remapButtons[boardKey.Key] = button;
            RemapBoard.Width = Math.Max(RemapBoard.Width, (x + width) * KeyUnit);
            x += width + boardKey.MarginRight / 46.0;
        }
        HighlightRemapKey();
    }
    private void HighlightRemapKey()
    {
        foreach (var (key, button) in _remapButtons)
        {
            bool changed = button.Tag is KeyRow { Changed: true };
            string style = key == _selectedKey
                ? changed ? "HubSelectedChangedKeyStyle" : "HubSelectedKeyStyle"
                : changed ? "HubChangedKeyStyle" : "HubKeyStyle";
            button.Style = (Style)Application.Current.Resources[style];
        }
    }

    private void RefreshProfileLibrary()
    {
        var model = Settings?.Model.Model; if (model is null) return;
        var selected = ProfileLibraryBox.SelectedItem as string;
        try
        {
            var names = ProfileLibrary.List(model).Select(p => p.Name).ToList();
            ProfileLibraryBox.ItemsSource = names;
            ProfileLibraryBox.SelectedItem = names.Contains(selected ?? "") ? selected : names.FirstOrDefault();
        }
        catch (Exception ex) { ProfileStatus.Text = $"配置库读取失败：{ex.Message}"; }
    }
    private KeyboardProfile ParseProfile() => ParseProfileText(ProfileEditor.Text,
        Settings?.Model ?? throw new InvalidOperationException("请先连接键盘。"));
    private static KeyboardProfile ParseProfileText(string text, KeyboardModelSchema model)
    {
        if (text.Length > KeyboardProfileLibrary.MaxFileBytes) throw new InvalidDataException("配置文件太大。");
        var profile = JsonSerializer.Deserialize<KeyboardProfile>(text, EditorJson) ?? throw new InvalidDataException("先读取当前配置或导入文件。");
        KeyboardProfileValidator.Validate(profile, model); return profile;
    }
    private void OnSaveProfile(object sender, RoutedEventArgs e) => Run(async () =>
    {
        var settings = Settings; if (settings is null || _advancedBusy) return;
        string name = ProfileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("请先填写配置名称。");
        string draft = ProfileEditor.Text;
        long revision = _profileDraftRevision;
        var library = ProfileLibrary;
        var loadedProfile = string.IsNullOrWhiteSpace(draft) ? null : ParseProfileText(draft, settings.Model);
        _advancedBusy = true;
        try
        {
            if (library.List(settings.Model.Model).Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) &&
                !await ConfirmAdvancedAsync("覆盖本地配置", $"将用{(loadedProfile is null ? "从键盘读取的配置" : "当前已载入的配置")}覆盖“{name}”。")) return;
            if (!CurrentSettings(settings)) return;
            if (revision != _profileDraftRevision) throw new InvalidOperationException("配置草稿已修改，请重新保存。");
            var profile = loadedProfile ?? await Task.Run(settings.BackupProfile);
            if (!CurrentSettings(settings)) return;
            await Task.Run(() => library.Save(name, profile));
            if (!CurrentSettings(settings)) return;
            bool draftChanged = revision != _profileDraftRevision;
            if (!draftChanged) ProfileEditor.Text = JsonSerializer.Serialize(profile, EditorJson);
            RefreshProfileLibrary(); ProfileLibraryBox.SelectedItem = name;
            ProfileStatus.Text = draftChanged ? $"已保存操作开始时的“{name}”；保留了你后续编辑的草稿。" : $"已保存“{name}”到本地库。宏库请在宏页面另存。";
        }
        finally { _advancedBusy = false; RefreshMacroActions(); }
    }, "保存配置失败");
    private void OnLoadProfile(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (_advancedBusy) return Task.CompletedTask;
        var name = ProfileLibraryBox.SelectedItem as string ?? throw new InvalidOperationException("请先选择本地方案。");
        var profile = ProfileLibrary.Load(Settings?.Model.Model ?? "", name);
        ProfileEditor.Text = JsonSerializer.Serialize(profile, EditorJson); ProfileNameBox.Text = name;
        ProfileStatus.Text = $"已载入“{name}”，点击应用到键盘后写入设备。";
        return Task.CompletedTask;
    }, "载入配置失败");
    private static void InitializePicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));
    private void OnExportProfile(object sender, RoutedEventArgs e) => Run(async () =>
    {
        var settings = Settings ?? throw new InvalidOperationException("请先连接键盘。");
        if (_advancedBusy) return;
        var profile = ParseProfile();
        _advancedBusy = true;
        try
        {
        var picker = new FileSavePicker { SuggestedFileName = $"{profile.Model}-配置" };
        picker.FileTypeChoices.Add("键盘配置", new[] { ".json" }); InitializePicker(picker);
        var file = await picker.PickSaveFileAsync(); if (file is null) return;
        await FileIO.WriteTextAsync(file, JsonSerializer.Serialize(profile, EditorJson));
        if (CurrentSettings(settings)) ProfileStatus.Text = $"已导出：{file.Name}";
        }
        finally { _advancedBusy = false; RefreshMacroActions(); }
    }, "导出配置失败");
    private void OnImportProfile(object sender, RoutedEventArgs e) => Run(async () =>
    {
        var settings = Settings ?? throw new InvalidOperationException("请先连接键盘。");
        if (_advancedBusy) return;
        long revision = _profileDraftRevision;
        _advancedBusy = true;
        try
        {
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json"); InitializePicker(picker);
        var file = await picker.PickSingleFileAsync(); if (file is null || !CurrentSettings(settings)) return;
        if ((await file.GetBasicPropertiesAsync()).Size > KeyboardProfileLibrary.MaxFileBytes) throw new InvalidDataException("配置文件太大。");
        var text = await FileIO.ReadTextAsync(file);
        var profile = ParseProfileText(text, settings.Model);
        if (!CurrentSettings(settings)) return;
        if (revision != _profileDraftRevision) { ProfileStatus.Text = "导入已取消：读取文件期间配置草稿发生修改。"; return; }
        ProfileEditor.Text = JsonSerializer.Serialize(profile, EditorJson);
        ProfileStatus.Text = $"已导入并验证 {file.Name}；点击应用到键盘后写入设备。";
        }
        finally { _advancedBusy = false; RefreshMacroActions(); }
    }, "导入配置失败");
}
