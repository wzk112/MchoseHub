using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace MchoseHub.App;

/// <summary>
/// 界面防误触。
///
/// 这是设备配置面板，用户经常一边看着键盘一边在上面打字。WinUI 里按钮
/// 一旦拿到焦点，按空格就等于「再点一次」——上一次点过「应用」或「全部填充」，
/// 之后敲个空格就把设置又下发一遍；ToggleSwitch 拿到焦点时空格直接就翻转了。
///
/// 这里做两件事：
///   1. 鼠标点过的控件不留焦点（AllowFocusOnInteraction = false），
///      随后敲空格就没有目标可以被激活；
///   2. 页面级 PreviewKeyDown 只拦截鼠标遗留焦点上的按钮激活和滑杆调整。
///      Tab 导航、程序化焦点、输入框和原生列表/下拉选择保持键盘操作。
///
/// 键盘焦点可使用空格、回车和方向键，不将 Tab 导航变成只能聚焦不能操作。
/// </summary>
internal static class UiGuard
{
    public static void Install(Page page)
    {
        page.PreviewKeyDown += (_, e) =>
        {
            var focused = FocusManager.GetFocusedElement(page.XamlRoot);

            // Pointer 焦点才可能是上次鼠标操作留下的目标。键盘和程序化
            // 焦点均是显式操作入口，不抑制其原生键盘行为。
            if (focused is not Control { FocusState: FocusState.Pointer } || IsTyping(focused))
            {
                return;
            }

            if (focused is ButtonBase or ToggleSwitch
                && e.Key is VirtualKey.Space or VirtualKey.Enter)
            {
                e.Handled = true;
                return;
            }

            // 仅保护鼠标操作后的滑杆；Tab 导航到滑杆时允许方向键调整。
            if (focused is Slider
                && e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right
                    or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown)
            {
                e.Handled = true;
            }
        };

        // 视觉树要等 Loaded 之后才建好
        page.Loaded += (_, _) => ApplyTo(page);
    }

    /// <summary>
    /// 给一棵子树补上「点了不留焦点」。代码里动态建出来的按钮（灯效快捷按钮、
    /// 诊断页那排读命令）在页面 Loaded 之后才存在，必须单独补一遍。
    /// </summary>
    public static void ApplyTo(DependencyObject root) => ClearPointerFocus(root);

    /// <summary>焦点是否在文本输入上（输入框、可编辑下拉框）。</summary>
    private static bool IsTyping(object? focused)
    {
        return focused is TextBox or RichEditBox or PasswordBox or AutoSuggestBox;
    }

    /// <summary>递归把「点一下就抢焦点」的行为关掉。</summary>
    private static void ClearPointerFocus(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            switch (child)
            {
                case ButtonBase:
                case ToggleSwitch:
                case Slider:
                    if (child is Control control)
                    {
                        control.AllowFocusOnInteraction = false;
                    }

                    break;
            }

            ClearPointerFocus(child);
        }
    }
}
