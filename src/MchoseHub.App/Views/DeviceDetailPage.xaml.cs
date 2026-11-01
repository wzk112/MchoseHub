using System.Diagnostics;
using MchoseHub.Core.Models;
using MchoseHub.Core.Protocol;
using MchoseHub.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace MchoseHub.App.Views;

public sealed partial class DeviceDetailPage : Page
{
    /// <summary>音箱 / 耳机的控制模式。</summary>
    private static readonly string[] AudioEffects = ["常亮", "呼吸", "流光", "音乐律动", "关闭"];

    /// <summary>按键层。第二个字节就是层编号（默认 00 / Fn 01 / Fn2 02）。</summary>
    private static readonly (string Key, string Name, byte Code)[] KeyLayers =
    [
        ("Default", "默认层", 0x00),
        ("Fn", "Fn 层", 0x01),
        ("Fn2", "Fn2 层", 0x02),
    ];

    private const double KeyUnit = 38;
    private const double KeyGap = 4;

    private readonly Dictionary<string, Button> _diyKeys = new(StringComparer.OrdinalIgnoreCase);

    private DiyLightTable? _diyTable;
    private string? _diySelected;
    private bool _unloaded;

    /// <summary>设备当前是不是「自定义（逐键调色）」灯效。逐键颜色只在这个灯效下渲染。</summary>
    private bool _inCustomEffect;

    /// <summary>当前机型可选的灯效列表（含该机型的「自定义」灯效编号）。</summary>
    private IReadOnlyList<KeyboardEffect> _effects = KeyboardEffectTable.Normal;

    /// <summary>
    /// 默认显示常用灯效；完整列表按型号从官方配置加载。
    /// </summary>
    private bool _showAllEffects;

    private DeviceGroup? _group;
    private KeyboardSession? _previewSession;
    private KeyboardSettings? _previewSettings;
    private bool _preview;
    private int _keyReadRevision;

    /// <summary>
    /// 改完控件就自动下发的定时器。
    ///
    /// 官方网页版是「一选中灯效 / 一拖亮度就立刻写设备」，我们之前非要再点一次
    /// 「应用」——用户点下拉换灯效，键盘没反应，自然会觉得「点了没用」。
    /// 现在照网页版来：控件改完 450 毫秒没有新动作就写一次。
    /// </summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autoApply;

    /// <summary>程序化回填控件时置位，避免「回填 → 触发事件 → 又写回设备」的循环。</summary>
    private bool _suppressAutoApply;

    /// <summary>设备上当前生效的灯效编号（用来判断界面的选择有没有真的写下去）。</summary>
    private int _deviceEffectId = -1;

    /// <summary>
    /// 两次真正写入之间至少隔这么久。
    ///
    /// 灯效配置是**写进键盘的配置存储**的，拖进度条时如果一直跟着写，
    /// 键盘会来不及处理，实测会卡住甚至像死机（要拔插才恢复）。
    /// 所以：拖动过程只攒着，停手后再写，而且两次写入之间有冷却。
    /// </summary>
    private static readonly TimeSpan WriteCooldownMin = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan WriteCooldownMax = TimeSpan.FromMilliseconds(3000);

    /// <summary>连续快速写入会让冷却时间翻倍，最多到 WriteCooldownMax。</summary>
    private TimeSpan _writeCooldown = WriteCooldownMin;
    private int _rapidWrites;

    /// <summary>上一次真正写进设备的内容；一样就不再写一遍（拖回原值不会重复写）。</summary>
    private string? _lastWrittenSignature;

    /// <summary>
    /// 上一次真正写进设备的「灯效 + 颜色」。颜色是另一条写命令（0x0A，519 字节），
    /// 对键盘的配置存储来说同样是一次完整擦写——拖亮度时颜色根本没变，
    /// 没必要跟着一起写。
    /// </summary>
    private string? _lastWrittenColor;

    private bool _applyInFlight;
    private bool _applyPending;
    private DateTime _lastWriteUtc = DateTime.MinValue;

    /// <summary>「亮度接近 0」这条提醒只弹一次，拖回来再拖下去才会再弹。</summary>
    private bool _lowBrightnessWarned;

    /// <summary>
    /// 正在拖滑杆。拖动期间**一次都不写设备**，只在界面上实时显示数值，
    /// 松手（或键盘方向键停下）才写一次。
    ///
    /// 这就是「拉进度条键盘会死机」的正解：键盘的灯效参数存在配置存储里，
    /// 每一次修改都是一次真实的存储写入。滑杆从 0 拖到 100 会发上百个
    /// ValueChanged，以前每个都排队去写，固件来不及擦写就被写卡了。
    /// </summary>
    private bool _sliderDragging;
    private bool _commitPending;
    private bool _slidersWired;

    /// <summary>
    /// 拖拽兜底：正常情况下「松手」事件会收尾，但指针在窗口外松开、或者输入设备
    /// 中途抽风时可能收不到。攒够 3 秒没有新动作就当作松手处理，
    /// 保证拖过的值一定写下去一次（而不是停在界面上没生效）。
    /// </summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dragSafety;

    public DeviceDetailPage()
    {
        InitializeComponent();
        UiGuard.Install(this);
        MacroEditor.Text = System.Text.Json.JsonSerializer.Serialize(new[] { new KeyboardMacro("示例宏", MacroTrigger.Once, new[] { new MacroStep("a", true), new MacroStep("a", false) }) }, EditorJson);
        InitializeEditors();
        Loaded += (_, _) =>
        {
            _unloaded = false;
            AppState.Reconnected -= OnReconnected;
            AppState.Reconnected += OnReconnected;
            Load();
        };
        Unloaded += (_, _) =>
        {
            _unloaded = true;
            _autoApply?.Stop();
            _diyApply?.Stop();
            _dragSafety?.Stop();
            _recording = false;
            _recordClock.Stop();
            _previewSession?.Dispose();
            _previewSession = null;
            _previewSettings = null;
            AppState.Reconnected -= OnReconnected;
        };

        _autoApply = DispatcherQueue.CreateTimer();
        _autoApply.Interval = TimeSpan.FromMilliseconds(450);
        _autoApply.IsRepeating = false;
        _autoApply.Tick += (_, _) =>
        {
            if (!_suppressAutoApply && !_unloaded && IsKeyboard)
            {
                Run(ApplyAutoAsync, "自动下发失败");
            }
        };

        _dragSafety = DispatcherQueue.CreateTimer();
        _dragSafety.Interval = TimeSpan.FromMilliseconds(3000);
        _dragSafety.IsRepeating = false;
        _dragSafety.Tick += (_, _) => EndSliderDrag();

        // 逐键颜色的合并写入：连点十几个键，只在下手停下来之后写一次
        _diyApply = DispatcherQueue.CreateTimer();
        _diyApply.Interval = TimeSpan.FromMilliseconds(600);
        _diyApply.IsRepeating = false;
        _diyApply.Tick += (_, _) => Run(FlushDiyAsync, "逐键颜色下发失败");
    }

