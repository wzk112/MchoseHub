// 设备扫描诊断工具：用来确认某个设备有没有被识别出来，以及各 HID 集合的用途。
// 用法：
//   mchose-scan              只列迈从设备
//   mchose-scan --all        列出全部 HID 设备
//   mchose-scan --probe      对识别到的设备尝试探测协议代次

using MchoseHub.Core.Protocol;
using MchoseHub.Core.Services;
using MchoseHub.Platform.Windows;

var includeAll = args.Contains("--all");
var probe = args.Contains("--probe");
var keyboard = args.Contains("--keyboard");
var groups = args.Contains("--groups");

if (groups)
{
    var serviceForGroups = new MchoseDeviceService(new WindowsHidTransport(), _ => { });
    var list = serviceForGroups.ScanGroups(includeAll);

    Console.WriteLine();
    Console.WriteLine($"按物理设备聚合后共 {list.Count} 台设备");
    Console.WriteLine(new string('-', 96));
    foreach (var group in list)
    {
        Console.WriteLine($"{group.Name}    {group.Id}");
        Console.WriteLine($"    集合数 {group.Collections.Count}    通道 {group.KindText}");
        Console.WriteLine($"    配置通道 usagePage 0x{group.Channel.Info.UsagePage:X4}  " +
                          $"in {group.Channel.Info.InputReportLength} / out {group.Channel.Info.OutputReportLength} / feature {group.Channel.Info.FeatureReportLength}");
    }
    return;
}

var transport = new WindowsHidTransport();
var service = new MchoseDeviceService(transport, message => Console.WriteLine($"  [log] {message}"));

var detected = service.Scan(includeAll);

Console.WriteLine();
Console.WriteLine($"共列出 {detected.Count} 个 HID 集合");
Console.WriteLine(new string('-', 110));
Console.WriteLine($"{"VID/PID",-14} {"usagePage",-10} {"usage",-8} {"支持",-6} {"输入/输出/feature",-18} 设备名");
Console.WriteLine(new string('-', 110));

foreach (var item in detected)
{
    var info = item.Info;
    var supported = item.Definition is null ? "—" : "是";
    var lengths = $"{info.InputReportLength}/{info.OutputReportLength}/{info.FeatureReportLength}";
    Console.WriteLine(
        $"{info.Id,-14} 0x{info.UsagePage:X4}     0x{info.Usage:X4}   {supported,-6} {lengths,-18} {item.DisplayName}");
}

if (!detected.Any(d => d.IsSupported))
{
    Console.WriteLine();
    Console.WriteLine("没有命中设备表。用 --all 看看设备是否至少能被枚举出来。");
}

if (probe)
{
    Console.WriteLine();
    Console.WriteLine("协议探测：");

    // 只看厂商自定义集合（usagePage 0xFF00 及以上），那才是配置通道
    var candidates = detected
        .Where(d => d.Info.UsagePage >= 0xFF00)
        .ToList();

    if (candidates.Count == 0)
    {
        Console.WriteLine("  没有找到厂商自定义 HID 集合，无法探测。");
    }

    foreach (var candidate in candidates)
    {
        Console.WriteLine($"  → {candidate.Info.Id} usagePage 0x{candidate.Info.UsagePage:X4}");
        try
        {
            using var connection = service.Connect(candidate);
            var generation = await connection.ProbeAsync();
            Console.WriteLine($"    协议代次：{generation}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    打开失败：{ex.Message}");
        }
    }
}

