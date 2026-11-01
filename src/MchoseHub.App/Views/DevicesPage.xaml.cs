using MchoseHub.Core.Models;
using MchoseHub.Core.Protocol;
using MchoseHub.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace MchoseHub.App.Views;

public sealed class DeviceRow
{
    public required string DisplayName { get; init; }
    public required string SubText { get; init; }
    public required string Badge { get; init; }
    public required DeviceGroup Group { get; init; }
}

public sealed partial class DevicesPage : Page
{
    private readonly List<DeviceGroup> _groups = [];
    private bool _opening;
    private int _scanRevision;

    public DevicesPage()
    {
        InitializeComponent();
        UiGuard.Install(this);
        Loaded += (_, _) => _ = ScanAsync();
    }

    /// <summary>
    /// 枚举要走一遍 SetupAPI，每台设备还要开句柄读产品名和能力，
    /// 某些集合上的这几个 IOCTL 会阻塞几百毫秒——放 UI 线程上就是那种「卡一下」。
    /// 统一扔线程池。
    /// </summary>
    private async Task ScanAsync()
    {
        int revision = ++_scanRevision;
        try
        {
            ScanButton.IsEnabled = false;
            ShowAllSwitch.IsEnabled = false;

            bool includeAll = ShowAllSwitch.IsOn;
            var groups = await Task.Run(() => AppState.Service.ScanGroups(includeAll));

            if (!IsLoaded || revision != _scanRevision)
            {
                return;
            }

            _groups.Clear();
            _groups.AddRange(groups);
            ApplyGroups();
        }
        catch (Exception ex)
        {
            if (revision != _scanRevision)
            {
                return;
            }

            AppState.WriteLog($"扫描失败：{ex}");
            Fail("扫描失败", ex.Message);
        }
        finally
        {
            if (revision == _scanRevision)
            {
                ScanButton.IsEnabled = true;
                ShowAllSwitch.IsEnabled = true;
            }
        }
    }

    private void ApplyGroups()
    {
        DeviceList.ItemsSource = _groups
            .Select(g => new DeviceRow
            {
                DisplayName = g.Name,
                SubText = $"{g.VendorId:X4}:{g.ProductId:X4}  ·  {g.Collections.Count} 个 HID 集合  ·  {g.KindText}" +
                          (g.Definition is null ? "  ·  型号未收录" : string.Empty),
                Badge = g.KindText,
                Group = g,
            })
            .ToList();

        if (_groups.Count == 0)
        {
            Notice.Severity = InfoBarSeverity.Informational;
            Notice.Title = "未发现迈从设备";
            Notice.Message = "插上设备后重新扫描。若已插入，确认它没有被其他程序独占，或打开右上角的「全部 HID」看看。";
            Notice.IsOpen = true;
        }
        else
        {
            Notice.IsOpen = false;
            DeviceList.SelectedIndex = 0;
        }

        UpdateButtons();
    }

    private DeviceRow? Selected =>
        DeviceList.SelectedIndex >= 0 && DeviceList.SelectedIndex < DeviceList.Items.Count
            ? DeviceList.Items[DeviceList.SelectedIndex] as DeviceRow
            : null;

    private void UpdateButtons()
    {
        OpenButton.IsEnabled = Selected?.Group.Kind is ChannelKind.Keyboard or ChannelKind.Audio;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void OnScan(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private void OnPreviewKeyboard(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(DeviceDetailPage), "preview", new DrillInNavigationTransitionInfo());

    private void OnShowAllToggled(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            // ScanAsync 自己把异常收成界面提示，事件处理器里不用再兜一层
            _ = ScanAsync();
        }
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        try
        {
            OpenSelected();
        }
        catch (Exception ex)
        {
            MainWindow.SetStatus("未连接");
            AppState.WriteLog($"打开设备失败：{ex}");
            Fail("打开失败", ex.Message);
        }
    }

    /// <summary>出错走右下角提示条，别占着列表上面的位置一直不走。</summary>
    private void Fail(string title, string message)
    {
        Toast.Error(title, message);
        Notice.Severity = InfoBarSeverity.Error;
        Notice.Title = title;
        Notice.Message = message;
        Notice.IsOpen = true;
    }

    private void OpenSelected()
    {
        var row = Selected;
        if (row is null || _opening)
        {
            return;
        }

        if (row.Group.Kind is not (ChannelKind.Keyboard or ChannelKind.Audio))
        {
            Fail("暂不支持这个 HID 设备", "“全部 HID”用于排查未收录型号；只有识别为键盘或音频设备的条目可以打开。");
            return;
        }

        // 连点两下会把上一个会话在别人还在用它的时候释放掉，句柄被提前关掉就会崩
        _opening = true;
        OpenButton.IsEnabled = false;
        try
        {
            AppState.CloseActiveDevice();
            AppState.ActiveGroup = row.Group;
            MainWindow.SetStatus($"正在连接 · {row.Group.Name}");

            switch (row.Group.Kind)
            {
                case ChannelKind.Keyboard:
                    var keyboard = AppState.Service.ConnectKeyboard(row.Group.Channel);
                    AppState.Keyboard = keyboard;
                    // 探测 report id + 建配置对象。失败也不会留下半成品，
                    // 详情页会显示原因并给一个「重新探测」按钮。
                    _ = StartKeyboardAsync(row.Group, keyboard);
                    break;
                case ChannelKind.Audio:
                    AppState.Connection = AppState.Service.Connect(row.Group.Channel);
                    MainWindow.SetStatus($"已连接 · {row.Group.Name}");
                    break;
            }

            Frame.Navigate(typeof(DeviceDetailPage), null, new DrillInNavigationTransitionInfo());
        }
        finally
        {
            _opening = false;
            UpdateButtons();
        }
    }

    /// <summary>
    /// 打开设备之后的握手。只有握手成功才开看门狗——通道没就绪时
    /// （比如官方客户端还占着设备）看门狗会一直判「掉线」并疯狂重连，
    /// 把「先关掉官方客户端」这个前提条件冲掉。
    /// </summary>
    private async Task StartKeyboardAsync(DeviceGroup group, KeyboardSession keyboard)
    {
        try
        {
            bool ready = await AppState.PrepareKeyboardAsync(group);
            if (!ReferenceEquals(AppState.Keyboard, keyboard))
            {
                return;
            }

            if (ready)
            {
                AppState.StartHealthMonitor();
                MainWindow.SetStatus($"已连接 · {group.Name}");
                Toast.Success("已连接", $"{group.Name} · 配置通道就绪");
            }
            else
            {
                MainWindow.SetStatus($"未连接 · {group.Name}");
                Toast.Warning("配置通道未就绪", "多半是官方 MCHOSE HUB 还占着设备，详情页顶部有「重新探测」。");
            }
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(AppState.Keyboard, keyboard))
            {
                return;
            }

            MainWindow.SetStatus($"未连接 · {group.Name}");
            AppState.WriteLog($"键盘握手失败：{ex}");
            Toast.Error("键盘握手失败", ex.Message);
        }
    }

}
