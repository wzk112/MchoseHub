using MchoseHub.Core.Models;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MchoseHub.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        UiGuard.Install(this);
        ThemeSelector.SelectedIndex = AppState.ThemePreference;
        AutoReconnectSwitch.IsOn = AppState.AutoReconnectEnabled;
        CatalogText.Text =
            $"已收录 {DeviceCatalog.All.Count} 款设备，" +
            $"覆盖 {DeviceCatalog.VendorIds.Count()} 个厂商 ID：" +
            string.Join("、", DeviceCatalog.VendorIds.OrderBy(v => v).Select(v => $"0x{v:X4}"));
        LogPathText.Text = App.CrashLogPath;
    }

    private void OnAutoReconnectToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || !IsLoaded)
        {
            // 初始化期间 Toggled 会先于页面加载触发一次，忽略即可：
            // 设备打开时本来就会自己开看门狗。
            return;
        }

        AppState.AutoReconnectEnabled = toggle.IsOn;
        if (!toggle.IsOn)
        {
            AppState.StopHealthMonitor();
            Toast.Info("自动重连已关闭", "设备掉线后不会再自动重连。");
            return;
        }

        if (AppState.KeyboardSettings is not null)
        {
            AppState.StartHealthMonitor();
        }

        Toast.Success("自动重连已开启");
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e) =>
        Launch(Path.GetDirectoryName(App.CrashLogPath)!);

    private void OnOpenCrashLog(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(App.CrashLogPath))
        {
            Toast.Info("还没有崩溃日志", "说明目前没有记录到未处理异常。");
            return;
        }

        Launch(App.CrashLogPath);
    }

    private void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppState.WriteLog($"打开 {target} 失败：{ex.Message}");
            Toast.Error("打不开", ex.Message);
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (App.MainWindow?.Content is not FrameworkElement root)
        {
            return;
        }

        root.RequestedTheme = ThemeSelector.SelectedIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        AppState.ThemePreference = ThemeSelector.SelectedIndex;
    }
}
