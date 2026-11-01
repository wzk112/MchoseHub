using System.Text.Json;

namespace MchoseHub.Core.Protocol;

public sealed record KeyboardFunction(string Id, string Name, string Group, byte[] Value);

/// <summary>Official GF/AD/OD/jD and Tlt tables, extracted 2026-10-05.</summary>
public static class KeyboardFunctions
{
    private static readonly Lazy<(IReadOnlyList<KeyboardFunction> Functions, Dictionary<string, byte> Keys)> Data = new(Load);
    public static IReadOnlyList<KeyboardFunction> All => Data.Value.Functions;
    public static IReadOnlyDictionary<string, byte> MacroKeys => Data.Value.Keys;
    public static KeyboardFunction? Find(string name) => All.FirstOrDefault(f =>
        string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(f.Id, name, StringComparison.OrdinalIgnoreCase));
    public static string? Describe(byte[] value) => All.FirstOrDefault(f => f.Value.SequenceEqual(value))?.Name;

    private static (IReadOnlyList<KeyboardFunction>, Dictionary<string, byte>) Load()
    {
        using var stream = typeof(KeyboardFunctions).Assembly.GetManifestResourceStream(
            "MchoseHub.Core.Resources.keyboard-functions.json") ?? throw new InvalidOperationException("功能码表缺失。");
        using var doc = JsonDocument.Parse(stream);
        var labels = new Dictionary<string, string>
        {
            ["keyboard.left"] = "鼠标左键",
            ["keyboard.right"] = "鼠标右键",
            ["keyboard.mid"] = "鼠标中键",
            ["keyboard.mForward"] = "鼠标前进",
            ["keyboard.mRetreat"] = "鼠标后退",
            ["keyboard.scrollUp"] = "滚轮向上",
            ["keyboard.scrollDown"] = "滚轮向下",
            ["mouse.playPause"] = "播放/暂停",
            ["mouse.silence"] = "静音",
            ["mouse.stopPlaying"] = "停止播放",
            ["mouse.previousSong"] = "上一曲",
            ["mouse.nextSong"] = "下一曲",
            ["mouse.volumeUp"] = "音量加",
            ["mouse.volumeDown"] = "音量减",
            ["mouse.zoomOut"] = "缩小",
            ["mouse.zoomIn"] = "放大",
            ["mouse.resetting"] = "重置缩放",
            ["mouse.recover"] = "重做",
            ["mouse.repeal"] = "撤销",
            ["mouse.selectAll"] = "全选",
            ["mouse.new"] = "新建",
            ["mouse.copy"] = "复制",
            ["mouse.cut"] = "剪切",
            ["mouse.paste"] = "粘贴",
            ["mouse.save"] = "保存",
            ["mouse.open"] = "打开",
            ["mouse.newItem"] = "新建标签页",
            ["mouse.closeItem"] = "关闭标签页",
            ["keyboard.retreat"] = "后退",
            ["keyboard.forward"] = "前进",
            ["mouse.taskbar"] = "任务栏",
            ["mouse.openNarrative"] = "讲述人",
            ["mouse.loopApplication"] = "切换应用",
            ["mouse.actionCenter"] = "操作中心",
            ["mouse.fileExplorer"] = "资源管理器",
            ["mouse.winSettingsCenter"] = "Windows 设置",
            ["keyboard.lockComputer"] = "锁定电脑",
            ["mouse.projectToOtherDevices"] = "投影",
            ["mouse.jumpToTray"] = "系统托盘",
            ["mouse.launchXboxGameBar"] = "Xbox Game Bar",
            ["mouse.operation"] = "运行",
            ["mouse.search"] = "搜索",
            ["mouse.displaySetting"] = "显示设置",
            ["mouse.simpleMenu"] = "快捷菜单",
            ["mouse.emojiBox"] = "表情面板",
            ["mouse.startMenu"] = "开始菜单",
            ["keyboard.conversionWindow"] = "切换窗口",
            ["keyboard.closeWindow"] = "关闭窗口",
            ["mouse.endApplication"] = "结束应用",
            ["mouse.switchNextApplication"] = "下一个应用",
            ["keyboard.browserHome"] = "浏览器主页",
            ["keyboard.calculator"] = "计算器",
            ["keyboard.email"] = "邮件",
            ["keyboard.myComputer"] = "此电脑",
            ["keyboard.favorites"] = "收藏夹",
            ["keyboard.showDesktop"] = "显示桌面",
            ["keyboard.windowsSecurity"] = "Windows 安全界面",
            ["mouse.taskManager"] = "任务管理器",
            ["mouse.screenBrightUp"] = "屏幕亮度加",
            ["mouse.screenBrightDown"] = "屏幕亮度减",
            ["keyboard.fnLayer"] = "Fn 层切换",
            ["keyboard.fn2Layer"] = "Fn2 层切换",
        };
        var list = doc.RootElement.GetProperty("functions").EnumerateArray().Select(e =>
        {
            string id = e.GetProperty("name").GetString()!;
            return new KeyboardFunction(id, labels.GetValueOrDefault(id, id), e.GetProperty("group").GetString()!,
                e.GetProperty("value").EnumerateArray().Select(v => v.GetByte()).ToArray());
        }).ToList();
        var keys = doc.RootElement.GetProperty("macroKeys").EnumerateObject()
            .ToDictionary(e => e.Name, e => e.Value.GetByte(), StringComparer.Ordinal);
        return (list, keys);
    }
}
