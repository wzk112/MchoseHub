using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MchoseHub.Core.Protocol;
using MchoseHub.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MchoseHub.App.Views;

public sealed partial class DeviceDetailPage
{
    private static readonly JsonSerializerOptions EditorJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private IReadOnlyList<KeyboardMacro>? _uploadedMacros;
    private KeyboardSettings? _macroOwner;
    private long _uploadedMacroRevision = -1;
    private bool _advancedBusy;
    private bool _macroFileBusy;
    private bool _recording;
    private readonly MacroRecordingBuffer _recordingBuffer = new();
    private readonly Stopwatch _recordClock = new();

    private void OnReadBattery(object sender, RoutedEventArgs e) => Run(async () =>
    {
        var settings = Settings;
        if (settings is null) throw new InvalidOperationException("请先连接键盘配置通道。");
        var level = await Task.Run(settings.ReadBattery);
        if (_unloaded || !ReferenceEquals(settings, Settings)) return;
        BatteryText.Text = level is { } value ? $"设备报告电量：{value}%" : "设备未报告有效电量。";
    }, "读取电量失败");

    private IReadOnlyList<KeyboardMacro> ParseMacroEditor()
    {
        var macros = ParseMacroJson(MacroEditor.Text);
        var bank = KeyboardMacroCodec.EncodeBank(macros);
        MacroStatus.Text = $"{macros.Count} 个宏，{bank.Length} 字节，{KeyboardMacroCodec.BuildPages(bank).Count} 个分包。";
        return macros;
    }
    private static IReadOnlyList<KeyboardMacro> ParseMacroJson(string text)
    {
        if (text.Length > 2_000_000) throw new InvalidDataException("宏 JSON 太大。");
        var macros = JsonSerializer.Deserialize<List<KeyboardMacro>>(text, EditorJson)
            ?? throw new InvalidDataException("请输入宏库 JSON 数组。");
        if (macros.Any(m => m is null || m.Steps is null || m.Steps.Any(s => s is null)))
            throw new InvalidDataException("宏及动作列表不能包含空值。");
        KeyboardMacroCodec.EncodeBank(macros);
        return macros;
    }
    private void OnValidateMacros(object sender, RoutedEventArgs e) => Run(() =>
    {
        RequireCommittedMacroMetadata();
        ParseMacroEditor(); return Task.CompletedTask;
    }, "宏库格式无效");