if (keyboard)
{
    Console.WriteLine();
    Console.WriteLine("键盘通道测试：");

    var channel = detected.FirstOrDefault(MchoseDeviceService.IsKeyboardChannel);
    if (channel is null)
    {
        Console.WriteLine("  没有找到键盘配置通道（需要厂商自定义集合且 feature 长度 ≥ 519）。");
    }
    else
    {
        Console.WriteLine($"  通道：{channel.Info.Id} usagePage 0x{channel.Info.UsagePage:X4} feature {channel.Info.FeatureReportLength}B");
        Console.WriteLine($"  路径：{channel.Info.Path}");

        try
        {
            using var session = service.ConnectKeyboard(channel);
            var reportId = await session.ResolveReportIdAsync();

            if (reportId is null)
            {
                Console.WriteLine("  report id 未确定，下面的读取大概率无响应。");
            }
            else
            {
                Console.WriteLine($"  report id = 0x{reportId:X2}");
            }

            foreach (var command in KeyboardProtocol.ReadCommands)
            {
                Console.WriteLine($"  → {command.DisplayName}（{command.Name}）");
                try
                {
                    var response = session.Read(command.Name);
                    Console.WriteLine($"     {KeyboardProtocol.Describe(response)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"     失败：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  打开失败：{ex.Message}");
        }
    }
}

// 反复开关键盘通道：专门验证「第二次打开就连不上、后面全部 Win32 87」这个问题
if (args.Contains("--reopen"))
{
    var channel3 = detected.FirstOrDefault(MchoseDeviceService.IsKeyboardChannel);
    if (channel3 is null)
    {
        Console.WriteLine();
        Console.WriteLine("没有找到键盘配置通道。");
        return;
    }

    int times = 3;
    int reopenIndex = Array.IndexOf(args, "--reopen");
    if (reopenIndex + 1 < args.Length && int.TryParse(args[reopenIndex + 1], out var parsedTimes))
    {
        times = Math.Clamp(parsedTimes, 1, 50);
    }

    Console.WriteLine();
    Console.WriteLine($"反复开关键盘通道 {times} 次：");
    int succeeded = 0;
    for (int round = 1; round <= times; round++)
    {
        try
        {
            using var reopened = service.ConnectKeyboard(channel3);
            var reportId = await reopened.ResolveReportIdAsync();
            if (reportId is null)
            {
                Console.WriteLine($"  第 {round,2} 次：report id 未能确定 ✘");
                continue;
            }

            var reply = reopened.Read("getPerformance");
            bool echoed = reply.Length > 2 && reply[1] == 0x84;
            Console.WriteLine(
                $"  第 {round,2} 次：report id 0x{reportId:X2}，getPerformance 回包 {reply.Length} 字节" +
                $"{(echoed ? " ✔" : " ✘（没有回显命令头）")}");
            if (echoed)
            {
                succeeded++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  第 {round,2} 次：失败 {ex.Message}");
        }

        await Task.Delay(300);
    }

    Console.WriteLine($"  成功 {succeeded}/{times}");
    return;
}

// 压力测试：--stress 秒数
// 读线程一直挂着一笔未完成的 ReadFile，同时制造大量托管分配 + 强制 GC 压缩。
// 如果读缓冲区没被钉住，GC 搬走它之后内核还会往旧地址写，堆一坏进程就崩；
// 这个测试就是专门用来把那种崩溃逼出来的。
if (args.Contains("--stress"))
{
    var stressChannel = detected.FirstOrDefault(MchoseDeviceService.IsKeyboardChannel);
    if (stressChannel is null)
    {
        Console.WriteLine("没有找到键盘配置通道，无法压测。");
        return;
    }

    int seconds = 60;
    int stressIndex = Array.IndexOf(args, "--stress");
    if (stressIndex + 1 < args.Length && int.TryParse(args[stressIndex + 1], out var parsedSeconds))
    {
        seconds = Math.Clamp(parsedSeconds, 5, 3600);
    }

    Console.WriteLine($"开始压测 {seconds} 秒：未完成的读 + 频繁分配 + 强制 GC …");
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    var rng = new Random(1234);
    var trash = new List<byte[]>();
    int rounds = 0;
    int cycles = 0;

    while (DateTime.UtcNow < deadline)
    {
        // 每轮换一个新会话，模拟「打开设备 → 用一会儿 → 关掉」
        using (var stressSession = service.ConnectKeyboard(stressChannel))
        {
            var id = await stressSession.ResolveReportIdAsync();
            if (id is null)
            {
                Console.WriteLine($"  第 {++cycles} 轮：report id 未确定，跳过");
                continue;
            }

            for (int i = 0; i < 12 && DateTime.UtcNow < deadline; i++)
            {
                rounds++;

                // 制造垃圾，逼 GC 压缩堆（这正是搬走未钉住缓冲区的时机）
                trash.Add(new byte[256 * 1024 + rng.Next(4096)]);
                if (trash.Count > 40)
                {
                    trash.RemoveRange(0, 20);
                }

                if (rounds % 4 == 0)
                {
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                }

                try
                {
                    var battery = stressSession.Read("getBattery");
                    var performance = stressSession.Read("getPerformance");
                    bool ok = battery.Length > 2 && performance.Length > 2;
                    if (rounds % 25 == 0)
                    {
                        Console.WriteLine(
                            $"  {DateTime.Now:HH:mm:ss}  第 {rounds} 次读取" +
                            $"（本会话第 {i + 1} 次）{(ok ? " ✔" : " ✘")}  " +
                            $"堆 {GC.GetTotalMemory(false) / (1024 * 1024)}MB  句柄 {GetHandles()}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  读取失败：{ex.Message}");
                }
            }

            cycles++;
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    Console.WriteLine($"压测结束：{rounds} 次读取 / {cycles} 次开关会话，没崩 ✔");
    return;
}

static int GetHandles() => System.Diagnostics.Process.GetCurrentProcess().HandleCount;

// 下面这一组开关都必须先有键盘配置会话。单独写 --diy-backup 之类而不带 --decode
// 的话，会话根本没建起来，脚本会在后面拿着空引用崩掉（事件日志里是 0xe0434352）。
// 与其让用户对着崩溃日志发愣，不如把任何一个键盘开关都当成「需要 --decode」。
string[] keyboardSessionFlags =
[
    "--decode", "--roundtrip", "--diy", "--diy-set", "--diy-backup", "--diy-restore",
    "--keys", "--remap", "--set", "--effect", "--light-color", "--brightness", "--repair-light",
    "--force-light",
    "--fields",
];

if (keyboardSessionFlags.Any(args.Contains))
{
    Console.WriteLine();
    Console.WriteLine("键盘配置解析：");

    var channel2 = detected.FirstOrDefault(MchoseDeviceService.IsKeyboardChannel);
    if (channel2 is null)
    {
        Console.WriteLine("  没有找到键盘配置通道。");
        return;
    }

    using var session2 = service.ConnectKeyboard(channel2);
    await session2.ResolveReportIdAsync();

    var modelKey = session2.IsWireless ? session2.ResolveWirelessModelKey() : KeyboardSettingsSession.ResolveModelKey(channel2.DisplayName);
    var schema = KeyboardModelSchemas.Get(modelKey);
    var settings = new KeyboardSettingsSession(session2, schema, message => Console.WriteLine($"  [log] {message}"));
    Console.WriteLine($"  机型 {schema.Model}（识别自 {channel2.DisplayName}），按键 {schema.KeyList.Count} 个");

    var performance = settings.ReadPerformance();
    Console.WriteLine("  —— 性能设置 ——");
    foreach (var name in new[] { "macSwitch", "rateVal", "latencySwitch", "lightType", "lightMode", "winKeySwitch", "sleepTimeVal" })
    {
        Console.WriteLine($"    {name,-14} = {performance.GetNumber(name)}");
    }

    if (args.Contains("--fields"))
    {
        // 逐字段打印偏移，便于和网页版 parser 对照
        var raw = session2.Read("getPerformance");
        int offset = 2;
        Console.WriteLine("  —— getPerformance 字段布局 ——");
        foreach (var field in schema.ReadPerformance.Fields)
        {
            int size = field.Kind switch
            {
                FieldKind.Uint8 => 1,
                FieldKind.Bit4 => 0,
                _ => field.Length,
            };

            if (field.Kind == FieldKind.Uint8)
            {
                Console.WriteLine($"    [{offset,3}] {field.Name,-16} = {raw[offset]}");
            }
            else if (field.Kind == FieldKind.FixedArray && field.Length <= 8)
            {
                Console.WriteLine($"    [{offset,3}] {field.Name,-16} = {Convert.ToHexString(raw.AsSpan(offset, field.Length))}");
            }

            offset += size;
        }
    }

    var light = settings.ReadLightColor();
    Console.WriteLine("  —— 灯效配置 ——");
    foreach (var index in new[] { 1, 2, 3, 4, 5, 7, 8, 10, 11, 12, 13 })
    {
        var color = light.GetBuffer($"lightModeColor{index}");
        if (color.Length == 0)
        {
            continue;
        }

        Console.WriteLine($"    lightModeColor{index,-3} = #{color[0]:X2}{color[1]:X2}{color[2]:X2}");
    }

    var keys = settings.ReadKeySetting();
    var table = keys.GetTable("#keyList");
    Console.WriteLine($"  —— 按键设置：解析到 {table.Count} 个键 ——");
    for (int i = 0; i < Math.Min(table.Count, 8); i++)
    {
        var name = i < schema.KeyList.Count ? schema.KeyList[i].Name : $"#{i}";
        Console.WriteLine($"    {name,-14} = {Convert.ToHexString(table[i])}");
    }

    // 现场改亮度：--brightness 100
    int brightnessArg = args.Contains("--brightness")
        ? int.Parse(args[Array.IndexOf(args, "--brightness") + 1])
        : -1;
    var highLevel = new KeyboardSettings(settings);

    // 修灯效参数区：--repair-light
    // 界面说「下发成功」但键盘毫无反应时用这个：把固件内部那块参数（space2..space11 +
    // 每个灯效的亮度/速度/多彩槽位）重铺成出厂值，用户自己的设置不变。
    if (args.Contains("--repair-light"))
    {
        try
        {
            Console.WriteLine($"  {highLevel.RepairLightProfile()}");
            Console.WriteLine("  提示：修完灯亮了但偏暗的话，在界面上把亮度拉到 100% 再应用一次即可。");
        }
        catch (KeyboardSession.NotReadyException ex)
        {
            // 设备正忙 / 没连上时不要把栈甩给用户
            Console.WriteLine($"  修复失败：{ex.Message}");
        }
    }

    // 直接改字段：--set latencySwitch=0,winKeySwitch=1
    // 键盘一片黑时的一键自救：--force-light
    // 只写三个用户可见字段——灯效=常亮(1)、亮度=100、lightType=0（退出逐键模式），
    // 颜色给白色。**不碰固件参数区**，所以不会被固件当成一次「恢复出厂」。
    if (args.Contains("--force-light"))
    {
        try
        {
            var current = highLevel.ReadPerformance();
            highLevel.WritePerformance(current with
            {
                LightMode = 1,
                LightType = 0,
                Brightness = 100,
                Speed = 0,
            });
            highLevel.WriteLighting(new LightingSettings
            {
                LightMode = 1,
                Brightness = 100,
                Speed = 0,
                Red = 0xFF,
                Green = 0xFF,
                Blue = 0xFF,
            });
            Console.WriteLine("  已强制点亮：灯效=常亮(1)，亮度=100%，颜色=白色，已退出逐键模式");
        }
        catch (KeyboardSession.NotReadyException ex)
        {
            Console.WriteLine($"  强制点亮失败：{ex.Message}");
        }
    }

    if (args.Contains("--set"))
    {
        var spec = args[Array.IndexOf(args, "--set") + 1];
        var values = settings.ReadPerformance();
        foreach (var pair in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=');
            values.SetNumber(parts[0], int.Parse(parts[1]));
        }

        Console.WriteLine($"  写字段：{spec}");
        settings.WritePerformance(values);
        await Task.Delay(200);
        var check = settings.ReadPerformance();
        foreach (var pair in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = pair.Split('=')[0];
            Console.WriteLine($"  回读 {name} = {check.GetNumber(name)}");
        }
    }

    // 切灯效：--effect 21（写 lightMode，lightType 由 lightMode 决定）
    if (args.Contains("--effect"))
    {
        int mode = int.Parse(args[Array.IndexOf(args, "--effect") + 1]);
        var current = highLevel.ReadPerformance();
        var next = current with { LightMode = mode };

        // 每个灯效在设备上另有亮度/速度槽位；读到该灯效的值就用它的，
        // 免得把上一个灯效（或自定义灯效那种缺字段的 0）带过去写黑。
        if (highLevel.TryGetEffectLighting(mode, out var storedBrightness, out var storedSpeed))
        {
            next = next with { Brightness = storedBrightness, Speed = storedSpeed };
        }

        Console.WriteLine($"  切灯效：{current.LightMode} → {mode}" +
                          $"（lightType {current.LightType} → {(mode == schema.CustomLightMode ? 1 : 0)}）");
        highLevel.WritePerformance(next);
        await Task.Delay(250);
        var after = highLevel.ReadPerformance();
        Console.WriteLine($"  回读：lightMode {after.LightMode}，lightType {after.LightType}，" +
                          $"亮度 {after.Brightness}，速度 {after.Speed}");
    }

    // 改某个灯效的颜色：--light-color 7=#FF00FF
    if (args.Contains("--light-color"))
    {
        var spec = args[Array.IndexOf(args, "--light-color") + 1];
        var parts = spec.Split('=');
        int mode = int.Parse(parts[0]);
        var hex = parts[1].TrimStart('#');
        byte r = Convert.ToByte(hex.Substring(0, 2), 16);
        byte g = Convert.ToByte(hex.Substring(2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(4, 2), 16);

        var lightTable = settings.ReadLightColor();
        var before = lightTable.GetBuffer($"lightModeColor{mode}");
        lightTable.SetBuffer($"lightModeColor{mode}", [r, g, b]);
        Console.WriteLine($"  灯效 {mode} 颜色：{Convert.ToHexString(before)} → {r:X2}{g:X2}{b:X2}");
        settings.WriteLightColor(lightTable);
        await Task.Delay(150);
        var after = settings.ReadLightColor().GetBuffer($"lightModeColor{mode}");
        Console.WriteLine($"  回读：{Convert.ToHexString(after)}");
    }

    if (brightnessArg >= 0)
    {
        var current = highLevel.ReadPerformance();
        Console.WriteLine($"  写亮度：灯效 {current.LightMode}，{current.Brightness} → {brightnessArg}");
        highLevel.WritePerformance(current with { Brightness = brightnessArg });
        await Task.Delay(200);
        var after = highLevel.ReadPerformance();
        Console.WriteLine($"  回读：亮度 {after.Brightness}，速度 {after.Speed}");
    }

    // 逐键灯光：--diy 读取，--diy-set Esc=#FF0000,Space=#00FF00 写入

    if (args.Contains("--diy") || args.Contains("--diy-set"))
    {
        var diy = highLevel.ReadDiyLight();
        var size = settings.Model.DiyTableSize;
        int lit = Enumerable.Range(0, size).Count(i => diy[i] != ((byte)0, (byte)0, (byte)0));
        Console.WriteLine($"  —— 逐键灯色（表容量 {size}，按键 {settings.Model.KeyList.Count} 个，非黑 {lit} 个）——");

        foreach (var key in new[] { "Esc", "one", "Q", "A", "Space", "Left_Ctrl" })
        {
            var index = settings.Model.FindLedIndex(key);
            if (index is null)
            {
                continue;
            }

            var (r, g, b) = diy[index.Value];
            Console.WriteLine($"    {key,-12} LED#{index.Value,-4} = #{r:X2}{g:X2}{b:X2}");
        }

        if (args.Contains("--diy-set"))
        {
            var spec = args[Array.IndexOf(args, "--diy-set") + 1];
            foreach (var pair in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=');
                var hex = parts[1].TrimStart('#');
                var name = parts[0];
                bool ok = KeyboardSettings.SetKeyColor(
                    settings.Model,
                    diy,
                    name,
                    Convert.ToByte(hex[..2], 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16));
                Console.WriteLine($"    设置 {name} = {parts[1]} → {(ok ? "已排入" : "机型没这个键")}");
            }

            highLevel.WriteDiyLight(diy);
            await Task.Delay(200);

            var check = highLevel.ReadDiyLight();
            foreach (var pair in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=');
                var index = settings.Model.FindLedIndex(parts[0]);
                if (index is null)
                {
                    continue;
                }

                var hex = parts[1].TrimStart('#');
                var (r, g, b) = check[index.Value];
                var want = $"#{hex.ToUpperInvariant()}";
                Console.WriteLine($"    回读 {parts[0]} = #{r:X2}{g:X2}{b:X2}（期望 {want}）{(want == $"#{r:X2}{g:X2}{b:X2}" ? " ✔" : " ✘")}");
            }
        }
    }

    // 改键：--keys Default|Fn|Fn2 读取，--remap Caps=Esc 写入（目标写「默认」就是恢复）
    if (args.Contains("--keys") || args.Contains("--remap"))
    {
        var layerArg = args.Contains("--keys")
            ? args[Array.IndexOf(args, "--keys") + 1]
            : "Default";
        var mappings = highLevel.ReadKeyMappings(layerArg);
        int changed = mappings.Count(m => !m.IsDefault);
        Console.WriteLine($"  —— {layerArg} 层按键（共 {mappings.Count} 个，改过的 {changed} 个）——");

        if (args.Contains("--hex"))
        {
            var raw = session2.SendRaw(KeyboardProtocol.LayeredKeySettingHeader(KeyboardProtocol.LayerCode(layerArg)));
            // 表区从第 8 字节开始，102 项 × 4 字节
            Console.WriteLine($"    按键表原始字节（{raw.Length} 字节回包）：{Convert.ToHexString(raw.AsSpan(8, 408))}");
        }

        foreach (var m in mappings.Where(x => !x.IsDefault).Take(12))
        {
            Console.WriteLine($"    {m.Key,-14} {m.Display}   {Convert.ToHexString(m.Value)}");
        }

        if (args.Contains("--remap"))
        {
            var spec = args[Array.IndexOf(args, "--remap") + 1];
            var parts = spec.Split('=');
            Console.WriteLine($"  改键：{parts[0]} → {parts[1]}（{layerArg} 层）");
            highLevel.RemapKey(layerArg, parts[0], parts[1]);
            await Task.Delay(200);

            var check = highLevel.ReadKeyMappings(layerArg)
                .FirstOrDefault(m => string.Equals(m.Key, parts[0], StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"    回读 {parts[0]} = {check?.Display}   {Convert.ToHexString(check?.Value ?? [])}");
        }
    }

    // 逐键灯色备份 / 还原：--diy-backup file.json / --diy-restore file.json
    if (args.Contains("--diy-backup") || args.Contains("--diy-restore"))
    {
        bool backup = args.Contains("--diy-backup");
        var path = args[Array.IndexOf(args, backup ? "--diy-backup" : "--diy-restore") + 1];
        var diy = highLevel.ReadDiyLight();

        if (backup)
        {
            var data = new
            {
                model = settings.Model.Model,
                size = diy.Size,
                red = diy.Red.Select(x => (int)x).ToArray(),
                green = diy.Green.Select(x => (int)x).ToArray(),
                blue = diy.Blue.Select(x => (int)x).ToArray(),
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(data));
            Console.WriteLine($"  已备份逐键灯色到 {path}（{diy.Size} 个 LED）");
        }
        else
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var red = ReadChannel(root, "red");
            var green = ReadChannel(root, "green");
            var blue = ReadChannel(root, "blue");

            for (int i = 0; i < Math.Min(diy.Size, red.Length); i++)
            {
                diy[i] = (red[i], green[i], blue[i]);
            }

            highLevel.WriteDiyLight(diy);
            Console.WriteLine($"  已从 {path} 还原逐键灯色");
        }
    }

    if (args.Contains("--roundtrip"))
    {
        if (args.Contains("--verbose"))
        {
            var raw = session2.Read("getPerformance");
            var encoded = KeyboardPayloadCodec.Encode(
                schema.WritePerformance, performance.Clone(), 0x04);
            Console.WriteLine("  —— 编码对照 ——");
            // 整段打出来：这个字段块里 space3..space11 是固件的参数区，
            // 排查「灯效写了没反应」时必须能跟官方出厂值逐字节比，截断就没法比了。
            Console.WriteLine($"    读回 payload（{raw.Length - 2} 字节）：{Convert.ToHexString(raw.AsSpan(2, raw.Length - 2))}");
            Console.WriteLine($"    待写 payload（{encoded.Length - 1} 字节）：{Convert.ToHexString(encoded.AsSpan(1, encoded.Length - 1))}");
        }

        Console.WriteLine();
        Console.WriteLine("  —— 回写校验（读 → 原样写回 → 再读，比对字节）——");
        var before = session2.Read("getPerformance");
        var writeValues = performance.Clone();
        settings.WritePerformance(writeValues);
        await Task.Delay(150);
        var after = session2.Read("getPerformance");
        Console.WriteLine($"    性能：{(before.AsSpan(2).SequenceEqual(after.AsSpan(2)) ? "一致 ✔" : "不一致 ✘")}");
        if (!before.AsSpan(2).SequenceEqual(after.AsSpan(2)))
        {
            Console.WriteLine($"      写前 {Convert.ToHexString(before.AsSpan(2, 40))}");
            Console.WriteLine($"      写后 {Convert.ToHexString(after.AsSpan(2, 40))}");
        }

        var lightBefore = session2.Read("getLightColor");
        settings.WriteLightColor(light.Clone());
        await Task.Delay(150);
        var lightAfter = session2.Read("getLightColor");
        // 灯效回包有效长度固定 2 + 496 字节，后面是上次读剩下的残留
        var lightLength = Math.Min(498, Math.Min(lightBefore.Length, lightAfter.Length));
        Console.WriteLine($"    灯效：{(lightBefore.AsSpan(2, lightLength - 2).SequenceEqual(lightAfter.AsSpan(2, lightLength - 2)) ? "一致 ✔" : "不一致 ✘")}");
    }
}

// 备份文件里通道可能是数字数组，也可能是早期版本写下的 base64 字符串
static byte[] ReadChannel(System.Text.Json.JsonElement root, string name)
{
    var element = root.GetProperty(name);
    return element.ValueKind == System.Text.Json.JsonValueKind.String
        ? Convert.FromBase64String(element.GetString()!)
        : element.EnumerateArray().Select(x => (byte)x.GetInt32()).ToArray();
}
