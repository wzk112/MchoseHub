using System.Collections.ObjectModel;
using MchoseHub.Core.Protocol;
using MchoseHub.Core.Services;
using MchoseHub.Platform.Windows;
using Microsoft.UI.Dispatching;

namespace MchoseHub.App;

/// <summary>应用级共享状态：设备服务、当前连接、报文日志。</summary>
public static class AppState
{
    private static DispatcherQueue? _dispatcher;
    private static MchoseDeviceService? _service;

    public static ObservableCollection<string> Log { get; } = [];

    public static MchoseDeviceService Service =>
        _service ??= new MchoseDeviceService(new WindowsHidTransport(), WriteLog);

    public static MchoseConnection? Connection { get; set; }

    /// <summary>键盘通道（feature report）。与音箱/耳机那条 output report 通道互不影响。</summary>
    public static KeyboardSession? Keyboard { get; set; }

    /// <summary>键盘配置读写（灯效 / 性能 / 按键）。只有键盘通道连上后才有值。</summary>
    public static KeyboardSettings? KeyboardSettings { get; set; }

    /// <summary>当前正在配置的设备。</summary>
    public static DeviceGroup? ActiveGroup { get; set; }

    /// <summary>
    /// 自动重连成功之后触发。页面订阅它去把界面上的设置重新读一遍
    /// （重连会换掉 KeyboardSettings 对象，旧引用已经作废）。
    /// </summary>
    public static event Action<DeviceGroup?>? Reconnected;

    /// <summary>正在自动重连时为 true，页面可以据此禁用「下发」类按钮。</summary>
    public static bool IsReconnecting { get; private set; }

    /// <summary>用户可以在设置页关掉自动重连。关掉之后看门狗不跑，掉线就只提示不重连。</summary>
    private static bool _autoReconnectEnabled = true;
    private static int _deviceRevision;
    public static bool AutoReconnectEnabled
    {
        get => _autoReconnectEnabled;
        set { _autoReconnectEnabled = value; if (!value) Interlocked.Increment(ref _deviceRevision); }
    }

    /// <summary>当前会话的主题选择，确保离开设置页再回来时界面不会显示成错误选项。</summary>
    public static int ThemePreference { get; set; }

    public static void CloseActiveDevice(bool cancelReconnect = true)
    {
        if (cancelReconnect) Interlocked.Increment(ref _deviceRevision);
        StopHealthMonitor();
        Connection?.Dispose();
        Connection = null;
        Keyboard?.Dispose();
        Keyboard = null;
        KeyboardSettings = null;
        ActiveGroup = null;
    }

    // ------------------------------------------------------------------
    // 掉线看门狗 + 自动重连
    //
    // 放在应用级而不是页面级：以前只有「设备详情页」开着的时候才有人看着通道，
    // 用户切到报文页/设置页，键盘掉了也无人察觉，回来一看全是失败。
    // 现在只要开着设备就在后台盯着，和当前在哪个页面无关。
    //
    // 探测用 getBattery（0x87）：它是纯读命令，不碰键盘的配置存储，
    // 所以可以放心高频轮询，不会像写灯效那样把固件写卡。
    // ------------------------------------------------------------------

    private static DispatcherQueueTimer? _healthTimer;
    private static int _healthFailures;
    private static int _healthBusy;

    /// <summary>看门狗间隔。6 秒一次，代价只有一条 feature 读。</summary>
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(6);

    public static void StartHealthMonitor()
    {
        if (_dispatcher is null)
        {
            return;
        }

        _healthTimer ??= _dispatcher.CreateTimer();
        _healthTimer.Interval = HealthInterval;
        _healthTimer.IsRepeating = true;
        _healthTimer.Tick -= OnHealthTick;
        _healthTimer.Tick += OnHealthTick;
        _healthTimer.Start();
        _healthFailures = 0;
    }

    public static void StopHealthMonitor()
    {
        _healthTimer?.Stop();
        _healthFailures = 0;
    }