    private string MacroLibraryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MchoseHub", _preview ? "preview-macros" : "macros", $"{Settings?.Model.Model ?? "unconnected"}.json");
    private void OnSaveMacros(object sender, RoutedEventArgs e) => Run(async () =>
    {
        var settings = Settings ?? throw new InvalidOperationException("先连接键盘再保存宏库。");
        if (_macroFileBusy) return;
        RequireCommittedMacroMetadata();
        if (_recording || _recordingBuffer.Steps.Count > 0) throw new InvalidOperationException("请先停止并保存当前录制。");
        var macros = ParseMacroEditor(); var path = MacroLibraryPath;
        long revision = _macroDraftRevision;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        _macroFileBusy = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(macros, EditorJson));
            File.Move(temporary, path, overwrite: true);
            if (!CurrentSettings(settings)) return;
            MacroStatus.Text = revision == _macroDraftRevision ? "宏库已保存到本机。" : "已保存操作开始时的宏库；后续修改尚未保存。";
        }
        finally { _macroFileBusy = false; if (File.Exists(temporary)) File.Delete(temporary); }
    }, "保存宏库失败");
    private void OnLoadMacros(object sender, RoutedEventArgs e) => Run(async () =>
    {
        var settings = Settings ?? throw new InvalidOperationException("请先连接键盘。");
        if (_macroFileBusy) return;
        RequireCommittedMacroMetadata();
        if (_recording || _recordingBuffer.Steps.Count > 0) throw new InvalidOperationException("请先停止并保存当前录制。");
        var path = MacroLibraryPath;
        long revision = _macroDraftRevision;
        _macroFileBusy = true;
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException("本机还没有此型号的宏库。");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 2_000_000) throw new InvalidDataException("宏 JSON 太大。");
            using var reader = new StreamReader(stream);
            var macros = ParseMacroJson(await reader.ReadToEndAsync());
            if (!CurrentSettings(settings)) return;
            if (revision != _macroDraftRevision) { MacroStatus.Text = "加载已取消：读取期间草稿发生修改。"; return; }
            SetEditorMacros(macros, 0);
            MacroStatus.Text = "已加载本机宏库；上传后才会写入键盘。";
        }
        finally { _macroFileBusy = false; }
    }, "加载宏库失败");
    private static void CopyText(string text)
    {
        var package = new DataPackage(); package.SetText(text); Clipboard.SetContent(package);
    }
    private void OnCopyMacros(object sender, RoutedEventArgs e) => Run(() =>
    {
        RequireCommittedMacroMetadata();
        ParseMacroEditor(); CopyText(MacroEditor.Text); MacroStatus.Text = "宏 JSON 已复制，可以粘贴另存或导入。";
        return Task.CompletedTask;
    }, "复制宏库失败");

    private async Task<bool> ConfirmAdvancedAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
    private void OnUploadMacros(object sender, RoutedEventArgs e) => Run(async () =>
    {
        var settings = Settings;
        if (settings is null || _advancedBusy) return;
        RequireCommittedMacroMetadata();
        if (_recording || _recordingBuffer.Steps.Count > 0) throw new InvalidOperationException("请先停止并保存当前录制。");
        var macros = ParseMacroEditor();
        long revision = _macroDraftRevision;
        _advancedBusy = true; MacroUploadButton.IsEnabled = MacroBindButton.IsEnabled = false;
        try
        {
            if (!await ConfirmAdvancedAsync("上传完整宏库", "这会覆盖键盘现有的整个宏库。已有宏按键按序号引用宏，可能触发新内容；请先保存原来的宏 JSON，并确保列表顺序正确。")) return;
            if (!CurrentSettings(settings)) return;
            if (revision != _macroDraftRevision) throw new InvalidOperationException("宏库已修改，请重新确认上传。");
            _uploadedMacros = null; _macroOwner = null;
            await Task.Run(() => settings.WriteMacros(macros));
            if (!CurrentSettings(settings)) return;
            if (revision != _macroDraftRevision)
            {
                MacroStatus.Text = "已上传操作开始时的宏库；草稿有新修改，请重新上传后再绑定。";
                return;
            }
            _uploadedMacros = macros; _macroOwner = settings;
            _uploadedMacroRevision = revision;
            MacroStatus.Text = "宏分包已发送，首个目录项回读一致。完整事件执行仍需在键盘上验证；现在可以绑定宏。";
        }
        finally { _advancedBusy = false; RefreshMacroActions(); }
    }, "上传宏库失败");

    private void OnBindMacro(object sender, RoutedEventArgs e) => Run(async () =>
    {
        var settings = Settings;
        if (_advancedBusy || settings is null) return;
        RequireCommittedMacroMetadata();
        if (_recording || _recordingBuffer.Steps.Count > 0) throw new InvalidOperationException("请先停止并保存当前录制。");
        if (_uploadedMacros is null || !ReferenceEquals(settings, _macroOwner) || _uploadedMacroRevision != _macroDraftRevision)
            throw new InvalidOperationException("先向当前连接上传最新的完整宏库，再绑定按键。");
        if (_selectedKey is null) throw new InvalidOperationException("先在按键列表中选一个键。");
        var number = MacroIndexBox.Value;
        if (!double.IsFinite(number) || number != Math.Truncate(number) || number < 1 || number > _uploadedMacros.Count)
            throw new ArgumentException("宏序号超出当前上传的列表。");
        var key = _selectedKey; int index = (int)number - 1; var macro = _uploadedMacros[index];
        var layer = KeyLayers[Math.Clamp(KeyLayerBox.SelectedIndex, 0, KeyLayers.Length - 1)].Key;
        _advancedBusy = true; MacroBindButton.IsEnabled = false;
        try
        {
            await Task.Run(() => settings.BindMacro(layer, key, macro.Trigger, index));
            if (_unloaded || !ReferenceEquals(settings, Settings)) return;
            await ReadKeysAsync();
            if (CurrentSettings(settings)) MacroStatus.Text = $"{layer}：{key} → {macro.Name}，按键编码回读通过。";
        }
        finally { _advancedBusy = false; RefreshMacroActions(); }
    }, "宏绑定失败");

    private void OnStartMacroRecording(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (_recording) return Task.CompletedTask;
        if (_macroFileBusy) throw new InvalidOperationException("宏库文件正在读取或保存，请稍后开始录制。");
        if (_recordingBuffer.Steps.Count > 0) throw new InvalidOperationException("上次录制尚未保存，请先点击停止并生成宏。");
        RequireCommittedMacroMetadata();
        ParseMacroEditor();
        _recordingBuffer.Clear(); _recordClock.Restart(); _recording = true;
        if (!MacroRecorder.Focus(FocusState.Programmatic))
        {
            _recording = false; _recordClock.Stop();
            throw new InvalidOperationException("录制区未能获取焦点，请点击录制区后再开始。");
        }
        RefreshMacroActions();
        MacroStatus.Text = "正在录制此处的按键；按 Esc 结束并生成宏。离开录制区会暂停并补齐抬键。";
        return Task.CompletedTask;
    }, "开始录制失败");
    private void PauseMacroRecording()
    {
        if (!_recording) return;
        _recordingBuffer.ReleaseHeld(_recordClock.ElapsedMilliseconds);
        _recordClock.Stop(); _recording = false;
        MacroStatus.Text = "录制已暂停，已补齐抬键；点击停止并生成宏保存本次录制。";
    }
    private void OnRecordKeyDown(object sender, KeyRoutedEventArgs e) => RecordKey(e, true);
    private void OnRecordKeyUp(object sender, KeyRoutedEventArgs e) => RecordKey(e, false);
    private void RecordKey(KeyRoutedEventArgs e, bool down)
    {
        if (!_recording) return;
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            if (down) { OnStopMacroRecording(sender: this, e: new RoutedEventArgs()); MacroSelectionBox.Focus(FocusState.Programmatic); }
            return;
        }
        string key = e.Key switch
        {
            VirtualKey.Back => "Backspace",
            VirtualKey.NumberKeyLock => "NumLock",
            VirtualKey.Scroll => "ScrollLock",
            VirtualKey.Shift => e.KeyStatus.ScanCode == 54 ? "ShiftRight" : "ShiftLeft",
            VirtualKey.LeftShift => "ShiftLeft",
            VirtualKey.RightShift => "ShiftRight",
            VirtualKey.Control => e.KeyStatus.IsExtendedKey ? "ControlRight" : "ControlLeft",
            VirtualKey.LeftControl => "ControlLeft",
            VirtualKey.RightControl => "ControlRight",
            VirtualKey.Menu => e.KeyStatus.IsExtendedKey ? "AltRight" : "AltLeft",
            VirtualKey.LeftMenu => "AltLeft",
            VirtualKey.RightMenu => "AltRight",
            VirtualKey.Enter when e.KeyStatus.IsExtendedKey => "NumpadEnter",
            VirtualKey.LeftWindows => "WinLeft",
            VirtualKey.RightWindows => "WinRight",
            VirtualKey.CapitalLock => "CapsLock",
            VirtualKey.NumberPad0 => "Numpad0",
            VirtualKey.NumberPad1 => "Numpad1",
            VirtualKey.NumberPad2 => "Numpad2",
            VirtualKey.NumberPad3 => "Numpad3",
            VirtualKey.NumberPad4 => "Numpad4",
            VirtualKey.NumberPad5 => "Numpad5",
            VirtualKey.NumberPad6 => "Numpad6",
            VirtualKey.NumberPad7 => "Numpad7",
            VirtualKey.NumberPad8 => "Numpad8",
            VirtualKey.NumberPad9 => "Numpad9",
            VirtualKey.Add => "NumpadAdd",
            VirtualKey.Subtract => "NumpadSubtract",
            VirtualKey.Multiply => "NumpadMultiply",
            VirtualKey.Divide => "NumpadDivide",
            VirtualKey.Decimal => "NumpadDecimal",
            VirtualKey.Snapshot => "PrintScreen",
            VirtualKey.Application => "Menu",
            _ => e.Key.ToString(),
        };
        key = (int)e.Key switch
        {
            186 => ";", 187 => "=", 188 => ",", 189 => "-", 190 => ".", 191 => "/", 192 => "`",
            219 => "[", 220 => "\\", 221 => "]", 222 => "'", _ => key,
        };
        if ((int)e.Key is >= 65 and <= 90) key = ((char)((int)e.Key + 32)).ToString();
        if (key.StartsWith("Number", StringComparison.Ordinal) && key.Length == 7) key = key[^1..];
        if (!KeyboardFunctions.MacroKeys.ContainsKey(key)) { MacroStatus.Text = $"未收录的录制键 {key}；可在 JSON 中手工编辑。"; e.Handled = true; return; }
        bool recorded = _recordingBuffer.Record(key, down, _recordClock.ElapsedMilliseconds); e.Handled = true;
        if (_recordingBuffer.CapacityReached)
        {
            PauseMacroRecording(); MacroStatus.Text = "事件数达到上限，已补齐抬键；点击停止并生成宏。";
            return;
        }
        if (!recorded) return;
        MacroStatus.Text = $"已录制 {_recordingBuffer.Steps.Count} 个事件 · Esc 结束录制";
    }
    private void OnStopMacroRecording(object sender, RoutedEventArgs e)
    {
        if (!_recording && _recordingBuffer.Steps.Count == 0) return;
        PauseMacroRecording();
        Run(() =>
        {
            if (_recordingBuffer.Steps.Count == 0) throw new InvalidOperationException("未录到按键，请在录制框内输入后再停止。");
            var macros = EditorMacros(); int number = 1;
            while (macros.Any(m => m.Name == $"录制宏 {number}")) number++;
            macros.Add(new KeyboardMacro($"录制宏 {number}", MacroTrigger.Once, _recordingBuffer.Steps.ToArray()));
            SetEditorMacros(macros, macros.Count - 1);
            MacroStatus.Text = $"录制完成，已追加 {_recordingBuffer.Steps.Count} 个事件。现有宏已保留。";
            _recordingBuffer.Clear();
            RefreshMacroActions();
            return Task.CompletedTask;
        }, "录制宏生成失败");
    }
    private void OnBackupProfile(object sender, RoutedEventArgs e) => Run(async () =>
    {
        var settings = Settings; if (settings is null || _advancedBusy) return;
        long revision = _profileDraftRevision;
        _advancedBusy = true;
        try
        {
            var profile = await Task.Run(settings.BackupProfile);
            if (_unloaded || !ReferenceEquals(settings, Settings)) return;
            if (revision != _profileDraftRevision) { ProfileStatus.Text = "读取完成；配置草稿已有新修改，保留当前草稿。"; return; }
            ProfileEditor.Text = JsonSerializer.Serialize(profile, EditorJson);
            ProfileStatus.Text = "已读取灯光、性能、逐键色和三个按键层，可保存到配置库或导出文件。宏库须另存。";
        }
        finally { _advancedBusy = false; RefreshMacroActions(); }
    }, "备份失败");
    private void OnCopyProfile(object sender, RoutedEventArgs e) => Run(() =>
    {
        var profile = ParseProfile();
        CopyText(JsonSerializer.Serialize(profile, EditorJson)); ProfileStatus.Text = "已验证并复制配置 JSON。"; return Task.CompletedTask;
    }, "复制失败");
    private void OnRestoreProfile(object sender, RoutedEventArgs e) => Run(async () =>
    {
        var settings = Settings; if (settings is null || _advancedBusy) return;
        var profile = ParseProfileText(ProfileEditor.Text, settings.Model);
        long revision = _profileDraftRevision;
        _advancedBusy = true;
        try
        {
            if (!await ConfirmAdvancedAsync("恢复键盘配置", "将覆盖灯光、性能、逐键色和全部三个按键层，保留设备中的宏库。请确认已保存当前配置。")) return;
            if (_unloaded || !ReferenceEquals(settings, Settings)) return;
            if (revision != _profileDraftRevision) throw new InvalidOperationException("配置草稿已修改，请重新确认恢复。");
            await Task.Run(() => settings.RestoreProfile(profile));
            if (_unloaded || !ReferenceEquals(settings, Settings)) return;
            await ReloadKeyboardSettingsAsync(); await ReadKeysAsync();
            if (CurrentSettings(settings)) ProfileStatus.Text = revision == _profileDraftRevision
                ? "配置已恢复；所有写入均通过回读比对。"
                : "已恢复操作开始时的配置并回读确认；草稿中的后续修改尚未应用。";
        }
        finally { _advancedBusy = false; RefreshMacroActions(); }
    }, "恢复失败（可能已部分写入，请用备份重试）");
}
