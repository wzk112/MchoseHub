using MchoseHub.App.Views;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;

namespace MchoseHub.App;

public sealed partial class MainWindow : Window
{
    private bool _clampingWindowSize;

    public MainWindow()
    {
        InitializeComponent();

        Title = "MCHOSE HUB";
        SizeForDisplay();
        AppWindow.Changed += OnAppWindowChanged;
        EnforceMinimumWindowSize(AppWindow);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        RootGrid.ActualThemeChanged += (_, _) => UpdateTitleBarColors();
        RootGrid.Loaded += (_, _) => UpdateTitleBarColors();
        UpdateTitleBarColors();

        // Windows 11 的 Mica 材质；不支持时自动退回纯色
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
        }

        Nav.SelectedItem = Nav.MenuItems[0];
        ContentFrame.Navigated += (_, _) => Nav.IsBackEnabled = ContentFrame.CanGoBack;
        Toast.Attach(ToastHost);
        AppState.WriteLog("应用启动，等待设备");
        Closed += (_, _) =>
        {
            AppWindow.Changed -= OnAppWindowChanged;
            AppState.CloseActiveDevice();
        };
    }

    public static void SetStatus(string text)
    {
        if (App.MainWindow is not MainWindow window)
        {
            return;
        }

        window.StatusText.Text = text;

        // 连上了点亮、重连中给个提示色、未连接保持灰的——颜色本身也是一条信息
        bool connected = text.StartsWith("已连接", StringComparison.Ordinal);
        bool busy = text.Contains("重连", StringComparison.Ordinal);

        window.StatusIcon.Fill = BrushOf(
            connected ? "SystemFillColorSuccessBrush"
            : busy ? "SystemFillColorCautionBrush"
            : "TextFillColorSecondaryBrush");
    }

    private void UpdateTitleBarColors()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;

        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

        // Keep Windows' contrast colors when a contrast theme is enabled.
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            titleBar.ForegroundColor = null;
            titleBar.InactiveForegroundColor = null;
            titleBar.ButtonForegroundColor = null;
            titleBar.ButtonInactiveForegroundColor = null;
            titleBar.ButtonHoverForegroundColor = null;
            titleBar.ButtonPressedForegroundColor = null;
            titleBar.ButtonHoverBackgroundColor = null;
            titleBar.ButtonPressedBackgroundColor = null;
            return;
        }

        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var foreground = dark
            ? Windows.UI.Color.FromArgb(255, 245, 247, 250)
            : Windows.UI.Color.FromArgb(255, 31, 41, 55);
        var inactive = dark
            ? Windows.UI.Color.FromArgb(255, 154, 165, 180)
            : Windows.UI.Color.FromArgb(255, 98, 109, 124);
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveForegroundColor = inactive;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactive;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = dark
            ? Windows.UI.Color.FromArgb(40, 255, 255, 255)
            : Windows.UI.Color.FromArgb(18, 0, 0, 0);
        titleBar.ButtonPressedBackgroundColor = dark
            ? Windows.UI.Color.FromArgb(60, 255, 255, 255)
            : Windows.UI.Color.FromArgb(30, 0, 0, 0);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void SizeForDisplay()
    {
        double scale = Math.Max(1, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Min((int)(1280 * scale), area.Width - (int)(32 * scale));
        int height = Math.Min((int)(860 * scale), area.Height - (int)(32 * scale));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height));
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange || args.DidPositionChange || args.DidPresenterChange)
        {
            EnforceMinimumWindowSize(sender);
        }
    }

    private void EnforceMinimumWindowSize(AppWindow window)
    {
        if (_clampingWindowSize || window.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            return;
        }

        var area = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Primary).WorkArea;
        if (area.Width <= 0 || area.Height <= 0) return;

        double scale = Math.Max(1, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0);
        int minimumWidth = Math.Min((int)Math.Ceiling(960 * scale), area.Width);
        int minimumHeight = Math.Min((int)Math.Ceiling(640 * scale), area.Height);
        var size = window.Size;
        if (size.Width >= minimumWidth && size.Height >= minimumHeight) return;

        // AppWindow uses physical pixels. Cap the enforced minimum to this
        // monitor's work area so high-DPI or small displays remain usable.
        int width = Math.Min(Math.Max(size.Width, minimumWidth), area.Width);
        int height = Math.Min(Math.Max(size.Height, minimumHeight), area.Height);
        int x = Math.Clamp(window.Position.X, area.X, area.X + area.Width - width);
        int y = Math.Clamp(window.Position.Y, area.Y, area.Y + area.Height - height);

        _clampingWindowSize = true;
        try
        {
            window.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
        }
        finally
        {
            _clampingWindowSize = false;
        }
    }

    /// <summary>取主题资源，取不到就回退到次要文字色，绝不因为一个颜色崩掉。</summary>
    private static Microsoft.UI.Xaml.Media.Brush BrushOf(string key)
    {
        try
        {
            if (Application.Current?.Resources?[key] is Microsoft.UI.Xaml.Media.Brush brush)
            {
                return brush;
            }
        }
        catch
        {
            // 落到下面的兜底
        }

        return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x80, 0x80, 0x80));
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        => NavigateToTopLevel(args.SelectedItem as NavigationViewItem);

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        => NavigateToTopLevel(args.InvokedItemContainer as NavigationViewItem);

    private void NavigateToTopLevel(NavigationViewItem? item)
    {
        if (item?.Tag is not string tag)
        {
            return;
        }

        var pageType = tag switch
        {
            "devices" => typeof(DevicesPage),
            "console" => typeof(ConsolePage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DevicesPage),
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            // 钻入式过渡：进入子页面时内容从右侧滑入，返回时反向
            ContentFrame.Navigate(pageType, null, new DrillInNavigationTransitionInfo());

            // 顶层导航之间不保留历史：不然「设备 → 详情 → 设备 → 详情」来回点，
            // 每个页面实例（详情页里那 87 个键格 + 一整棵视觉树）都会一直挂在
            // 返回栈上回收不掉，开久了内存只涨不降。
            ContentFrame.BackStack.Clear();
            Nav.IsBackEnabled = false;
        }
    }

    private void OnNavBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }
}