    private static void OnHealthTick(object? sender, object e)
    {
        if (IsReconnecting || !AutoReconnectEnabled)
        {
            return;
        }

        var keyboard = Keyboard;
        var group = ActiveGroup;
        if (keyboard is null || group is null)
        {
            return;
        }

        if (!keyboard.IsOpen)
        {
            _ = ReconnectAsync("设备连接已经断开");
            return;
        }

        // 一次只放一个探测任务进去：上一轮还没回来（设备卡着）时不要往上叠
        if (Interlocked.CompareExchange(ref _healthBusy, 1, 0) != 0)
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                keyboard.Read("getBattery");
                if (ReferenceEquals(keyboard, Keyboard)) Interlocked.Exchange(ref _healthFailures, 0);
            }
            catch (Exception)
            {
                if (!ReferenceEquals(keyboard, Keyboard)) return;
                // 单次失败可能只是设备在忙（比如刚被写了一次灯效，固件还在落盘），
                // 连续三轮（约 18 秒）都没应答才判定掉线——宁可晚一点重连，
                // 也不要误判之后把正在写的会话拆掉。
                if (Interlocked.Increment(ref _healthFailures) >= 3)
                {
                    Interlocked.Exchange(ref _healthFailures, 0);
                    _dispatcher?.TryEnqueue(() => { if (ReferenceEquals(keyboard, Keyboard)) _ = ReconnectAsync("设备连续三次没有应答"); });
                }
            }
            finally
            {
                Interlocked.Exchange(ref _healthBusy, 0);
            }
        });
    }

    /// <summary>
    /// 自动重连：退避重扫 → 找同一台设备 → 重开配置通道 → 重建配置对象。
    /// 全程只发提示条，不弹对话框——掉线经常发生在用户正拖滑杆的时候，
    /// 弹个模态框会把手里的操作直接打断。
    /// </summary>
    public static async Task<bool> ReconnectAsync(string reason)
    {
        if (IsReconnecting || !AutoReconnectEnabled)
        {
            return false;
        }

        var group = ActiveGroup;
        if (group is null)
        {
            return false;
        }

        IsReconnecting = true;
        int revision = _deviceRevision;
        try
        {
            Toast.Warning("设备断开了，正在自动重连", reason);
            MainWindow.SetStatus("正在重连…");
            WriteLog($"自动重连开始：{reason}");

            if (!await TryReconnectAsync(group))
            {
                if (revision != _deviceRevision) return false;
                MainWindow.SetStatus("未连接");
                Toast.Error("自动重连失败",
                    "确认键盘插好、官方 MCHOSE HUB 已退出，然后回设备页点「打开设备」。");
                return false;
            }

            if (revision != _deviceRevision) return false;
            MainWindow.SetStatus("已连接");
            WriteLog($"自动重连成功：{ActiveGroup?.Name}");
            Toast.Success("已自动重新连接", ActiveGroup?.Name);
            Reconnected?.Invoke(ActiveGroup);
            return true;
        }
        catch (Exception ex)
        {
            if (revision != _deviceRevision) return false;
            MainWindow.SetStatus("未连接");
            WriteLog($"自动重连异常：{ex}");
            Toast.Error("自动重连出错", ex.Message);
            return false;
        }
        finally
        {
            IsReconnecting = false;
        }
    }

    /// <summary>
    /// 设备掉线之后自动重连：重新扫描 → 找同一台设备 → 重开配置通道 → 重建配置对象。
    ///
    /// 键盘掉线的原因很杂：被系统挂起、USB 重新枚举、被官方客户端抢走句柄、
    /// 甚至固件自己卡住。以前只能让用户自己拔插再点「重新探测」，
    /// 现在后台自己退避重试几次，成功了页面会自动把设置重新读一遍。
    /// </summary>
    public static async Task<bool> TryReconnectAsync(DeviceGroup? group = null, int attempts = 5)
    {
        group ??= ActiveGroup;
        if (group is null)
        {
            WriteLog("自动重连：没有可参考的设备信息。");
            return false;
        }

        int revision = _deviceRevision;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (revision != _deviceRevision || !AutoReconnectEnabled) return false;
                CloseActiveDevice(cancelReconnect: false);

                // 退避：键盘刚被放开时固件有 1～3 秒恢复期，冲太急只会一直失败
                await Task.Delay(500 * attempt);
                if (revision != _deviceRevision || !AutoReconnectEnabled) return false;

                var groups = await Task.Run(() => Service.ScanGroups());
                if (revision != _deviceRevision || !AutoReconnectEnabled) return false;
                var found = groups
                    .FirstOrDefault(g => g.Kind == group.Kind
                                         && g.Id == group.Id);

                if (found is null)
                {
                    WriteLog($"自动重连第 {attempt} 次：还没扫到 {group.Name}。");
                    continue;
                }

                ActiveGroup = found;
                Keyboard = Service.ConnectKeyboard(found.Channel);
                if (await PrepareKeyboardAsync(found))
                {
                    if (revision != _deviceRevision || !AutoReconnectEnabled) return false;
                    WriteLog($"自动重连成功：{found.Name}");
                    // CloseActiveDevice 会把看门狗停掉，重连成功要重新挂上，
                    // 否则下一次掉线就没人看着了。
                    StartHealthMonitor();
                    return true;
                }

                WriteLog($"自动重连第 {attempt} 次：设备在，但配置通道没应答。");
            }
            catch (Exception ex)
            {
                WriteLog($"自动重连第 {attempt} 次失败：{ex.Message}");
            }
        }

        if (revision == _deviceRevision) CloseActiveDevice(cancelReconnect: false);
        return false;
    }

    /// <summary>
    /// 打开键盘通道之后的准备工作：先确定 report id，再按机型建配置读写对象。
    ///
    /// 关键点：探测失败时**不留下半成品**。以前这里不管探测结果都会把
    /// KeyboardSettings 建出来，于是界面上「读取设置」拿着一堆未确定的
    /// report id（0x00）去发 519 字节报文，设备一律回 Win32 87。
    /// </summary>
    public static async Task<bool> PrepareKeyboardAsync(DeviceGroup? group = null)
    {
        group ??= ActiveGroup;
        var keyboard = Keyboard;
        if (group is null || keyboard is null)
        {
            return false;
        }

        try
        {
            // 探测本身是阻塞收发的，ResolveReportIdAsync 内部会扔到线程池
            var reportId = await keyboard.ResolveReportIdAsync();
            if (reportId is null)
            {
                if (ReferenceEquals(Keyboard, keyboard))
                {
                    KeyboardSettings = null;
                }
                WriteLog("键盘配置通道没有应答，report id 未能确定；可以直接重试。");
                return false;
            }

            // 打开另一台设备时，上一台的握手任务可能刚好在这里返回。
            // 旧任务绝不能把新会话的 KeyboardSettings 覆盖掉。
            if (!ReferenceEquals(Keyboard, keyboard)
                || !string.Equals(ActiveGroup?.Id, group.Id, StringComparison.Ordinal))
            {
                WriteLog($"忽略已经过期的键盘握手结果：{group.Name}");
                return false;
            }

            var modelKey = keyboard.IsWireless
                ? await Task.Run(keyboard.ResolveWirelessModelKey)
                : KeyboardSettingsSession.ResolveModelKey(group.Name);
            if (!ReferenceEquals(Keyboard, keyboard) || ActiveGroup?.Id != group.Id) return false;
            var model = KeyboardModelSchemas.Get(modelKey);
            KeyboardSettings = new KeyboardSettings(new KeyboardSettingsSession(keyboard, model, WriteLog));
            if (keyboard.IsWireless) ActiveGroup = group with { Name = $"{group.Name} · {modelKey}" };
            WriteLog($"键盘机型识别为 {model.Model}，已加载 {model.KeyList.Count} 个按键定义" +
                     $"（report id 0x{reportId:X2}）");
            return true;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(Keyboard, keyboard))
            {
                KeyboardSettings = null;
            }
            WriteLog($"键盘初始化失败：{ex.Message}");
            return false;
        }
    }

    public static void Attach(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    /// <summary>
    /// 把会阻塞的 HID 收发挪到线程池上跑，并把异常收成日志。
    /// 直接 Task.Run 而不接住异常的话，会变成「未观察的任务异常」，只能靠兜底记录。
    /// </summary>
    public static void Run(Action action, string what)
    {
        _ = Task.Run(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                WriteLog($"{what}失败：{ex.Message}");
            }
        });
    }

    /// <summary>
    /// 异步版。用 Action 包 async lambda 会变成 async void，await 之后的异常
    /// 逃出 try 之外，直接落到未处理异常（表现为「用着用着崩一下」）。
    /// 这里把整个 Task 包进去 await，异常才真的收得住。
    /// </summary>
    public static void Run(Func<Task> action, string what)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                WriteLog($"{what}失败：{ex.Message}");
            }
        });
    }

    public static void WriteLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        AppendToFile(line);
        if (_dispatcher is null)
        {
            Append(line);
            return;
        }
        _dispatcher.TryEnqueue(() => Append(line));
    }

    /// <summary>
    /// 日志同时落盘。
    ///
    /// 之前界面上的日志只在内存里，程序一关就没了——设备出问题（灯不亮、掉线、
    /// 需要拔插才恢复）之后根本没法回看「到底写了什么」。现在每一行都写到
    /// session.log，超过 2 MB 就把上一份留成 session.log.1。
    ///
    /// 写文件这件事全部交给一条后台流水线：
    /// 一次 HID 写入会连着记十几行，而这些调用基本都发生在 UI 线程上，
    /// 每行都同步地开文件 → 写 → 关的话，界面会一顿一顿的（尤其是在报文页）。
    /// 现在 UI 线程只负责入队，真正的 IO 在后台线程上顺序做。
    /// </summary>
    private static readonly object _fileGate = new();
    private static readonly System.Collections.Concurrent.BlockingCollection<string> _fileQueue = new(4096);
    private static Task? _fileWriter;

    private static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "MchoseHub", "session.log");

    private static void AppendToFile(string line)
    {
        try
        {
            lock (_fileGate)
            {
                _fileWriter ??= Task.Factory.StartNew(
                    FileWriterLoop,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }

            _fileQueue.TryAdd(line);
        }
        catch
        {
            // 日志入队失败不能影响功能
        }
    }

    private static void FileWriterLoop()
    {
        foreach (var line in _fileQueue.GetConsumingEnumerable())
        {
            try
            {
                var path = LogFilePath;
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                var info = new FileInfo(path);
                if (info.Exists && info.Length > 2 * 1024 * 1024)
                {
                    var backup = path + ".1";
                    if (File.Exists(backup))
                    {
                        File.Delete(backup);
                    }

                    File.Move(path, backup);
                }

                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // 日志写不进去不能影响功能
            }
        }
    }

    private static void Append(string line)
    {
        Log.Add(line);
        while (Log.Count > 500)
        {
            Log.RemoveAt(0);
        }
    }
}