    private bool IsKeyboard => _preview || _group?.Kind == ChannelKind.Keyboard;

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _preview = e.Parameter is "preview";
    }

    /// <summary>
    /// 应用级看门狗完成重连之后回调到这里：设置对象已经换成新的了，
    /// 界面必须整个重新读一遍，否则手里的旧引用全是废的。
    /// </summary>
    private void OnReconnected(DeviceGroup? group)
    {
        if (_unloaded || _preview)
        {
            return;
        }

        _group = AppState.ActiveGroup ?? _group;
        _lastWrittenSignature = null;
        _lastWrittenColor = null;
        _deviceEffectId = -1;
        Run(async () =>
        {
            await ReloadKeyboardSettingsAsync();
            await ReadKeysAsync();
            await ReloadDiyAsync();
        }, "重连后刷新界面");
    }

    /// <summary>
    /// 自动下发：同一时刻只允许一次写入在进行，期间来的改动合并成「最后一次」，
    /// 并且两次写入之间保留冷却时间——避免拖进度条把键盘的配置存储写卡。
    /// </summary>
    private async Task ApplyAutoAsync()
    {
        if (_applyInFlight)
        {
            _applyPending = true;
            return;
        }

        _applyInFlight = true;
        try
        {
            var since = DateTime.UtcNow - _lastWriteUtc;
            if (since < _writeCooldown)
            {
                await Task.Delay(_writeCooldown - since);
            }

            if (_unloaded)
            {
                _applyPending = false;
                return;
            }

            await ApplyLightingAsync(auto: true);

            // 亮度被拖到接近 0 时提醒一句：很多人是在这里把键盘拖「黑」的，
            // 然后以为设备坏了。只在真的落到这个区间时提示一次。
            if (IsKeyboard && BrightnessSlider.IsEnabled && BrightnessSlider.Value <= 3)
            {
                if (!_lowBrightnessWarned)
                {
                    _lowBrightnessWarned = true;
                    Toast.Warning("亮度已经接近 0", "键盘看起来会是全黑的。要恢复就点「强制点亮」。");
                }
            }
            else
            {
                _lowBrightnessWarned = false;
            }

            // 距上一次写入很近 = 用户还在连续调，冷却时间往后翻倍，
            // 让键盘的配置存储有时间把上一笔落盘。静置一会儿自动回落。
            var gap = DateTime.UtcNow - _lastWriteUtc;
            _rapidWrites = gap < TimeSpan.FromSeconds(6) ? _rapidWrites + 1 : 0;
            var factor = Math.Pow(2, Math.Min(_rapidWrites, 3));
            _writeCooldown = TimeSpan.FromMilliseconds(
                Math.Min(WriteCooldownMin.TotalMilliseconds * factor, WriteCooldownMax.TotalMilliseconds));

            _lastWriteUtc = DateTime.UtcNow;
        }
        finally
        {
            _applyInFlight = false;
        }

        if (_applyPending)
        {
            _applyPending = false;
            await ApplyAutoAsync();
        }
    }

    /// <summary>控件改完安排一次自动下发（程序化回填时会被 _suppressAutoApply 挡掉）。</summary>
    private void ScheduleAutoApply()
    {
        if (_suppressAutoApply || _unloaded || !IsKeyboard)
        {
            return;
        }

        // 滑杆还捏在手里：先攒着，松手后再写，避免拖动过程疯狂写配置存储
        if (_sliderDragging)
        {
            _commitPending = true;
            _autoApply?.Stop();
            _dragSafety?.Stop();
            _dragSafety?.Start();
            return;
        }

        _autoApply?.Stop();
        _autoApply?.Start();
    }

    // ---------------- 滑杆：拖动中不写，松手才写 ----------------

    /// <summary>
    /// 给滑杆挂上「拖动中 / 松手」的判定。
    ///
    /// ValueChanged 在拖动时会连续触发，靠它判断不了用户是「正在拖」还是「拖完了」，
    /// 所以看指针：按下 → 进入拖动；松开 / 被抢走捕获 / 失焦 → 收尾写一次。
    /// 键盘方向键改值不会走指针事件，由 ValueChanged 上的防抖兜住。
    /// </summary>
    private void WireSlider(Slider slider)
    {
        slider.AddHandler(UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(OnSliderPointerPressed), handledEventsToo: true);
        slider.AddHandler(UIElement.PointerReleasedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(OnSliderPointerReleased), handledEventsToo: true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(OnSliderPointerReleaseLike), handledEventsToo: true);
        slider.PointerCanceled += (_, _) => EndSliderDrag();
        slider.LostFocus += (_, _) => EndSliderDrag();
    }

    private void OnSliderPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _sliderDragging = true;
        _autoApply?.Stop();
        // 不把事件标记成已处理：Slider 自己要靠它开始拖动
        e.Handled = false;
    }

    private void OnSliderPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => EndSliderDrag();

    private void OnSliderPointerReleaseLike(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => EndSliderDrag();

    private void EndSliderDrag()
    {
        if (!_sliderDragging)
        {
            return;
        }

        _sliderDragging = false;
        _dragSafety?.Stop();
        if (_commitPending)
        {
            _commitPending = false;
            ScheduleAutoApply();
        }
    }

    /// <summary>回填控件期间把自动下发关掉，免得自己触发自己。</summary>
    private void WithoutAutoApply(Action action)
    {
        bool previous = _suppressAutoApply;
        _suppressAutoApply = true;
        try
        {
            action();
        }
        finally
        {
            _suppressAutoApply = previous;
        }
    }

    private void Load()
    {
        // 这是 Loaded 事件的回调。异常从这里逃到 XAML 层时 .NET 兜不住，
        // 进程会直接消失（事件日志里就是 coreclr 0xc0000005）。整段兜住。
        try
        {
            if (_preview)
            {
                _previewSession = new KeyboardSession(new PreviewKeyboardSession());
                _previewSettings = new KeyboardSettings(new KeyboardSettingsSession(_previewSession, KeyboardModelSchemas.Get("K87")));
                WithoutAutoApply(BuildSectionContent);
                DeviceName.Text = "K87 · 离线体验";
                DeviceSubtitle.Text = "使用示例配置体验功能，所有修改仅保存在本次预览内存中。";
                ChannelBadge.Text = "离线预览";
                CloseDeviceButton.Content = "返回设备中心";
                BatteryButton.Visibility = RetryProbeButton.Visibility = Visibility.Collapsed;
                NavDiagnostics.Visibility = Visibility.Collapsed;
                return;
            }
            _group = AppState.ActiveGroup;
            if (_group is null)
            {
                DeviceName.Text = "没有选中的设备";
                SectionNav.Visibility = Visibility.Collapsed;
                return;
            }

            BuildHeader();
            WithoutAutoApply(BuildSectionContent);
        }
        catch (Exception ex)
        {
            AppState.WriteLog($"详情页初始化失败：{ex}");
            Note(InfoBarSeverity.Error, "页面初始化失败", Friendly(ex));
        }
    }

    private void BuildHeader()
    {
        if (_group is null)
        {
            return;
        }

        DeviceName.Text = _group.Name;
        DeviceSubtitle.Text = "灯光、按键与性能设置 · 修改后自动回读确认";
        ChannelBadge.Text = _group.KindText;
    }

    private void BuildSectionContent()
    {
        BatteryButton.Visibility = IsKeyboard ? Visibility.Visible : Visibility.Collapsed;
        // 按键 / 性能只对键盘有意义
        NavKeys.Visibility = IsKeyboard ? Visibility.Visible : Visibility.Collapsed;
        NavMacros.Visibility = NavProfiles.Visibility = IsKeyboard ? Visibility.Visible : Visibility.Collapsed;
        NavPerformance.Visibility = IsKeyboard ? Visibility.Visible : Visibility.Collapsed;

        if (IsKeyboard)
        {
            EffectMoreSwitch.Visibility = Visibility.Visible;
            RefreshEffectList(selectId: 1);
        }
        else
        {
            EffectMoreSwitch.Visibility = Visibility.Collapsed;
            EffectBox.ItemsSource = AudioEffects.ToList();
            EffectBox.SelectedIndex = 0;
        }

        // 官方网页版的灯效速度滑杆是 1–5 档，写进设备时统一 -1，也就是 0–4。
        // 之前这里放的是 0–10 档的下拉，能写出固件不认的值；现在照官方做成滑杆。
        SpeedSlider.Value = 4;

        // 滑杆要区分「正在拖」和「松手」，只能在拿到实例之后挂事件
        if (!_slidersWired)
        {
            _slidersWired = true;
            WireSlider(BrightnessSlider);
            WireSlider(SpeedSlider);
        }

        RateBox.ItemsSource = KeyboardSettings.PollingRatesHz.Select(hz => $"{hz} Hz").ToList();
        SleepBox.ItemsSource = KeyboardSettings.SleepOptionsMinutes
            .Select(m => m == 0 ? "不休眠" : $"{m} 分钟")
            .ToList();

        KeyLayerBox.ItemsSource = KeyLayers.Select(l => l.Name).ToList();
        KeyLayerBox.SelectedIndex = 0;

        LightingHint.Text = _preview ? "离线示例；修改不会发送到设备。"
            : "选择灯效、亮度和颜色，修改后自动保存。";

        RawHint.Text = IsKeyboard
            ? "输入有线格式的完整配置报文；2.4G 会自动转换为无线分包。宏请在「宏」页上传；手工写入不会回读校验。"
            : "音频设备走 output report（report id 0xBC），报文长度 15 字节。";

        AudioNoteCard.Visibility = IsKeyboard ? Visibility.Collapsed : Visibility.Visible;

        BuildDiagnostics();
        BuildAbout();
        SectionNav.SelectedItem = NavLighting;

        if (IsKeyboard)
        {
            // 键盘通道的 report id 探测和机型识别是异步的，这里等一下再读。
            // 探测失败时顶部会亮出「配置通道未就绪」和「重新探测」按钮。
            if (Settings is null)
            {
                ShowChannelState(
                    InfoBarSeverity.Informational, "正在探测配置通道",
                    "正在和设备握手确定 report id，几秒钟内会有结果。");
            }

            _ = WaitForKeyboardThenReloadAsync();
        }

        BuildDiyBoard();
        RefreshProfileLibrary();
        DiyHint.Text = IsKeyboard
            ? "先选择颜色，再点击按键。逐键颜色仅在自定义灯效下显示。"
            : string.Empty;
    }

    private KeyboardSettings? Settings => _preview ? _previewSettings : AppState.KeyboardSettings;

    /// <summary>
    /// 所有异步事件处理器的统一外壳。async void 里没接住的异常会直接钻进
    /// XAML 的原生层，连日志都来不及写进程就消失了；这里统一接住，
    /// 转成界面上的一条提示，同时写进日志。
    /// </summary>
    private async void Run(Func<Task> action, string title)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppState.WriteLog($"{title}：{ex}");
            if (!_unloaded)
            {
                Note(InfoBarSeverity.Error, title, ex.Message);
            }
        }
    }

    /// <summary>把异常翻成人话：Win32 87 就是「report id 或长度不被设备接受」。</summary>
    private static string Friendly(Exception ex) => ex switch
    {
        KeyboardSession.NotReadyException => ex.Message,
        IOException io when io.Message.Contains("错误码 87") =>
            "设备不接受这条报文（report id 没确定，或报文长度不符）。点顶部「重新探测」重来一次。",
        IOException io when io.Message.Contains("错误码 31") =>
            "设备暂时没有响应。刚被上一个句柄放开时它需要几百毫秒恢复，稍等一下再试。",
        IOException io when io.Message.Contains("错误码 1") => "设备已经断开，请重新插好再打开。",
        IOException io when io.Message.Contains("错误码 5") => "设备被别的程序占用了，先关掉官方 MCHOSE HUB。",
        _ => ex.Message,
    };

    /// <summary>配置通道没就绪时亮出顶部提示，就绪后收起。</summary>
    private void ShowChannelState(InfoBarSeverity severity, string title, string message)
    {
        ChannelNote.Severity = severity;
        ChannelNote.Title = title;
        ChannelNote.Message = message;
        ChannelNote.IsOpen = true;
        RetryProbeButton.IsEnabled = true;
        ChannelStatePanel.Visibility = Visibility.Visible;
    }

    private void HideChannelState()
    {
        ChannelStatePanel.Visibility = Visibility.Collapsed;
        ChannelNote.IsOpen = false;
    }

    /// <summary>等键盘配置对象就绪（最多 6 秒），然后把手里的设置读一遍。</summary>
    private async Task WaitForKeyboardThenReloadAsync()
    {
        try
        {
            for (int i = 0; i < 30 && Settings is null && !_unloaded; i++)
            {
                await Task.Delay(200);
            }

            if (_unloaded)
            {
                return;
            }

            if (Settings is null)
            {
                // 第一次探测可能只是没赶上（设备刚被别的程序松开），这里自己再试一次
                if (await AppState.PrepareKeyboardAsync(_group))
                {
                    HideChannelState();
                }
                else
                {
                    ShowChannelState(
                        InfoBarSeverity.Warning, "配置通道未就绪",
                        "没能拿到设备的应答，report id 还没确定。多半是官方 MCHOSE HUB 或别的程序" +
                        "正占着设备：先把它关掉，再点「重新探测」。");
                    DiyHint.Text = "逐键调色需要先连上键盘的配置通道。";
                    return;
                }
            }
            else
            {
                HideChannelState();
            }

            await ReloadKeyboardSettingsAsync();
            await ReadKeysAsync();
            await ReloadDiyAsync();
        }
        catch (Exception ex)
        {
            AppState.WriteLog($"键盘准备流程失败：{ex}");
            ShowChannelState(InfoBarSeverity.Error, "配置通道出错", Friendly(ex));
        }
    }

    /// <summary>彻底重来一次：放掉旧句柄 → 重新打开 → 重新探测 → 重新读设置。</summary>
    private async void OnRetryProbe(object sender, RoutedEventArgs e)
    {
        var group = _group;
        try
        {
            RetryProbeButton.IsEnabled = false;
            ShowChannelState(InfoBarSeverity.Informational, "正在重新探测配置通道", "重新打开设备并握手…");

            if (group is null)
            {
                return;
            }

            AppState.StopHealthMonitor();

            await Task.Run(() =>
            {
                AppState.Keyboard?.Dispose();
                AppState.Keyboard = AppState.Service.ConnectKeyboard(group.Channel);
                AppState.KeyboardSettings = null;
            });

            bool ready = await AppState.PrepareKeyboardAsync(group);
            if (_unloaded)
            {
                return;
            }

            if (!ready)
            {
                MainWindow.SetStatus($"未连接 · {group.Name}");
                ShowChannelState(
                    InfoBarSeverity.Error, "仍然没有应答",
                    "设备没有回应配置命令。确认键盘插好、官方 MCHOSE HUB 已退出，然后再试一次。");
                return;
            }

            AppState.StartHealthMonitor();
            MainWindow.SetStatus($"已连接 · {group.Name}");
            HideChannelState();
            await ReloadKeyboardSettingsAsync();
            await ReadKeysAsync();
            await ReloadDiyAsync();
        }
        catch (Exception ex)
        {
            AppState.WriteLog($"重新探测失败：{ex}");
            ShowChannelState(InfoBarSeverity.Error, "重新探测失败", Friendly(ex));
        }
        finally
        {
            RetryProbeButton.IsEnabled = true;
        }
    }

    // ---------------- 逐键调光 ----------------

    private void BuildDiyBoard()
    {
        DiyBoard.Children.Clear();
        _diyKeys.Clear();
        DiyBoard.Width = 0;
        double rowHeight = KeyUnit;
        var board = Settings?.Model.Board ?? [];
        double x = 0, row = 0;
        foreach (var boardKey in board)
        {
            string key = boardKey.Key;
            if (key is "backquote" or "Tab" or "Caps" or "Left_Shift" or "Left_Ctrl") { row++; x = 0; }
            x += boardKey.MarginLeft / 46.0;
            double width = (boardKey.Width + 4) / 46.0;

            var border = new Button
            {
                Width = width * KeyUnit - KeyGap,
                Height = boardKey.Height / 42.0 * rowHeight - KeyGap,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0)),
                Style = (Style)Application.Current.Resources["HubKeyStyle"],
                Tag = key,
                Padding = new Thickness(0),
                AllowFocusOnInteraction = false,
                Content = new TextBlock
                {
                    Text = ShortLabel(key),
                    FontSize = 11,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                },
            };

            ToolTipService.SetToolTip(border, key);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(border, $"{ShortLabel(key)}，设置灯色");
            border.Click += OnDiyKeyClicked;
            Canvas.SetLeft(border, x * KeyUnit);
            Canvas.SetTop(border, row * rowHeight);
            DiyBoard.Children.Add(border);
            _diyKeys[key] = border;
            DiyBoard.Width = Math.Max(DiyBoard.Width, (x + width) * KeyUnit);
            x += width + boardKey.MarginRight / 46.0;
        }
    }

    private static string ShortLabel(string key) => key switch
    {
        "one" => "1", "two" => "2", "three" => "3", "four" => "4", "five" => "5",
        "six" => "6", "seven" => "7", "eight" => "8", "nine" => "9", "zero" => "0",
        "up" => "↑", "down" => "↓", "left" => "←", "right" => "→",
        "backquote" => "`",
        "transverse" => "-",
        "equal" => "=",
        "backslash" => "\\",
        "left_bracket" => "[",
        "right_bracket" => "]",
        "semicolon" => ";",
        "singlequotes" => "'",
        "comma" => ",",
        "dot" => ".",
        "slash" => "/",
        "Left_Shift" => "Shift",
        "Right_Shift" => "Shift",
        "Left_Ctrl" => "Ctrl",
        "Right_Ctrl" => "Ctrl",
        "Left_Alt" => "Alt",
        "Right_Alt" => "Alt",
        "Left_Win" => "Win",
        "Space" => "Space",
        "PgUp" => "PgUp",
        "PgDn" => "PgDn",
        _ => key,
    };

    /// <summary>把整张逐键灯色表读回来，重画键盘。</summary>
    private async Task ReloadDiyAsync()
    {
        var settings = Settings;
        if (settings is null || !IsKeyboard || _unloaded)
        {
            return;
        }

        try
        {
            var table = await Task.Run(settings.ReadDiyLight);
            if (_unloaded || !ReferenceEquals(settings, Settings))
            {
                return;
            }
            _diyTable = table;

            // 第一次拿到表的时候确保画布已经建好（BuildDiyBoard 在页面构造时
            // 可能还没拿到机型信息，这里兜一次底）。
            if (_diyKeys.Count == 0)
            {
                BuildDiyBoard();
            }

            PaintDiyBoard();
            int lit = Enumerable.Range(0, _diyTable.Size).Count(i => _diyTable[i] != ((byte)0, (byte)0, (byte)0));
            DiyHint.Text = _preview ? "选择颜色后点击按键，仅修改离线预览。"
                : $"{lit} 个灯位已设置颜色。点击按键配色；启用逐键灯光后显示。";
        }
        catch (Exception ex)
        {
            DiyHint.Text = $"逐键灯色读取失败：{Friendly(ex)}";
        }
    }

    private void PaintDiyBoard()
    {
        var model = Settings?.Model;
        if (_diyTable is null || model is null)
        {
            return;
        }

        foreach (var (key, border) in _diyKeys)
        {
            var index = model.FindLedIndex(key);
            if (index is null)
            {
                border.Opacity = 0.35;
                continue;
            }

            var (r, g, b) = _diyTable[index.Value];
            bool black = r == 0 && g == 0 && b == 0;
            var color = Color.FromArgb(255, r, g, b);
            var brush = new SolidColorBrush(color);
            border.Background = brush;
            if (border.Content is TextBlock label)
            {
                // 亮色键上用深色字，不然看不清
                double luma = 0.299 * r + 0.587 * g + 0.114 * b;
                label.Foreground = new SolidColorBrush(luma > 140
                    ? Color.FromArgb(255, 16, 16, 16)
                    : Color.FromArgb(255, 255, 255, 255));
            }

            border.ClearValue(Control.BorderBrushProperty);
            border.Style = (Style)Application.Current.Resources[
                key == _diySelected ? "HubSelectedKeyStyle" : "HubKeyStyle"];
            if (key != _diySelected && !black) border.BorderBrush = brush;
        }

    }

    private void OnDiyKeyClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        var key = tag;
        _diySelected = key;
        Run(() => ApplyKeyColorAsync(key, ColorSelector.Color), "逐键染色失败");
    }

    private async Task ApplyKeyColorAsync(string key, Color color)
    {
        var settings = Settings;
        if (settings is null || _diyTable is null)
        {
            return;
        }

        DiySelection.Text = $"已选中 {key}，正在染成 #{color.R:X2}{color.G:X2}{color.B:X2}";

        var table = _diyTable;
        bool ok = KeyboardSettings.SetKeyColor(settings.Model, table, key, color.R, color.G, color.B);
        if (!ok)
        {
            DiySelection.Text = $"机型 {settings.Model.Model} 没有「{key}」这个键";
            return;
        }

        // 先在界面上上色，写入交给合并队列（连点十几个键只会写一次）
        PaintDiyBoard();
        DiySelection.Text = $"{key} = #{color.R:X2}{color.G:X2}{color.B:X2}";
        QueueDiyWrite(describeSwitch: true);
        await Task.CompletedTask;
    }

    /// <summary>
    /// 逐键颜色要在「自定义」灯效下才渲染，画之前先确保已经切过去。
    /// 返回 true 表示这次顺手切了灯效。
    /// </summary>
    // ------------------------------------------------------------------
    // 逐键颜色：合并写入
    //
    // 逐键表是一整块 385 字节的配置存储写入，比灯效那条命令还重。
    // 以前每点一个键就「读→写→再读一遍」，用户连着点十几个键就是十几次全量擦写，
    // 固件很容易被写卡（键盘像死机，要拔插）。现在：
    //   · 点键只改内存里的表和界面，写入攒 600 毫秒；
    //   · 两次真正写入之间至少隔 1.5 秒；
    //   · 写入进行中来新改动就合并成「最后一版」；
    //   · 写完不再整体回读（写路径自己已经回读校验过），界面以本地表为准。
    // ------------------------------------------------------------------

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _diyApply;
    private bool _diyWriteInFlight;
    private bool _diyWritePending;
    private DateTime _lastDiyWriteUtc = DateTime.MinValue;
    private static readonly TimeSpan DiyCooldown = TimeSpan.FromMilliseconds(1500);

    /// <summary>攒一次逐键颜色的下发。连续的点击会被合并成一次写入。</summary>
    private void QueueDiyWrite(bool describeSwitch)
    {
        _diyPendingDescribeSwitch = describeSwitch;
        if (_unloaded || !IsKeyboard)
        {
            return;
        }

        _diyApply?.Stop();
        _diyApply?.Start();
    }

    private bool _diyPendingDescribeSwitch;

    private async Task FlushDiyAsync()
    {
        var settings = Settings;
        if (settings is null || _diyTable is null || _unloaded)
        {
            return;
        }

        if (_diyWriteInFlight)
        {
            _diyWritePending = true;
            return;
        }

        _diyWriteInFlight = true;
        try
        {
            var since = DateTime.UtcNow - _lastDiyWriteUtc;
            if (since < DiyCooldown)
            {
                await Task.Delay(DiyCooldown - since);
            }

            if (_unloaded || !ReferenceEquals(settings, Settings))
            {
                _diyWritePending = false;
                return;
            }

            var table = _diyTable.Clone();
            await Task.Run(() => settings.WriteDiyLight(table));
            if (_unloaded || !ReferenceEquals(settings, Settings)) return;
            _lastDiyWriteUtc = DateTime.UtcNow;
            _inCustomEffect = await Task.Run(() => IsCustomEffect(settings));
            if (_unloaded || !ReferenceEquals(settings, Settings)) return;

            var colored = CountColoredLeds(table);
            if (_inCustomEffect)
            {
                DiySelection.Text = $"已保存（{colored} 个键有颜色），当前就是「自定义」灯效";
            }
            else
            {
                DiySelection.Text = _diyPendingDescribeSwitch
                    ? $"已保存 {colored} 个灯位。点击「启用逐键灯光」后显示。"
                    : $"已保存（{colored} 个键有颜色）";
            }
        }
        catch (Exception ex)
        {
            _diyWritePending = false;
            _diyTable = null;
            if (!_unloaded) await ReloadDiyAsync();
            DiySelection.Text = $"下发失败：{Friendly(ex)}";
            AppState.WriteLog($"逐键颜色下发失败：{ex.Message}");
        }
        finally
        {
            _diyWriteInFlight = false;
        }

        if (_diyWritePending)
        {
            _diyWritePending = false;
            await FlushDiyAsync();
        }
    }

    /// <summary>
    /// 逐键颜色只有在「自定义」灯效下才渲染，所以写完要保证设备真的在这个灯效上
    /// ——但不能反过来把用户从别的灯效硬拽过来：表里没上色的键在「自定义」下是黑的，
    /// 一旦自动切过去，用户看到的就是「键盘怎么黑了」。这里只做状态确认，
    /// 切换交给用户点那个按钮。
    /// </summary>
    private static bool IsCustomEffect(KeyboardSettings settings)
    {
        try
        {
            var current = settings.ReadPerformance();
            return current.LightMode == settings.Model.CustomLightMode && current.LightType == 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnFillAllKeys(object sender, RoutedEventArgs e) =>
        Run(FillAllKeysAsync, "填充失败");

    private async Task FillAllKeysAsync()
    {
        if (_diyTable is null || Settings is null)
        {
            return;
        }

        var color = ColorSelector.Color;
        _diyTable.Fill(color.R, color.G, color.B);
        PaintDiyBoard();
        DiySelection.Text = $"全部 LED = #{color.R:X2}{color.G:X2}{color.B:X2}";

        // 整块都上色了，切过去一定是亮的，顺手切
        await SwitchToCustomModeAsync(announce: false);
        QueueDiyWrite(describeSwitch: false);
    }

    private void OnClearAllKeys(object sender, RoutedEventArgs e) =>
        Run(ClearAllKeysAsync, "清空失败");

    private Task ClearAllKeysAsync()
    {
        if (_diyTable is null || Settings is null)
        {
            return Task.CompletedTask;
        }

        _diyTable.Fill(0, 0, 0);
        PaintDiyBoard();
        DiySelection.Text = "已清空逐键颜色（表里已经没有颜色，键盘在「自定义」下会是全黑的）";
        QueueDiyWrite(describeSwitch: false);
        return Task.CompletedTask;
    }

    /// <summary>恢复网页版 lightDiyObj 里那份出厂逐键灯色。</summary>
    private void OnRestoreDiyDefault(object sender, RoutedEventArgs e) =>
        Run(RestoreDiyDefaultAsync, "恢复出厂逐键灯色失败");

    private async Task RestoreDiyDefaultAsync()
    {
        var settings = Settings;
        if (settings is null || _diyTable is null)
        {
            return;
        }

        var defaults = settings.Model.DefaultDiyColors;
        if (defaults is null)
        {
            DiySelection.Text = "这个机型没有收录出厂逐键灯色";
            return;
        }

        for (int i = 0; i < Math.Min(_diyTable.Size, defaults.Count); i++)
        {
            _diyTable[i] = defaults[i];
        }

        PaintDiyBoard();
        DiySelection.Text = "已恢复出厂逐键灯色";

        await SwitchToCustomModeAsync(announce: false);
        QueueDiyWrite(describeSwitch: false);
    }

    private void OnReloadDiy(object sender, RoutedEventArgs e) => _ = ReloadDiyAsync();

    private void OnSwitchToCustomMode(object sender, RoutedEventArgs e) =>
        Run(() => SwitchToCustomModeAsync(announce: true), "切换灯效失败");

    /// <summary>
    /// 切到「自定义（逐键调色）」灯效。逐键颜色只在这个灯效下渲染，
    /// 所以画键盘之前必须先切过来——这也是之前「点了没反应」的原因。
    /// </summary>
    private async Task SwitchToCustomModeAsync(bool announce)
    {
        var settings = Settings;
        if (settings is null)
        {
            return;
        }

        try
        {
            // 「自定义」灯效显示什么，完全由逐键颜色表决定。表里几乎没有颜色的时候
            // 切过去键盘就是一片黑——用户看到的就是「键盘怎么黑了」。
            // 表里一个颜色都没有就直接拦下来（那是「关闭灯效」该干的事）；
            // 只有零星几个颜色就提醒一句再切——用户可能就是要这个效果。
            int colored = _diyTable is null ? -1 : CountColoredLeds(_diyTable);
            if (colored == 0)
            {
                DiySelection.Text = "逐键颜色表里一个颜色都没有，切过去键盘会全黑。先点几个键，或者用「全部填充」；只想关灯的话选「关闭」灯效。";
                Toast.Warning("逐键表是空的，先上色再切",
                    "「自定义」灯效只显示逐键颜色表，表里没颜色时键盘就是全黑的。");
                return;
            }

            if (colored is > 0 and < 6)
            {
                Toast.Info("正在切到「自定义」灯效",
                    $"逐键表里只有 {colored} 个键有颜色，其它键在「自定义」下是不亮的。");
            }

            var current = await Task.Run(settings.ReadPerformance);

            // 两个都要对上：设备可能是 lightMode=21 但 lightType 还是 0 的
            // 「半切过去」状态（正是「点了没反应」的样子），只比 lightMode 会漏掉它。
            if (current.LightMode != settings.Model.CustomLightMode || current.LightType != 1)
            {
                await Task.Run(() => settings.WritePerformance(
                    current with { LightMode = settings.Model.CustomLightMode }));
            }

            await Task.Delay(120);
            await ReloadKeyboardSettingsAsync();
            if (announce)
            {
                DiySelection.Text = "已切到「自定义」灯效，键盘上应该能看到逐键颜色了";
            }
        }
        catch (Exception ex)
        {
            DiySelection.Text = $"切换失败：{Friendly(ex)}";
        }
    }

    /// <summary>逐键颜色表里有几个 LED 是有颜色的（用来判断「切过去会不会全黑」）。</summary>
    private static int CountColoredLeds(DiyLightTable table)
    {
        int count = 0;
        int length = Math.Min(table.Red.Length, Math.Min(table.Green.Length, table.Blue.Length));
        for (int i = 0; i < length; i++)
        {
            if (table.Red[i] != 0 || table.Green[i] != 0 || table.Blue[i] != 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>把设备上的真实设置读回来填进控件。</summary>
    private async Task ReloadKeyboardSettingsAsync()
    {
        var settings = Settings;
        if (settings is null || !IsKeyboard)
        {
            return;
        }

        try
        {
            // 机型确定后才知道「自定义」是 19 还是 21，这时补进列表
            _effects = KeyboardEffectTable.ForModel(settings.Model.CustomLightMode, settings.Model.Model);

            var performance = await Task.Run(settings.ReadPerformance);
            var lighting = await Task.Run(settings.ReadLighting);
            if (_unloaded)
            {
                return;
            }

            // lightType 也必须是 1，否则设备不会渲染逐键颜色表
            _inCustomEffect = lighting.LightMode == settings.Model.CustomLightMode
                              && performance.LightType == 1;

            // 回填控件时不能触发「改了就自动下发」，否则读一次设备就会写回去一次
            WithoutAutoApply(() =>
            {
                MultiColorSwitch.IsOn = performance.MultiColor;
                RateBox.IsEnabled = settings.SupportsPollingRate;
                RateBox.SelectedIndex = Math.Clamp((int)performance.Rate - 1, 0, KeyboardSettings.PollingRatesHz.Length - 1);
                SleepBox.SelectedIndex = Math.Max(0, Array.IndexOf(KeyboardSettings.SleepOptionsMinutes, performance.SleepMinutes));
                LatencySwitch.IsOn = performance.Latency == LatencyMode.Speed;
                LatencySwitch.Visibility = settings.Model.Model == "Z75" ? Visibility.Collapsed : Visibility.Visible;
                Z75LatencyBox.Visibility = settings.Model.Model == "Z75" ? Visibility.Visible : Visibility.Collapsed;
                Z75LatencyBox.SelectedIndex = performance.Latency switch { LatencyMode.LowLatencyStandard => 1, LatencyMode.Speed => 2, _ => 0 };
                WinLockSwitch.IsOn = performance.WinKeyLocked;
                MacModeSwitch.IsOn = performance.MacMode;

                RefreshEffectList(lighting.LightMode);
                BrightnessSlider.Value = lighting.Brightness;
                SpeedSlider.Value = Math.Clamp(lighting.Speed, 0, 4) + 1;
                ColorSelector.Color = Color.FromArgb(255, lighting.Red, lighting.Green, lighting.Blue);

                // 回填之后标签要跟着走，否则显示的还是上一次的数值
                BrightnessLabel.Text = $"亮度 {(int)BrightnessSlider.Value}%";
                SpeedLabel.Text = $"速度 {(int)SpeedSlider.Value} 档";
            });

            // 设备里现在真实存着的颜色：之后「颜色没变就不重复写」以它为准，
            // 这样即使中间被键盘上的快捷键或官方客户端改过，也不会误跳过。
            _lastWrittenColor = $"{lighting.LightMode}|{lighting.Red:X2}{lighting.Green:X2}{lighting.Blue:X2}";

            BuildEffectQuickButtons();
            UpdateEffectIndicators(lighting.LightMode);

            PerformanceResult.Text =
                $"机型 {settings.Model.Model}（{settings.Model.KeyList.Count} 键）\r\n" +
                $"回报率 {(int)performance.Rate switch { 1 => "250Hz", 2 => "500Hz", _ => "1000Hz" }}  " +
                $"休眠 {(performance.SleepMinutes == 0 ? "关闭" : performance.SleepMinutes + " 分钟")}  " +
                $"低延迟 {(performance.Latency == LatencyMode.Speed ? "开" : "关")}  " +
                $"Win 键 {(performance.WinKeyLocked ? "锁定" : "正常")}  " +
                $"Mac 模式 {(performance.MacMode ? "开" : "关")}  " +
                $"灯效 {performance.LightMode}";

            KeySummary.Text = string.Empty;
        }
        catch (Exception ex)
        {
            AppState.WriteLog($"读取设置失败：{ex}");
            Note(InfoBarSeverity.Error, "读取失败", Friendly(ex));
        }
    }

    private void OnReloadSettings(object sender, RoutedEventArgs e) => _ = ReloadKeyboardSettingsAsync();

    /// <summary>当前下拉里实际列出来的灯效。下标 → 编号的换算都走它。</summary>
    private List<KeyboardEffect> _visibleEffects = [];

    private int SelectedEffectId =>
        EffectBox.SelectedIndex >= 0 && EffectBox.SelectedIndex < _visibleEffects.Count
            ? _visibleEffects[EffectBox.SelectedIndex].Id
            : 1;

    private KeyboardEffect? SelectedEffect =>
        EffectBox.SelectedIndex >= 0 && EffectBox.SelectedIndex < _visibleEffects.Count
            ? _visibleEffects[EffectBox.SelectedIndex]
            : null;

    /// <summary>重建灯效下拉。selectId 是要选中的灯效编号。</summary>
    private void RefreshEffectList(int? selectId = null)
    {
        // 先把「要保留哪个」算出来，再换列表——顺序反了会拿新列表的旧下标去查
        int keep = selectId ?? SelectedEffectId;

        WithoutAutoApply(() =>
        {
            _visibleEffects = _effects
                .Where(e => _showAllEffects
                            || KeyboardEffectTable.Common.Contains(e.Id)
                            || e.IsCustom)
                .OrderBy(e => e.Id)
                .ToList();

            EffectBox.ItemsSource = _visibleEffects.Select(e => $"{e.Name}（{e.Id}）").ToList();

            int index = _visibleEffects.FindIndex(e => e.Id == keep);
            EffectBox.SelectedIndex = index >= 0 ? index : 0;
        });
    }

    private void OnEffectMoreToggled(object sender, RoutedEventArgs e)
    {
        if (!IsKeyboard || _visibleEffects.Count == 0)
        {
            return;
        }

        _showAllEffects = EffectMoreSwitch.IsOn;
        RefreshEffectList();
    }

    /// <summary>
    /// 把常用灯效摆成一排按钮（关闭 / 常亮 / 呼吸 / 梦幻彩虹 / 该机型的自定义）。
    ///
    /// 官方网页版就是一排灯效图标，点一下立刻切过去。下拉弹层在有些输入方式下
    /// 点不动（它是独立的弹出窗口），所以常用灯效不能再依赖下拉。
    /// </summary>
    private void BuildEffectQuickButtons()
    {
        EffectQuickPanel.Children.Clear();
        if (!IsKeyboard)
        {
            return;
        }

        foreach (var effect in _effects
                     .Where(e => KeyboardEffectTable.Common.Contains(e.Id) || e.IsCustom)
                     .OrderBy(e => e.Id))
        {
            var button = new Button
            {
                Content = effect.Name,
                Tag = effect.Id,
                MinWidth = 88,
            };
            ToolTipService.SetToolTip(button, $"{effect.Name}（编号 {effect.Id}）");
            button.Click += OnQuickEffect;
            EffectQuickPanel.Children.Add(button);
        }

        // 这些按钮是页面 Loaded 之后才建出来的，防误触要单独补一遍
        UiGuard.ApplyTo(EffectQuickPanel);
    }

    private void OnQuickEffect(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id } || !IsKeyboard)
        {
            return;
        }

        // 先把下拉同步到这个灯效（这一步不触发自动下发），再手动写一次，
        // 免得「按钮 + 下拉」各写一遍。
        WithoutAutoApply(() => SelectEffect(id));
        ScheduleAutoApply();
    }

    /// <summary>把下拉选到指定灯效编号。被「只显示常用灯效」挡住时会自动展开全部编号。</summary>
    private void SelectEffect(int id)
    {
        if (_visibleEffects.Any(e => e.Id == id) || _effects.All(e => e.Id != id))
        {
            int index = _visibleEffects.FindIndex(e => e.Id == id);
            if (index >= 0)
            {
                EffectBox.SelectedIndex = index;
            }

            return;
        }

        EffectMoreSwitch.IsOn = true;
        _showAllEffects = true;
        RefreshEffectList(id);
    }

    /// <summary>选中态高亮 + 一行「当前灯效」说明，让用户一眼看到设备现在是什么状态。</summary>
    private void UpdateEffectIndicators(int currentId)
    {
        _deviceEffectId = currentId;

        var accent = (Style)Application.Current.Resources["AccentButtonStyle"];
        foreach (var child in EffectQuickPanel.Children)
        {
            if (child is Button { Tag: int id } button)
            {
                button.Style = id == currentId ? accent : null;
            }
        }

        var current = _effects.FirstOrDefault(e => e.Id == currentId);
        EffectStatus.Text = current is null
            ? $"设备当前灯效：{currentId}"
            : $"设备当前灯效：{current.Name}（{currentId}）";
    }

    /// <summary>
    /// 下拉收起来时兜一次底：有些输入方式不一定每次都派发 SelectionChanged，
    /// 只要选中的和设备上的不一致，就再安排一次下发。
    /// </summary>
    private void OnEffectDropDownClosed(object sender, object e)
    {
        if (!IsKeyboard || SelectedEffectId == _deviceEffectId)
        {
            return;
        }

        ScheduleAutoApply();
    }

    private void OnEffectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsKeyboard || EffectBox.SelectedIndex < 0)
        {
            return;
        }

        // 官方对每个灯效都标了哪些参数可调（比如「关闭」全灰、「梦幻彩虹」不给颜色、
        // 「自定义逐键调色」不给亮度和速度），这里照着置灰，免得用户拖了个没用的滑杆。
        var effect = SelectedEffect;
        bool brightnessOn = effect is null || !effect.BrightnessDisabled;
        bool speedOn = effect is null || !effect.SpeedDisabled;
        bool colorOn = effect is null || !effect.ColorDisabled;

        WithoutAutoApply(() =>
        {
            BrightnessSlider.IsEnabled = brightnessOn;
            SpeedSlider.IsEnabled = speedOn;
            ColorSection.Visibility = colorOn ? Visibility.Visible : Visibility.Collapsed;
            MultiColorSwitch.IsEnabled = IsKeyboard && colorOn && effect?.IsCustom != true;

            // 亮度、速度跟官方一样是「全局一份」：切灯效时沿用用户当前的值，
            // 不要反过来去读设备里那个灯效的存档值——那一份通常是出厂的 3%，
            // 拉过来就等于把新灯效写成一片黑（「切了没反应」就是这么来的）。
            //
            // 少数灯效（呼吸、旋转风车…）官方不给亮度滑杆，写入时会固定给满亮度，
            // 界面这里也照实显示。
            if (effect is { BrightnessDisabled: true, IsCustom: false })
            {
                BrightnessSlider.Value = 100;
            }
        });

        EffectNote.Text = effect switch
        {
            { IsCustom: true } =>
                "自定义灯效使用逐键颜色，不支持单独调整亮度和速度。",
            { Id: 0 } => "灯效已关闭，亮度和颜色不适用。",
            _ when !brightnessOn || !speedOn || !colorOn =>
                $"这个灯效不支持调整：{string.Join('、', new[]
                {
                    brightnessOn ? null : "亮度",
                    speedOn ? null : "速度",
                    colorOn ? null : "颜色",
                }.Where(x => x is not null))}。",
            _ => string.Empty,
        };
        EffectNote.Visibility = string.IsNullOrEmpty(EffectNote.Text) ? Visibility.Collapsed : Visibility.Visible;

        // 选完灯效就直接写下去，不用再点「应用」——网页版就是这样
        ScheduleAutoApply();
    }

    private void OnBrightnessChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        BrightnessLabel.Text = $"亮度 {(int)e.NewValue}%";
        ScheduleAutoApply();
    }

    private void OnMultiColorChanged(object sender, RoutedEventArgs e) => ScheduleAutoApply();

    private void OnSpeedChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        SpeedLabel.Text = $"速度 {(int)e.NewValue} 档";
        ScheduleAutoApply();
    }

    private void OnColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        // 「自定义」灯效下颜色由逐键表决定，拖色板不该顺手改键，
        // 交给「应用」（写到选中的键）或直接点键盘图上的键。
        if (_inCustomEffect)
        {
            return;
        }

        ScheduleAutoApply();
    }

    private void OnKeyLayerChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedKey = null;
        if (RemapPanel is not null) RemapPanel.Visibility = Visibility.Collapsed;
        HighlightRemapKey();
        _ = ReadKeysAsync();
    }

    // ---------------- 分区切换 ----------------

    private string? CurrentSection => (SectionNav.SelectedItem as FrameworkElement)?.Tag as string;

    private void OnLightingLayoutChanged(object sender, SizeChangedEventArgs e)
    {
        if (LightingKeyboardCard is null || LightingParametersCard is null || LightingMaintenance is null || e.NewSize.Width <= 0) return;
        bool narrow = e.NewSize.Width < 900;
        Grid.SetColumnSpan(LightingKeyboardCard, narrow ? 2 : 1);
        Grid.SetColumn(LightingParametersCard, narrow ? 0 : 1);
        Grid.SetColumnSpan(LightingParametersCard, narrow ? 2 : 1);
        Grid.SetRow(LightingParametersCard, narrow ? 2 : 1);
        Grid.SetRow(LightingNote, narrow ? 3 : 2);
        Grid.SetRow(LightingMaintenance, narrow ? 4 : 3);
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (CurrentSection is not string tag)
        {
            return;
        }

        PanelLighting.Visibility = tag == "lighting" ? Visibility.Visible : Visibility.Collapsed;
        PanelKeys.Visibility = tag == "keys" ? Visibility.Visible : Visibility.Collapsed;
        PanelMacros.Visibility = tag == "macros" ? Visibility.Visible : Visibility.Collapsed;
        PanelProfiles.Visibility = tag == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        PanelPerformance.Visibility = tag == "performance" ? Visibility.Visible : Visibility.Collapsed;
        PanelDiagnostics.Visibility = tag == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        PanelAbout.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "profiles") RefreshProfileLibrary();
    }

    // ---------------- 灯光 ----------------

    private void OnApplyLighting(object sender, RoutedEventArgs e) =>
        Run(() => ApplyLightingAsync(), "下发失败");

    /// <summary>
    /// 把灯光面板上的当前选择写进设备。
    ///
    /// <paramref name="auto"/> 为 true 表示这是控件改完自动触发的（拖滑杆时不弹成功提示，
    /// 免得提示条一直闪）。
    /// </summary>
    private async Task ApplyLightingAsync(bool auto = false)
    {
        Color color = ColorSelector.Color;
        int brightness = Math.Clamp((int)BrightnessSlider.Value, 0, 100);
        // 界面是 1–5 档，设备认 0–4
        int speed = Math.Clamp((int)SpeedSlider.Value - 1, 0, 4);
        int effect = SelectedEffectId;
        bool multiColor = MultiColorSwitch.IsOn;

        if (IsKeyboard)
        {
            var settings = Settings;
            if (settings is null)
            {
                Note(InfoBarSeverity.Error, "键盘通道未就绪", "先回设备页重新打开设备。");
                return;
            }

            // 值和上次写进去的一模一样就别再写一遍：拖进度条来回蹭、
            // 切灯效后界面回填，都会走到这里。少写一次，键盘就少一次卡住的机会。
            var signature = $"{effect}|{brightness}|{speed}|{multiColor}|{color.R:X2}{color.G:X2}{color.B:X2}";
            if (auto && signature == _lastWrittenSignature)
            {
                return;
            }

            // 灯效编号和设备现在的对不上 = 这次是「换灯效」，只切模式，
            // 不要顺手把选中那个键的颜色也改了。
            bool switchingMode = effect != _deviceEffectId;

            // 「自定义（逐键调色）」灯效下，键盘显示的是下面那张逐键颜色表，
            // 改 lightModeColorN 对画面没有任何影响。以前这里照样写一遍、
            // 照样报「已下发并回读确认」，用户看到的就是「说成功但没反应」。
            // 既然是逐键灯效，就把选中的颜色直接落到键上。
            if (!switchingMode && effect == settings.Model.CustomLightMode)
            {
                if (_diyTable is null || _diySelected is null)
                {
                    if (auto)
                    {
                        // 自动触发时只留一句轻提示，不要弹 InfoBar
                        if (DiySelection is not null)
                        {
                            DiySelection.Text = "先选择颜色，再点击键盘图上的按键。";
                        }
                    }
                    else
                    {
                        Note(InfoBarSeverity.Informational, "自定义灯效要用逐键调色",
                            "先选择颜色，再点击键盘图上的按键，或使用「全部填充」。");
                    }

                    return;
                }

                await ApplyKeyColorAsync(_diySelected, color);
                if (!auto)
                {
                    Note(InfoBarSeverity.Success, "已按逐键灯效下发",
                        $"当前灯效是「{SelectedEffect?.Name ?? "自定义"}」，颜色已写到 {_diySelected} 上。");
                }

                return;
            }

            try
            {
                // 切到「自定义」之前先看一眼逐键表：表里一个颜色都没有的话，
                // 键盘切过去就是一片黑，用户很容易以为「点一下就坏了」。
                // 提示一句，写入照旧——他要切就切。
                if (switchingMode && effect == settings.Model.CustomLightMode
                    && _diyTable is not null && CountColoredLeds(_diyTable) == 0)
                {
                    Toast.Warning("逐键表还是空的",
                        "「自定义」灯效显示的是逐键颜色表；表里一个颜色都没有，切过去键盘会全黑。" +
                        "先点几个键上色，或用「全部填充」。");
                }

                // 灯光设置分散在两条命令上：亮度和模式走 setPerformance，颜色走 setLightColor
                var performance = await Task.Run(() =>
                {
                    var current = settings.ReadPerformance();
                    return current with
                    {
                        LightMode = effect,
                        Brightness = brightness,
                        Speed = speed,
                        MultiColor = multiColor,
                    };
                });

                await Task.Run(() => settings.WritePerformance(performance));

                // 颜色没变就不写第二条命令。拖亮度时颜色是没变的，这一省就少一整次
                // 519 字节的配置存储擦写——键盘被写卡的概率直接减半。
                var colorSignature = $"{effect}|{color.R:X2}{color.G:X2}{color.B:X2}";
                if (colorSignature != _lastWrittenColor)
                {
                    // 两条写命令之间留出落盘时间：固件一次只干一件事
                    await Task.Delay(150);
                    await Task.Run(() => settings.WriteLighting(new LightingSettings
                    {
                        LightMode = effect,
                        Brightness = brightness,
                        Speed = speed,
                        Red = color.R,
                        Green = color.G,
                        Blue = color.B,
                    }));
                    _lastWrittenColor = colorSignature;
                }

                _lastWrittenSignature = signature;

                // 写完立刻把「设备当前灯效」和快捷按钮的高亮更新掉。
                // 以前这里只等下一次整体回读才更新，用户点完看到标题还写着旧灯效、
                // 高亮还在上一个按钮上，就会以为「点了没反应」。
                UpdateEffectIndicators(effect);

                if (auto)
                {
                    // 自动下发（换灯效 / 拖滑杆）：不弹提示、也不回读一遍。
                    // 写路径内部本来就有回读校验；再整体读一遍性能 + 颜色是两次多余的
                    // HID 往返，而且会在用户正拖着滑杆时拿设备值把界面覆盖掉。
                    return;
                }

                await ReloadKeyboardSettingsAsync();

                var applied = SelectedEffect;
                Note(InfoBarSeverity.Success, "已下发并回读确认",
                    $"{EffectBox.SelectedItem}" +
                    (applied is null || applied.BrightnessDisabled ? string.Empty : $"，亮度 {brightness}") +
                    (applied is null || applied.SpeedDisabled ? string.Empty : $"，速度 {speed}") +
                    (applied is null || applied.ColorDisabled ? string.Empty : $"，颜色 #{color.R:X2}{color.G:X2}{color.B:X2}"));
            }
            catch (Exception ex)
            {
                AppState.WriteLog($"灯光下发失败：{ex}");
                Note(InfoBarSeverity.Error, "下发失败", Friendly(ex));
            }

            return;
        }

        var connection = AppState.Connection;
        if (connection is null)
        {
            Note(InfoBarSeverity.Error, "未连接", "音频通道未就绪。");
            return;
        }

        try
        {
            byte mode = (byte)Math.Max(0, EffectBox.SelectedIndex);
            await connection.ApplyLightingAsync(mode, (byte)brightness, (byte)speed, 0, color.R, color.G, color.B, false);
            Note(InfoBarSeverity.Success, "已下发",
                $"模式 {mode}，亮度 {brightness}，周期 {speed}，颜色 #{color.R:X2}{color.G:X2}{color.B:X2}");
        }
        catch (Exception ex)
        {
            AppState.WriteLog($"性能下发失败：{ex.GetType().Name} {ex.Message}");
            Note(InfoBarSeverity.Error, "下发失败", Friendly(ex));
        }
    }

    private void OnRestoreDefault(object sender, RoutedEventArgs e) =>
        Run(RestoreDefaultAsync, "恢复默认失败");

    private void OnRepairLight(object sender, RoutedEventArgs e) =>
        Run(RepairLightProfileAsync, "修复灯效参数失败");

    private void OnForceLight(object sender, RoutedEventArgs e) =>
        Run(ForceVisibleLightAsync, "强制点亮失败");

    /// <summary>
    /// 「强制点亮」：键盘一片黑时的一键自救。
    ///
    /// 只写三个用户可见的字段——灯效=常亮(1)、亮度=100、lightType=0（退出逐键模式），
    /// 再把当前灯效的颜色设成白色。**不动固件的参数区**，所以不会触发固件那套
    /// 「写 0x04 = 恢复出厂」的动作，是最轻的恢复手段。
    ///
    /// 黑色的常见原因有三种，它一次盖住两种：
    ///  1. 还停在「自定义（逐键调色）」灯效上，而逐键表几乎是黑的；
    ///  2. 亮度被拖到了 0，或者残留着出厂那份 3%（肉眼就是全黑）；
    ///  3. 固件参数区被写脏（这种要点「修复灯效」）。
    /// </summary>
    private async Task ForceVisibleLightAsync()
    {
        var settings = Settings;
        if (settings is null || !IsKeyboard)
        {
            Toast.Error("键盘通道未就绪", "先回设备页重新打开设备。");
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                var current = settings.ReadPerformance();
                settings.WritePerformance(current with
                {
                    // 常亮 + 满亮度 + 退出逐键模式
                    LightMode = 1,
                    LightType = 0,
                    Brightness = 100,
                    Speed = 0,
                });

                settings.WriteLighting(new LightingSettings
                {
                    LightMode = 1,
                    Brightness = 100,
                    Speed = 0,
                    Red = 0xFF,
                    Green = 0xFF,
                    Blue = 0xFF,
                });
            });

            _lastWrittenSignature = null;
            _lastWrittenColor = null;
            await ReloadKeyboardSettingsAsync();

            Toast.Success("已强制点亮", "灯效改回常亮、满亮度、白色。还是黑的话点「修复灯效」。");
            Note(InfoBarSeverity.Success, "已强制点亮",
                "灯效 = 常亮（编号 1），亮度 100%，颜色白色。这一步没有动固件参数区。");
            AppState.WriteLog("强制点亮：lightMode=1 lightType=0 brightness=100 color=#FFFFFF");
        }
        catch (Exception ex)
        {
            AppState.WriteLog($"强制点亮失败：{ex}");
            Toast.Error("强制点亮失败", Friendly(ex));
            Note(InfoBarSeverity.Error, "强制点亮失败", Friendly(ex));
        }
    }

    /// <summary>
    /// 「修复灯效」：界面显示下发成功、设备也存下了，但键盘上没反应时用。
    ///
    /// 那种情况通常是固件内部的灯效参数区（space2..space11 + 每个灯效的亮度/速度/多彩槽位）
    /// 被写脏了，固件已经不认识这张表。这里把它重铺成出厂值，用户自己的设置（回报率、
    /// 灯效编号、颜色、当前灯效亮度）原样保留。
    /// </summary>
    private async Task RepairLightProfileAsync()
    {
        var settings = Settings;
        if (settings is null || !IsKeyboard)
        {
            Note(InfoBarSeverity.Error, "键盘通道未就绪", "先回设备页重新打开设备。");
            return;
        }

        try
        {
            var message = await Task.Run(settings.RepairLightProfile);
            await ReloadKeyboardSettingsAsync();
            await ReloadDiyAsync();
            Note(InfoBarSeverity.Success, "灯效参数已还原出厂值", message);
            AppState.WriteLog(message);
        }
        catch (Exception ex)
        {
            AppState.WriteLog($"修复灯效参数失败：{ex.GetType().Name} {ex.Message}");
            Note(InfoBarSeverity.Error, "修复失败", Friendly(ex));
        }
    }

    private async Task RestoreDefaultAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "恢复默认设置",
            Content = "这会恢复官方出厂性能、灯色、逐键颜色和三个按键层；宏库保留。建议先备份当前配置。确定继续？",
            PrimaryButtonText = "恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (IsKeyboard)
        {
            var settings = Settings;
            if (settings is null)
            {
                Note(InfoBarSeverity.Error, "键盘通道未就绪", "先回设备页重新打开设备。");
                return;
            }

            try
            {
                await Task.Run(settings.RestoreFactory);

                await Task.Delay(120);
                await ReloadKeyboardSettingsAsync();
                await ReloadDiyAsync();
                await ReadKeysAsync();
                Note(InfoBarSeverity.Success, "已恢复出厂配置", "已写回官方出厂性能、灯色、逐键色，并恢复三个按键层；宏库保留。");
            }
            catch (Exception ex)
            {
                AppState.WriteLog($"灯光恢复默认失败：{ex}");
                Note(InfoBarSeverity.Error, "失败", Friendly(ex));
            }

            return;
        }

        try
        {
            if (AppState.Connection is { } connection)
            {
                await connection.RestoreFactoryAsync();
                Note(InfoBarSeverity.Success, "已发送", "恢复出厂设置指令已下发。");
            }
        }
        catch (Exception ex)
        {
            AppState.WriteLog($"恢复出厂失败：{ex}");
            Note(InfoBarSeverity.Error, "失败", Friendly(ex));
        }
    }

    /// <summary>把提示写到当前分区自己的 InfoBar 上，避免用户看不到。</summary>
    private void Note(InfoBarSeverity severity, string title, string message)
    {
        if (CurrentSection is "macros") { MacroStatus.Text = $"{title}：{message}"; return; }
        if (CurrentSection is "profiles") { ProfileStatus.Text = $"{title}：{message}"; return; }
        var target = CurrentSection is "performance"
            ? PerformanceNote
            : LightingNote;

        if (CurrentSection is "keys")
        {
            KeySummary.Text = $"{title}：{message}";
            return;
        }

        target.Severity = severity;
        target.Title = title;
        target.Message = message;
        target.IsOpen = true;
    }

    // ---------------- 按键 / 性能 ----------------

    private void OnReadKeySetting(object sender, RoutedEventArgs e) => _ = ReadKeysAsync();

    private string? _selectedKey;

    private void OnKeyRowClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not KeyRow row)
        {
            return;
        }

        SelectRemapKey(row);
    }

    private void SelectRemapKey(KeyRow row)
    {
        _selectedKey = row.Key;
        RemapPanel.Visibility = Visibility.Visible;
        RemapLabel.Text = $"把 {row.Key} 改成：";

        var settings = Settings;
        if (settings is null)
        {
            return;
        }

        if (RemapTargetBox.ItemsSource is null)
        {
            RemapTargetBox.ItemsSource = settings.RemapTargets;
        }

        // 设备上现在是什么，就默认选中什么，方便对照
        var current = row.Value.StartsWith("→ ", StringComparison.Ordinal)
            ? row.Value[2..]
            : "默认";
        var pick = settings.RemapTargets.Contains(current) ? current : "默认";
        RemapTargetBox.SelectedItem = pick;
        RemapTargetBox.Text = pick;
        HighlightRemapKey();
    }

    /// <summary>目标键：可编辑下拉里以输入框内容为准，再退回选中项。</summary>
    private string? RemapTarget =>
        (string.IsNullOrWhiteSpace(RemapTargetBox.Text) ? RemapTargetBox.SelectedItem as string : RemapTargetBox.Text)
        ?.Trim();

    private void OnApplyRemap(object sender, RoutedEventArgs e) =>
        Run(() => ApplyRemapAsync(RemapTarget), "改键失败");

    private void OnResetKey(object sender, RoutedEventArgs e)
    {
        RemapTargetBox.SelectedItem = "默认";
        RemapTargetBox.Text = "默认";
        Run(() => ApplyRemapAsync("默认"), "恢复默认键位失败");
    }

    private async Task ApplyRemapAsync(string? target)
    {
        var settings = Settings;
        if (settings is null || _selectedKey is null)
        {
            return;
        }

        var layer = KeyLayers[Math.Clamp(KeyLayerBox.SelectedIndex, 0, KeyLayers.Length - 1)];
        string key = _selectedKey;

        var settingsSession = settings.RemapTargets;
        if (!string.IsNullOrEmpty(target)
            && target != "默认"
            && !settingsSession.Contains(target, StringComparer.OrdinalIgnoreCase))
        {
            KeySummary.Text = $"没有「{target}」这个键，换个名字再试（大小写不敏感）。";
            return;
        }

        try
        {
            await Task.Run(() => settings.RemapKey(layer.Key, key, target));
            await Task.Delay(120);
            await ReadKeysAsync();
            KeySummary.Text = $"{layer.Name}：{key} → {target ?? "默认"} 已下发并回读";
        }
        catch (Exception ex)
        {
            KeySummary.Text = $"改键失败：{Friendly(ex)}";
        }
    }

    private void OnReadMacro(object sender, RoutedEventArgs e) => Run(async () =>
    {
        var session = _preview ? _previewSession : AppState.Keyboard;
        if (session is null) throw new InvalidOperationException("请先连接键盘。");
        var result = await Task.Run(() => session.Read("getMacro"));
        if (_unloaded || !ReferenceEquals(session, _preview ? _previewSession : AppState.Keyboard)) return;
        MacroReadResult.Text = "设备宏目录片段（不能读取完整事件库）\r\n" + KeyboardProtocol.Describe(result);
        MacroReadResult.Visibility = Visibility.Visible;
        MacroStatus.Text = _preview ? "已读取离线宏目录示例。" : "已读取设备宏目录片段；本地宏草稿未改变。";
    }, "读取宏信息失败");

    /// <summary>读取某一层的按键映射，并把改过的键标出来。</summary>
    private async Task ReadKeysAsync()
    {
        var settings = Settings;
        int revision = ++_keyReadRevision;
        if (settings is null || !IsKeyboard || _unloaded)
        {
            KeySummary.Text = "键盘通道未就绪。";
            return;
        }

        var layer = KeyLayers[Math.Clamp(KeyLayerBox.SelectedIndex, 0, KeyLayers.Length - 1)];
        KeySummary.Text = "正在读取…";

        try
        {
            var rows = await Task.Run(() =>
                settings.ReadKeyMappings(layer.Key).Where(m => settings.Model.Board.Any(b => b.Key == m.Key))
                    .Select(m => new KeyRow
                    {
                        Key = m.Key,
                        Layer = layer.Name,
                        Value = m.Display,
                        Changed = !m.IsDefault,
                    })
                    .ToList());

            if (_unloaded || revision != _keyReadRevision || !ReferenceEquals(settings, Settings))
            {
                return;
            }

            KeyListView.ItemsSource = rows;
            BuildRemapBoard(rows);
            int changed = rows.Count(r => r.Changed);
            KeySummary.Text = $"{layer.Name}：共 {rows.Count} 个键，其中 {changed} 个改过键位。";
        }
        catch (Exception ex)
        {
            KeySummary.Text = $"读取失败：{Friendly(ex)}";
        }
    }

    private void OnApplyPerformance(object sender, RoutedEventArgs e) =>
        Run(ApplyPerformanceAsync, "性能下发失败");

    private async Task ApplyPerformanceAsync()
    {
        var settings = Settings;
        if (settings is null || !IsKeyboard)
        {
            Note(InfoBarSeverity.Error, "键盘通道未就绪", "先回设备页重新打开设备。");
            return;
        }

        try
        {
            int rate = Math.Clamp(RateBox.SelectedIndex + 1, 1, 3);
            int sleep = KeyboardSettings.SleepOptionsMinutes[
                Math.Clamp(SleepBox.SelectedIndex, 0, KeyboardSettings.SleepOptionsMinutes.Length - 1)];

            // 控件的值必须在 UI 线程上取，后台线程访问 XAML 控件会直接抛异常
            bool latencyOn = LatencySwitch.IsOn;
            var latency = settings.Model.Model == "Z75"
                ? Z75LatencyBox.SelectedIndex switch { 1 => LatencyMode.LowLatencyStandard, 2 => LatencyMode.Speed, _ => LatencyMode.Standard }
                : latencyOn ? LatencyMode.Speed : LatencyMode.Standard;
            bool winLockOn = WinLockSwitch.IsOn;
            bool macOn = MacModeSwitch.IsOn;

            await Task.Run(() =>
            {
                var current = settings.ReadPerformance();
                settings.WritePerformance(current with
                {
                    Rate = (PollingRate)rate,
                    SleepMinutes = sleep,
                    Latency = latency,
                    WinKeyLocked = winLockOn,
                    MacMode = macOn,
                });
            });

            await Task.Delay(120);
            if (_unloaded || !ReferenceEquals(settings, Settings)) return;
            await ReloadKeyboardSettingsAsync();
            Note(InfoBarSeverity.Success, "已下发并回读确认", $"{RateBox.SelectedItem}，休眠 {SleepBox.SelectedItem}");
        }
        catch (Exception ex)
        {
            AppState.WriteLog($"性能下发失败：{ex.GetType().Name} {ex.Message}");
            Note(InfoBarSeverity.Error, "下发失败", ex.Message);
        }
    }

    /// <summary>按键列表用的一行。</summary>
    public sealed class KeyRow
    {
        public required string Key { get; init; }
        public required string Layer { get; init; }
        public required string Value { get; init; }
        public required bool Changed { get; init; }
    }

    private void ReadInto(TextBox target, string commandName)
    {
        var keyboard = AppState.Keyboard;
        var command = KeyboardProtocol.FindReadCommand(commandName);
        if (keyboard is null || command is null)
        {
            target.Text = "键盘通道未就绪。";
            return;
        }

        // HID 读是阻塞调用，放 UI 线程上会让整个窗口卡住
        Run(() => ReadIntoAsync(target, commandName, command), "读取失败");
    }

    private async Task ReadIntoAsync(TextBox target, string commandName, KeyboardProtocol.ReadCommand command)
    {
        try
        {
            var response = await Task.Run(() => AppState.Keyboard?.Read(commandName) ?? []);
            target.Text = $"{DateTime.Now:HH:mm:ss}  {command.DisplayName}\r\n" +
                          KeyboardProtocol.Describe(response);
        }
        catch (Exception ex)
        {
            target.Text = $"读取失败：{Friendly(ex)}";
        }
    }

    // ---------------- 诊断 ----------------

    private void BuildDiagnostics()
    {
        CommandPanel.Children.Clear();
        CommandPanel.RowDefinitions.Clear();
        CommandPanel.ColumnDefinitions.Clear();
        for (int i = 0; i < 3; i++)
        {
            CommandPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        if (IsKeyboard)
        {
            for (int i = 0; i < KeyboardProtocol.ReadCommands.Count; i++)
            {
                var command = KeyboardProtocol.ReadCommands[i];
                var button = new Button
                {
                    Content = command.DisplayName,
                    Tag = command.Name,
                    MinWidth = 140,
                };
                button.Click += OnReadCommand;

                int row = i / 3;
                while (CommandPanel.RowDefinitions.Count <= row)
                {
                    CommandPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                }
                Grid.SetRow(button, row);
                Grid.SetColumn(button, i % 3);
                CommandPanel.Children.Add(button);
            }
        }
        else
        {
            var button = new Button { Content = "读取设备状态", MinWidth = 140 };
            button.Click += OnReadAudioState;
            Grid.SetRow(button, 0);
            Grid.SetColumn(button, 0);
            CommandPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            CommandPanel.Children.Add(button);
        }

        UiGuard.ApplyTo(CommandPanel);
    }

    private void OnReadCommand(object sender, RoutedEventArgs e) =>
        Run(() => ReadCommandAsync((Button)sender), "读取失败");

    private async Task ReadCommandAsync(Button button)
    {
        var keyboard = AppState.Keyboard;
        var name = (string)button.Tag;
        var command = KeyboardProtocol.FindReadCommand(name);
        if (keyboard is null || command is null)
        {
            CommandResult.Text = "键盘通道未就绪。";
            return;
        }

        try
        {
            var response = await Task.Run(() => keyboard.Read(name));
            CommandResult.Text = $"{DateTime.Now:HH:mm:ss}  {command.DisplayName}\r\n" +
                                 KeyboardProtocol.Describe(response) + "\r\n\r\n" + CommandResult.Text;
        }
        catch (Exception ex)
        {
            CommandResult.Text = $"{command.DisplayName} 失败：{Friendly(ex)}\r\n" + CommandResult.Text;
        }
    }

    private void OnReadAudioState(object sender, RoutedEventArgs e) =>
        Run(ReadAudioStateAsync, "读取设备状态失败");

    private async Task ReadAudioStateAsync()
    {
        var connection = AppState.Connection;
        if (connection is null)
        {
            CommandResult.Text = "音频通道未就绪。";
            return;
        }

        var generation = await connection.ProbeAsync();
        var state = await connection.GetStateAsync();
        CommandResult.Text = $"{DateTime.Now:HH:mm:ss}  协议代次：{generation}\r\n" +
                             (state is null
                                 ? "设备未在超时内回包，完整报文见「报文」页。"
                                 : $"灯效模式 {state.Light.ControlModeText}，亮度 {state.Light.Brightness}/{state.Light.BrightnessMax}，周期 {state.Light.Cycle}") +
                             "\r\n\r\n" + CommandResult.Text;
    }

    private void OnSendRaw(object sender, RoutedEventArgs e) =>
        Run(SendRawAsync, "手工发送失败");

    private async Task SendRawAsync()
    {
        byte[] bytes;
        try
        {
            bytes = RawBox.Text
                .Split([' ', ',', '\t', '\r', '\n', '-'], StringSplitOptions.RemoveEmptyEntries)
                .Select(t => Convert.ToByte(t.Replace("0x", ""), 16))
                .ToArray();
        }
        catch (Exception ex)
        {
            CommandResult.Text = $"解析失败：{ex.Message}\r\n" + CommandResult.Text;
            return;
        }

        try
        {
            if (IsKeyboard)
            {
                var keyboard = AppState.Keyboard;
                if (keyboard is null)
                {
                    CommandResult.Text = "键盘通道未就绪。\r\n" + CommandResult.Text;
                    return;
                }
                // 写命令要等设备回包确认，同样是阻塞调用
                var response = await Task.Run(() => keyboard.SendRaw(bytes));
                CommandResult.Text = $"{DateTime.Now:HH:mm:ss}  手工发送\r\n{KeyboardProtocol.Describe(response)}\r\n\r\n" + CommandResult.Text;
            }
            else
            {
                var connection = AppState.Connection;
                if (connection is null)
                {
                    CommandResult.Text = "音频通道未就绪。\r\n" + CommandResult.Text;
                    return;
                }
                await Task.Run(() => connection.Send(MchoseProtocol.ReportId, bytes));
                CommandResult.Text = $"{DateTime.Now:HH:mm:ss}  已发送到 output report 0x{MchoseProtocol.ReportId:X2}\r\n\r\n" + CommandResult.Text;
            }
        }
        catch (Exception ex)
        {
            CommandResult.Text = $"发送失败：{Friendly(ex)}\r\n" + CommandResult.Text;
        }
    }

    private void OnOpenConsole(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(ConsolePage), null, new DrillInNavigationTransitionInfo());

    // ---------------- 关于 ----------------

    private void BuildAbout()
    {
        AboutPanel.Children.Clear();

        void AddRow(string label, string value)
        {
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var key = new TextBlock
            {
                Text = label,
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            var val = new TextBlock
            {
                Text = value,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(val, 1);
            grid.Children.Add(key);
            grid.Children.Add(val);
            AboutPanel.Children.Add(grid);
        }

        if (_group is null)
        {
            if (_preview) AddRow("离线体验", "示例 K87；修改不会发送至任何物理设备。离开页面后预览改动清除。");
            return;
        }

        AddRow("设备", _group.Name);
        AddRow("VID / PID", $"{_group.VendorId:X4}:{_group.ProductId:X4}");
        AddRow("通道类型", _group.KindText);
        AddRow("型号收录", _group.Definition is null ? "未收录（按通用通道通信）" : "已收录");
        AddRow("HID 集合数", $"{_group.Collections.Count} 个");

        if (IsKeyboard)
        {
            AddRow("report id", AppState.Keyboard?.IsReportIdResolved == true
                ? $"0x{AppState.Keyboard.ReportId:X2}"
                : "未确定");
            AddRow("协议", AppState.Keyboard?.IsWireless == true ? "2.4G：0x13 output/input，14 字节分包" : "有线：feature report，519 字节载荷");
            AddRow("配置型号", Settings?.Model.Model ?? "等待识别");
        }
        else
        {
            AddRow("协议", "output report，report id 0xBC，15 字节");
            AddRow("音频运行时", _group.Definition?.AudioRuntime switch
            {
                AudioRuntime.CMedia => "C-Media（需要官方驱动 + APO）",
                AudioRuntime.Thx => "THX（需要 THX 的 APO）",
                _ => "未知",
            });
            if (_group.Definition?.HardwareId is { } hwid)
            {
                AddRow("硬件 ID", hwid);
            }
        }
    }

    private void OnCloseDevice(object sender, RoutedEventArgs e)
    {
        if (!_preview) { AppState.CloseActiveDevice(); MainWindow.SetStatus("未连接"); }
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private void OnOpenSoundSettings(object sender, RoutedEventArgs e) => Launch("ms-settings:sound");

    private void OnOpenOfficialSite(object sender, RoutedEventArgs e) => Launch("https://www.mchose.com.cn/");

    private static void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppState.WriteLog($"打开 {target} 失败：{ex.Message}");
        }
    }
}
