using System.Text;
using Microsoft.UI.Xaml;

namespace MchoseHub.App;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    /// <summary>崩溃日志路径。任何未处理异常都会写到这里，方便回查。</summary>
    public static string CrashLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "MchoseHub", "crash.log");

    public App()
    {
        InitializeComponent();

        // UI 线程上的未处理异常默认会把进程直接干掉，这里先记下来
        UnhandledException += (_, e) =>
        {
            WriteCrash("UI 线程未处理异常", e.Exception);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrash("非 UI 线程未处理异常", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrash("后台任务未观察异常", e.Exception);
            e.SetObserved();
        };
    }

    private static void WriteCrash(string kind, Exception? ex)
    {
        try
        {
            var folder = Path.GetDirectoryName(CrashLogPath);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var text = new StringBuilder()
                .AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}")
                .AppendLine(ex?.ToString() ?? "(无异常对象)")
                .AppendLine(new string('-', 60))
                .ToString();
            File.AppendAllText(CrashLogPath, text);
        }
        catch
        {
            // 记日志本身失败就只能算了
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        AppState.Attach(MainWindow.DispatcherQueue);
        MainWindow.Activate();
    }
}
