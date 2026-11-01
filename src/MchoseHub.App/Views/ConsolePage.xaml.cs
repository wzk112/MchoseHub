using System.Collections.ObjectModel;
using System.Collections.Specialized;
using MchoseHub.Core.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MchoseHub.App.Views;

public sealed partial class ConsolePage : Page
{
    public ObservableCollection<string> Logs => AppState.Log;

    public ConsolePage()
    {
        InitializeComponent();
        UiGuard.Install(this);
        AppState.Log.CollectionChanged += OnLogChanged;
        // 每次导航都会新建一个页面实例，不摘钩子的话旧的页面会被日志一直引用着
        Unloaded += (_, _) => AppState.Log.CollectionChanged -= OnLogChanged;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Logs.Count > 0)
        {
            LogList.ScrollIntoView(Logs[^1]);
        }
    }

    private void OnClear(object sender, RoutedEventArgs e) => Logs.Clear();

    private void OnSend(object sender, RoutedEventArgs e)
    {
        var connection = AppState.Connection;
        if (connection is null)
        {
            AppState.WriteLog("发送失败：尚未连接设备");
            return;
        }

        try
        {
            byte reportId = Convert.ToByte(ReportIdBox.Text.Trim().Replace("0x", ""), 16);
            var payload = ParseHex(PayloadBox.Text);
            // 写 output report 在设备没准备好时会阻塞到超时，不能占着 UI 线程
            AppState.Run(() => connection.Send(reportId, payload), "发送 output report");
        }
        catch (Exception ex)
        {
            AppState.WriteLog($"发送失败：{ex.Message}");
        }
    }

    private void OnSendBaseState(object sender, RoutedEventArgs e)
    {
        var connection = AppState.Connection;
        if (connection is null)
        {
            AppState.WriteLog("发送失败：尚未连接设备");
            return;
        }
        AppState.Run(
            () => connection.Send(MchoseProtocol.ReportId, MchoseCommands.GetBaseState()),
            "发送结构化帧");
    }

    private void OnSendLegacyState(object sender, RoutedEventArgs e)
    {
        var connection = AppState.Connection;
        if (connection is null)
        {
            AppState.WriteLog("发送失败：尚未连接设备");
            return;
        }
        AppState.Run(
            () => connection.Send(MchoseProtocol.ReportId, MchoseCommands.LegacyGetState()),
            "发送第一代帧");
    }

    private static byte[] ParseHex(string text)
    {
        var tokens = text.Split([' ', ',', '\t', '\r', '\n', '-'], StringSplitOptions.RemoveEmptyEntries);
        var bytes = new List<byte>(tokens.Length);
        foreach (var token in tokens)
        {
            bytes.Add(Convert.ToByte(token.Replace("0x", ""), 16));
        }
        return bytes.ToArray();
    }
}
