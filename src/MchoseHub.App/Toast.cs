using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace MchoseHub.App;

/// <summary>
/// 应用内提示：右下角浮出来一张小卡片，几秒后自己淡出消失。
///
/// 和页面里的 InfoBar 分工：
///  - InfoBar 放**常驻状态**（例如「配置通道未就绪」还带一个「重新探测」按钮），
///    它得一直待在那儿等人处理；
///  - Toast 放**刚发生的事**（下发成功、写入失败、自动重连…），看完就该消失，
///    不然面板上堆一屏提示条没法用。
/// </summary>
internal static class Toast
{
    private const int MaxVisible = 4;

    private static Panel? _host;
    private static DispatcherQueue? _dispatcher;

    public static void Attach(Panel host)
    {
        _host = host;
        _dispatcher = host.DispatcherQueue;
    }

    public static void Info(string title, string? message = null) =>
        Show(InfoBarSeverity.Informational, title, message);

    public static void Success(string title, string? message = null) =>
        Show(InfoBarSeverity.Success, title, message);

    public static void Warning(string title, string? message = null) =>
        Show(InfoBarSeverity.Warning, title, message);

    public static void Error(string title, string? message = null) =>
        Show(InfoBarSeverity.Error, title, message, milliseconds: 8000);

    public static void Show(InfoBarSeverity severity, string title, string? message = null, int milliseconds = 4500)
    {
        var host = _host;
        if (host is null)
        {
            return;
        }

        var dispatcher = _dispatcher ?? host.DispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            Post(host, severity, title, message, milliseconds);
        }
        else
        {
            dispatcher.TryEnqueue(() => Post(host, severity, title, message, milliseconds));
        }
    }

    private static void Post(Panel host, InfoBarSeverity severity, string title, string? message, int milliseconds)
    {
        try
        {
            // 同时挂太多就没意义了，先挤掉最老的那张
            while (host.Children.Count >= MaxVisible)
            {
                host.Children.RemoveAt(0);
            }

            var card = BuildCard(severity, title, message);
            host.Children.Add(card);

            // 入场：从下方轻轻浮上来 + 淡入。
            // 用 RenderTransform 上的 TranslateTransform 来做，WinUI 的 Storyboard
            // 对它是标准支持；直接动 Translation（Vector3）在旧版本上不一定有投影器。
            var slide = new TranslateTransform { Y = 14 };
            card.RenderTransform = slide;

            var enter = new Storyboard();
            var fadeIn = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(180)) };
            Storyboard.SetTarget(fadeIn, card);
            Storyboard.SetTargetProperty(fadeIn, "Opacity");
            enter.Children.Add(fadeIn);

            var rise = new DoubleAnimation
            {
                From = 14,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(rise, slide);
            Storyboard.SetTargetProperty(rise, "Y");
            enter.Children.Add(rise);

            enter.Begin();

            var timer = host.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Dismiss(host, card);
            };
            timer.Start();

            // 点一下也能马上收掉
            card.Tapped += (_, _) => Dismiss(host, card);
        }
        catch (Exception ex)
        {
            AppState.WriteLog($"提示条显示失败：{ex.Message}");
        }
    }

    private static void Dismiss(Panel host, UIElement card)
    {
        if (!host.Children.Contains(card))
        {
            return;
        }

        var fade = new Storyboard();
        var fadeOut = new DoubleAnimation { From = 1, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(220)) };
        Storyboard.SetTarget(fadeOut, card);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        fade.Children.Add(fadeOut);

        if (card.RenderTransform is TranslateTransform slide)
        {
            var sink = new DoubleAnimation
            {
                From = slide.Y,
                To = 8,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            Storyboard.SetTarget(sink, slide);
            Storyboard.SetTargetProperty(sink, "Y");
            fade.Children.Add(sink);
        }

        fade.Completed += (_, _) => host.Children.Remove(card);
        fade.Begin();
    }

    private static Border BuildCard(InfoBarSeverity severity, string title, string? message)
    {
        // 每种严重程度：图标 + 主色 + 底色回退。
        // 主题资源在个别 Windows App SDK 版本里可能被改名/裁掉，索引器取不到会直接
        // 抛 KeyNotFoundException；提示条本身是「有错要报」的功能，不能自己先炸掉，
        // 所以一律走带兜底的取色。
        var (iconName, accentKey, accentFallback, background, foreground) = severity switch
        {
            InfoBarSeverity.Success => (
                "success", "SystemFillColorSuccessBrush", Color.FromArgb(0xFF, 0x0F, 0x7B, 0x0F),
                "SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush"),
            InfoBarSeverity.Warning => (
                "warning", "SystemFillColorCautionBrush", Color.FromArgb(0xFF, 0x9D, 0x5D, 0x00),
                "SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush"),
            InfoBarSeverity.Error => (
                "error", "SystemFillColorCriticalBrush", Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C),
                "SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush"),
            _ => (
                "info", "SystemFillColorNeutralBrush", Color.FromArgb(0xFF, 0x60, 0x60, 0x60),
                "SystemFillColorNeutralBackgroundBrush", "SystemFillColorNeutralBrush"),
        };

        var iconBrush = Res(accentKey, new SolidColorBrush(accentFallback));
        var backgroundBrush = Res(background, null) ?? Res("CardBackgroundFillColorDefaultBrush", null)
            ?? new SolidColorBrush(Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B));
        var borderBrush = Res("CardStrokeColorDefaultBrush", null) ?? iconBrush;

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            Style = Res<Style>("BodyStrongTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap,
        });

        if (!string.IsNullOrWhiteSpace(message))
        {
            text.Children.Add(new TextBlock
            {
                Text = message,
                Style = Res<Style>("CaptionTextBlockStyle"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Res("TextFillColorSecondaryBrush", null),
            });
        }

        var content = new Grid { ColumnSpacing = 10 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new Image
        {
            Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri($"ms-appx:///Assets/Icons/{iconName}.svg")),
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(icon, 0);
        content.Children.Add(icon);
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        return new Border
        {
            Child = content,
            Padding = new Thickness(14, 12, 18, 12),
            CornerRadius = new CornerRadius(10),
            MaxWidth = 380,
            MinWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = backgroundBrush,
            BorderThickness = new Thickness(1),
            BorderBrush = borderBrush,
        };
    }

    /// <summary>取主题资源；取不到就返回兜底值，绝不抛。</summary>
    private static T? Res<T>(string key, T? fallback = default) where T : class
    {
        try
        {
            return Application.Current?.Resources?[key] as T ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static Brush? Res(string key, Brush? fallback) => Res<Brush>(key, fallback);
}
