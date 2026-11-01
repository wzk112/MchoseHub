using MchoseHub.Core.Protocol;
using MchoseHub.Core.Services;
using MchoseHub.Core.Transport;

int passed=0;
void Test(string name, Action test) { test();passed++;Console.WriteLine($"PASS {name}"); }
void Equal<T>(T expected,T actual) { if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new Exception($"Expected {expected}, got {actual}"); }
void Bytes(byte[] expected,byte[] actual) { if(!expected.SequenceEqual(actual))throw new Exception($"Expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual)}"); }
void Throws(Action action) { try{action();}catch(Exception ex) when(ex is ArgumentException or InvalidDataException or KeyboardSession.NotReadyException){return;}throw new Exception("Expected input to be rejected"); }

Test("official function table has 166 four-byte entries",()=>{Equal(166,KeyboardFunctions.All.Count);Equal(true,KeyboardFunctions.All.All(f=>f.Value.Length==4));});
Test("media/system/mouse codes match official source",()=>{Bytes([2,0,0,205],KeyboardFunctions.Find("播放/暂停")!.Value);Bytes([0,1,6,0],KeyboardFunctions.Find("复制")!.Value);Bytes([1,1,1,0],KeyboardFunctions.Find("鼠标左键")!.Value);});
Test("macro golden bytes: UTF8 name and event delays",()=>{
    var macro=new KeyboardMacro("A",MacroTrigger.Once,[new("a",true,50),new("a",false,0)]);
    Bytes([4,0,10,0,1,65,0,0,50,4,128,0,0,4],KeyboardMacroCodec.EncodeBank([macro]));
});
Test("macro modifiers and mouse event classes",()=>Bytes([16,0,10,224,160,0,20,1],KeyboardMacroCodec.EncodeEvents([new("ControlLeft",true,10),new("Left",false,20,true)])));
Test("macro trigger golden bindings",()=>{Bytes([3,4,1,0],KeyboardMacroCodec.Binding(MacroTrigger.Hold,0));Bytes([3,2,1,1],KeyboardMacroCodec.Binding(MacroTrigger.Toggle,1));Bytes([3,1,1,2],KeyboardMacroCodec.Binding(MacroTrigger.Once,2));});
Test("macro pages preserve 513-byte boundary",()=>{
    var bank=Enumerable.Range(0,513).Select(i=>(byte)i).ToArray();var pages=KeyboardMacroCodec.BuildPages(bank);
    Equal(2,pages.Count);Bytes([5,0,0,2,0,0,2],pages[0][..7]);Bytes([5,0,0,2,1,1,0],pages[1][..7]);
    Bytes(bank,pages.SelectMany(p=>p.Skip(7)).ToArray());
});
Test("macro directory multiple UTF8 names",()=>{
    var bank=KeyboardMacroCodec.EncodeBank([new("中",MacroTrigger.Once,[new("a",true)]),new("B",MacroTrigger.Hold,[new("b",false)])]);
    Bytes([8,0,8,0,16,0,6,0],bank[..8]);Bytes([3,0xE4,0xB8,0xAD],bank[8..12]);
});
Test("macro invalid keys, delays, duplicates and capacity rejected",()=>{
    Throws(()=>KeyboardMacroCodec.EncodeEvents([new("unknown",true)]));Throws(()=>KeyboardMacroCodec.EncodeEvents([new("a",true,-1)]));
    Throws(()=>KeyboardMacroCodec.EncodeEvents([new("a",true,60001)]));Throws(()=>KeyboardMacroCodec.EncodeBank([new("",MacroTrigger.Once,[new("a",true)])]));
    Throws(()=>KeyboardMacroCodec.EncodeBank([new("A",MacroTrigger.Once,[new("a",true)]),new("A",MacroTrigger.Once,[new("a",true)])]));
    Throws(()=>KeyboardMacroCodec.EncodeBank([new("A",MacroTrigger.Once,Enumerable.Repeat(new MacroStep("a",true),16000).ToArray())]));
});
Test("unknown model never falls back to K87",()=>{Throws(()=>KeyboardSettingsSession.ResolveModelKey("MCHOSE K870"));Throws(()=>KeyboardModelSchemas.Get("ACE68"));});
Test("official model aliases resolve longest model",()=>{Equal("K99_V2",KeyboardSettingsSession.ResolveModelKey("MCHOSE K99 V2"));Equal("Z75S",KeyboardSettingsSession.ResolveModelKey("MCHOSE Z75 S"));Equal("G87",KeyboardSettingsSession.ResolveModelKey("MCHOSE G87"));});
Test("model-specific light modes",()=>{
    Equal(true,KeyboardEffectTable.ForModel(19,"Z75").Any(e=>e.Id==14));Equal(false,KeyboardEffectTable.ForModel(19,"Z75").Any(e=>e.Id==13));
    Equal(false,KeyboardEffectTable.ForModel(21).Any(e=>e.Id==6));Equal(true,KeyboardEffectTable.ForModel(21,"G75_V2").Any(e=>e.Id==6));
});
Test("invalid layer names rejected",()=>{Throws(()=>KeyboardProtocol.LayerCode("Fn3"));Throws(()=>KeyboardProtocol.LayeredKeySettingHeader(3));});
Test("truncated configuration cannot decode as zeros",()=>Throws(()=>KeyboardPayloadCodec.Decode(KeyboardModelSchemas.Get("K87").ReadPerformance,new byte[12])));
Test("incomplete configuration cannot encode",()=>Throws(()=>KeyboardPayloadCodec.Encode(KeyboardModelSchemas.Get("K87").WritePerformance,new PayloadValues(),4)));
Test("scalar values do not wrap",()=>{var values=new PayloadValues();Throws(()=>values.SetNumber("rateVal",256));Throws(()=>values.SetNumber("rateVal",-1));});
Test("frame overflow/version/checksum validation",()=>{
    Throws(()=>MchoseFrameCodec.Build([1,2],3,new byte[255],reportLength:300));
    var f=MchoseFrameCodec.Build([1,2],3,[5,6],check:true);Equal(true,MchoseFrameCodec.Parse(f)!.IsValid);f[7]^=1;Equal(false,MchoseFrameCodec.Parse(f)!.IsValid);f[1]=2;Equal<MchoseFrame?>(null,MchoseFrameCodec.Parse(f));
});

foreach(var modelName in KeyboardModelSchemas.Models)
{
    var model=KeyboardModelSchemas.Get(modelName);
    Test($"{modelName}: full official board and strict restore plan",()=>{
        Equal(true,model.Board.Count>60);Equal(true,model.Board.All(b=>model.FindLedIndex(b.Key) is not null || (modelName=="G75_V2" && b.Key is "circle_pure" or "circle_volumeRoll")));
        using var fake=new FakeSession(model);using var session=new KeyboardSession(fake);var settings=new KeyboardSettingsSession(session,model);
        var profile=new KeyboardProfile(1,modelName,PayloadSnapshot.From(fake.Values(model.ReadPerformance,[0,0,1,0,128,0])),
            PayloadSnapshot.From(fake.Values(model.ReadLightColor,[0,0,1,0,0,2])),PayloadSnapshot.From(fake.Values(model.ReadDiyLight,[0,0,1,0,122,1])),
            Enumerable.Range(0,3).Select(i=>PayloadSnapshot.From(fake.Values(model.ReadKeySetting,[(byte)i,0,1,0,248,1]))).ToArray());
        settings.ValidateProfile(profile);settings.CreateFactoryProfile(profile);Equal(0,fake.Writes);
        Throws(()=>settings.ValidateProfile(profile with{Model="other"}));
        profile.Lighting.Buffers.Remove("space0");Throws(()=>settings.ValidateProfile(profile));Equal(0,fake.Writes);
    });
}
Test("Fn layer ordinary remap uses HID key, not target Fn action",()=>{
    var model=KeyboardModelSchemas.Get("K87");using var fake=new FakeSession(model);using var session=new KeyboardSession(fake);
    var settings=new KeyboardSettings(new KeyboardSettingsSession(session,model));settings.RemapKey("Fn","Caps","F1");
    var mapping=settings.ReadKeyMappings("Fn").First(m=>m.Key=="Caps");Bytes([0,0,0,58],mapping.Value);
});
Test("K87 remap preserves every unrelated raw slot including nonzero firmware placeholders",()=>
{
    var model=KeyboardModelSchemas.Get("K87");
    using var fake=new FakeSession(model){InitialKeyValues=new Dictionary<int,byte[]>{[70]=[0,0,0,0x87],[6]=[0x12,0x34,0x56,0x78]}};
    using var session=new KeyboardSession(fake);var rawSettings=new KeyboardSettingsSession(session,model);
    var before=rawSettings.ReadKeySetting().GetTable("#keyList").Select(b=>b.ToArray()).ToArray();
    var settings=new KeyboardSettings(rawSettings);settings.RemapKey("Default","Scroll","F1");
    var after=rawSettings.ReadKeySetting().GetTable("#keyList");int changed=settings.IndexOfKey("Scroll");
    for(int i=0;i<before.Length;i++)Bytes(i==changed ? [0,0,0,58] : before[i],after[i]);
    Bytes([0,0,0,0x87],after[70]);
    var restore=rawSettings.ReadKeySetting();restore.SetTable("#keyList",before.Select(b=>b.ToArray()).ToList());
    rawSettings.WriteKeySetting(restore);after=rawSettings.ReadKeySetting().GetTable("#keyList");
    for(int i=0;i<before.Length;i++)Bytes(before[i],after[i]);
});
Test("write retry checks third and final write",()=>{
    var model=KeyboardModelSchemas.Get("K87");using var fake=new FakeSession(model){ApplyAfterWrites=3};using var session=new KeyboardSession(fake);
    var settings=new KeyboardSettings(new KeyboardSettingsSession(session,model));var p=settings.ReadPerformance();
    settings.WritePerformance(p with{WinKeyLocked=true});Equal(3,fake.Writes);Equal(true,settings.ReadPerformance().WinKeyLocked);
});
Test("partial raw report is rejected before transport",()=>{
    using var fake=new FakeSession(KeyboardModelSchemas.Get("K87"));using var session=new KeyboardSession(fake);
    Throws(()=>session.SendRaw(new byte[520]));Throws(()=>session.SendRaw([]));Equal(0,fake.Sends);
});
Test("same-model physical keyboards remain separate",()=>
{
    var template = new HidDeviceInfo { Path="a", VendorId=0x05AC, ProductId=0x024F, ProductName="MCHOSE K87", UsagePage=0xFF00, FeatureReportLength=520 };
    var first = Guid.NewGuid(); var second = Guid.NewGuid();
    using var transport = new FakeTransport([template with {ContainerId=first}, template with {ContainerId=first,Path="a2"}, template with {ContainerId=second,Path="b"}]);
    var groups = new MchoseDeviceService(transport).ScanGroups();
    Equal(2, groups.Count); Equal(3,groups.Sum(g=>g.Collections.Count));Equal(2,groups.Select(g=>g.Id).Distinct().Count());
    Equal(false, template.PhysicalId == (template with {Path="different"}).PhysicalId);
});
Test("battery percentage follows official offset",()=>
{
    using var fake = new FakeSession(KeyboardModelSchemas.Get("K87")); using var session = new KeyboardSession(fake);
    Equal<int?>(50, new KeyboardSettings(new KeyboardSettingsSession(session,KeyboardModelSchemas.Get("K87"))).ReadBattery());
});
Test("Z75 exposes all official latency choices",()=>
{
    var model=KeyboardModelSchemas.Get("Z75");using var fake=new FakeSession(model);using var session=new KeyboardSession(fake);
    var settings=new KeyboardSettings(new KeyboardSettingsSession(session,model));var p=settings.ReadPerformance();
    foreach(var latency in new[]{LatencyMode.Standard,LatencyMode.LowLatencyStandard,LatencyMode.Speed})
    { settings.WritePerformance(p with{Latency=latency});Equal(latency,settings.ReadPerformance().Latency); }
});
Test("wireless requests, layers and checksums match official bytes",()=>
{
    var request=KeyboardWirelessProtocol.ReadRequest(0x41,2);
    Bytes([0x41,1,0,0x20],request[..4]);Equal((byte)0x75,request[^1]);
    var macro=KeyboardWirelessProtocol.ReadRequest(0x43);Bytes([0x43,1,0,0x10,3,2],macro[..6]);
    Equal("K87",KeyboardWirelessProtocol.Identify([3,0,0,0,1,0x35]));Throws(()=>KeyboardWirelessProtocol.Identify([0,0,0,0,0,0]));
});
Test("wireless writes preserve official last-packet and macro page tags",()=>
{
    var key=KeyboardWirelessProtocol.WritePackets(1,[1,2,0,0],1).Single();Bytes([1,1,0,28,1,2,0,0],key[..8]);
    var perf=KeyboardWirelessProtocol.WritePackets(4,new byte[16]);Equal((byte)2,perf[^1][3]);
    var diy=KeyboardWirelessProtocol.WritePackets(2,new byte[16]);Equal((byte)14,diy[^1][3]);
    var macro=KeyboardWirelessProtocol.WritePackets(3,new byte[530]);Equal((byte)28,macro[37][3]);
    Throws(()=>KeyboardWirelessProtocol.WritePackets(3,new byte[3571]));
    Equal(true,macro.All(p=>p[^1]==KeyboardWirelessProtocol.Checksum(p.AsSpan(0,18))));
});
Test("K87 wireless end-to-end: reversed packets, performance, lighting, keys, DIY, profile, macro",()=>
{
    using var fake=new FakeWirelessSession();using var session=new KeyboardSession(fake);
    Equal(true,session.IsWireless);Equal<byte?>(19,session.ResolveReportId());Equal("K87",session.ResolveWirelessModelKey());
    var settings=new KeyboardSettings(new KeyboardSettingsSession(session,KeyboardModelSchemas.Get("K87")));
    var p=settings.ReadPerformance();settings.WritePerformance(p with{WinKeyLocked=true});Equal(true,settings.ReadPerformance().WinKeyLocked);
    var lighting=settings.ReadLighting();settings.WriteLighting(lighting with{Red=12,Green=34,Blue=56});Equal((byte)12,settings.ReadLighting().Red);
    settings.RemapKey("Fn","Caps","F1");Bytes([0,0,0,58],settings.ReadKeyMappings("Fn").First(m=>m.Key=="Caps").Value);
    var diy=settings.ReadDiyLight();diy[0]=(4,5,6);settings.WriteDiyLight(diy);Equal((byte)4,settings.ReadDiyLight()[0].R);
    var backup=settings.BackupProfile();settings.ValidateProfile(backup);
    settings.WriteMacros([new("A",MacroTrigger.Once,[new("a",true),new("a",false)])]);
    settings.BindMacro("Default","Caps",MacroTrigger.Once,0);Bytes([3,1,1,0],settings.ReadKeyMappings("Default").First(m=>m.Key=="Caps").Value);
    Equal(0,fake.FeatureCalls);Equal(true,fake.OutputPackets.All(p=>p.Length==19));
});
Test("incomplete wireless performance is rejected before configuration writes",()=>
{
    using var fake=new FakeWirelessSession { TruncatePerformance=true };using var session=new KeyboardSession(fake);
    var settings=new KeyboardSettings(new KeyboardSettingsSession(session,KeyboardModelSchemas.Get("K87")));
    Throws(()=>settings.ReadPerformance());Equal(0,fake.Writes);
});
Test("wireless model signature prevents using a wrong field layout",()=>
{
    using var fake=new FakeWirelessSession();using var session=new KeyboardSession(fake);
    Throws(()=>new KeyboardSettingsSession(session,KeyboardModelSchemas.Get("K99")));Equal(0,fake.Writes);
});
Test("closing a wireless session cancels an unanswered read",()=>
{
    using var fake=new FakeWirelessSession {SuppressReplies=true};var session=new KeyboardSession(fake);
    var task=Task.Run(()=>{try{session.ResolveReportId();throw new Exception("Read should be canceled");}catch(ObjectDisposedException){}});
    if(!fake.RequestStarted.Wait(2000))throw new Exception("Read never started");
    var timer=System.Diagnostics.Stopwatch.StartNew();session.Dispose();
    if(!task.Wait(2000)||timer.ElapsedMilliseconds>=2000)throw new Exception("Close blocked on wireless timeout");
});
Test("official group-B wired keyboards use report 0x09",()=>
{
    foreach(var name in new[]{"Z75","G98_Pro","G87_V2"})
    {
        using var fake=new FakeSession(KeyboardModelSchemas.Get(name));using var session=new KeyboardSession(fake);
        Equal<byte?>(9,session.ResolveReportId());
    }
});
Test("unrelated vendor HID cannot be opened as keyboard or audio",()=>
{
    var device=new HidDeviceInfo{Path="unrelated",VendorId=0x1234,ProductId=0x9876,ProductName="Other gaming device",UsagePage=0xFF00,FeatureReportLength=520,OutputReportLength=64};
    Equal(false,KeyboardSession.LooksLikeKeyboardChannel(device));using var transport=new FakeTransport([device]);
    Equal(ChannelKind.Unknown,new MchoseDeviceService(transport).ScanGroups(includeAll:true).Single().Kind);
});
KeyboardProfile LocalProfile(string modelName = "K87")
{
    var model = KeyboardModelSchemas.Get(modelName);
    using var fake = new FakeSession(model);
    return new KeyboardProfile(1, modelName,
        PayloadSnapshot.From(fake.Values(model.ReadPerformance, [0, 0, 1, 0, 128, 0])),
        PayloadSnapshot.From(fake.Values(model.ReadLightColor, [0, 0, 1, 0, 0, 2])),
        PayloadSnapshot.From(fake.Values(model.ReadDiyLight, [0, 0, 1, 0, 122, 1])),
        Enumerable.Range(0, 3).Select(i => PayloadSnapshot.From(fake.Values(model.ReadKeySetting, [(byte)i, 0, 1, 0, 248, 1]))).ToArray());
}
void WithLibrary(Action<KeyboardProfileLibrary> action)
{
    var directory = Path.Combine(Path.GetTempPath(), "MchoseHub-profile-tests-" + Guid.NewGuid().ToString("N"));
    try { action(new KeyboardProfileLibrary(directory)); }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
Test("local profiles round-trip named settings with model isolation", () => WithLibrary(library =>
{
    var work = LocalProfile(); var gaming = LocalProfile(); gaming.Performance.Numbers["winKeySwitch"] = 1;
    library.Save("办公", work); library.Save("游戏", gaming); library.Save("办公", LocalProfile("K99"));
    Equal(2, library.List("K87").Count); Equal(1, library.List("K99").Count);
    Equal(0, library.Load("K87", "办公").Performance.Numbers["winKeySwitch"]);
    Equal(1, library.Load("K87", "游戏").Performance.Numbers["winKeySwitch"]);
    Equal("K99", library.Load("K99", "办公").Model);
    Equal(true, library.Delete("K87", "办公")); Equal(false, library.Delete("K87", "办公"));
    Equal(1, library.List("K87").Count); Equal(1, library.List("K99").Count);
}));
Test("local profile names cannot escape the model directory", () => WithLibrary(library =>
{
    var profile = LocalProfile();
    foreach (var name in new[] { "", " ", "..", "../outside", "..\\outside", "C:\\outside", "x:y", "CON", "NUL.json", "COM1", "trailing.", " trailing", "newline\n" })
    {
        Throws(() => library.Save(name, profile)); Throws(() => library.Load("K87", name)); Throws(() => library.Delete("K87", name));
    }
    Throws(() => library.List("../K87")); Equal(false, Directory.Exists(library.RootDirectory));
}));
Test("invalid profile replacement preserves the last complete file", () => WithLibrary(library =>
{
    library.Save("日常", LocalProfile());
    var invalid = LocalProfile(); invalid.Performance.Numbers["rateVal"] = 99;
    Throws(() => library.Save("日常", invalid));
    Equal(3, library.Load("K87", "日常").Performance.Numbers["rateVal"]);
    var replacement = LocalProfile(); replacement.Performance.Numbers["rateVal"] = 1;
    library.Save("日常", replacement);
    Equal(1, library.Load("K87", "日常").Performance.Numbers["rateVal"]);
    Equal(1, Directory.GetFiles(Path.Combine(library.RootDirectory, "K87")).Length);
}));
Test("profile loading rejects oversized, malformed and cross-model files", () => WithLibrary(library =>
{
    library.Save("valid", LocalProfile());
    var directory = Path.Combine(library.RootDirectory, "K87");
    File.WriteAllText(Path.Combine(directory, "oversized.json"), new string(' ', KeyboardProfileLibrary.MaxFileBytes + 1));
    File.WriteAllText(Path.Combine(directory, "broken.json"), "{\"Version\":");
    File.WriteAllText(Path.Combine(directory, "wrong-model.json"), System.Text.Json.JsonSerializer.Serialize(LocalProfile("K99")));
    File.WriteAllText(Path.Combine(directory, "null.json"), "null");
    foreach (var name in new[] { "oversized", "broken", "wrong-model", "null" }) Throws(() => library.Load("K87", name));
}));
Test("profile validator rejects null collections, mismatched scalar types and extra fields", () =>
{
    var model = KeyboardModelSchemas.Get("K87");
    Throws(() => KeyboardProfileValidator.Validate(LocalProfile() with { Performance = null! }, model));
    Throws(() => KeyboardProfileValidator.Validate(LocalProfile() with { KeyLayers = null! }, model));
    var profile = LocalProfile(); profile.Performance.Numbers.Remove("rateVal"); profile.Performance.Buffers["rateVal"] = [3];
    Throws(() => KeyboardProfileValidator.Validate(profile, model));
    profile = LocalProfile(); profile.Performance.Numbers["injected"] = 1;
    Throws(() => KeyboardProfileValidator.Validate(profile, model));
    profile = LocalProfile(); profile.KeyLayers[0].Tables["#keyList"][0] = null!;
    Throws(() => KeyboardProfileValidator.Validate(profile, model));
    profile = LocalProfile(); profile.Performance.Buffers["space0"] = [0, 0, 0, 0, 0, 0];
    Throws(() => KeyboardProfileValidator.Validate(profile, model));
});
Test("offline preview keeps Default, Fn and Fn2 mappings independent", () =>
{
    using var memory = new PreviewKeyboardSession(); using var session = new KeyboardSession(memory);
    var settings = new KeyboardSettings(new KeyboardSettingsSession(session, KeyboardModelSchemas.Get("K87")));
    foreach (var pair in new[] { ("Default", "F1"), ("Fn", "F2"), ("Fn2", "F3") }) settings.RemapKey(pair.Item1, "Caps", pair.Item2);
    byte code = 58;
    foreach (var layer in new[] { "Default", "Fn", "Fn2" }) Bytes([0, 0, 0, code++], settings.ReadKeyMappings(layer).First(k => k.Key == "Caps").Value);
    Equal("preview://K87", memory.Device.Path);
});
Test("offline preview supports lighting, profile and macro round-trips without physical transport", () =>
{
    using var memory = new PreviewKeyboardSession(); using var session = new KeyboardSession(memory);
    var settings = new KeyboardSettings(new KeyboardSettingsSession(session, KeyboardModelSchemas.Get("K87")));
    var performance = settings.ReadPerformance(); settings.WritePerformance(performance with { WinKeyLocked = true });
    Equal(true, settings.ReadPerformance().WinKeyLocked);
    var lighting = settings.ReadLighting(); settings.WriteLighting(lighting with { Red = 17, Green = 38, Blue = 59 });
    Equal((byte)17, settings.ReadLighting().Red);
    var backup = settings.BackupProfile(); settings.ValidateProfile(backup);
    settings.WritePerformance(performance with { WinKeyLocked = false });
    settings.RestoreProfile(backup); Equal(true, settings.ReadPerformance().WinKeyLocked);
    settings.WriteMacros([new("离线宏", MacroTrigger.Once, [new("a", true), new("a", false)])]);
    settings.BindMacro("Fn2", "Caps", MacroTrigger.Once, 0);
    Bytes([3, 1, 1, 0], settings.ReadKeyMappings("Fn2").First(k => k.Key == "Caps").Value);
});
Test("partial profile restore failure rereads the changed device before a later lighting edit", () =>
{
    var model = KeyboardModelSchemas.Get("K87"); using var fake = new FakeSession(model); using var session = new KeyboardSession(fake);
    var raw = new KeyboardSettingsSession(session, model); var settings = new KeyboardSettings(raw);
    var oldLighting = settings.ReadLighting(); settings.ReadDiyLight();
    var profile = settings.BackupProfile();
    profile.Lighting.Buffers["lightModeColor2"] = [17, 38, 59];
    fake.FailNextWriteCommand = 6; // Lighting succeeds, DIY fails: a real partial restore.
    Throws(() => settings.RestoreProfile(profile));
    Bytes([17, 38, 59], raw.ReadLightColor().GetBuffer("lightModeColor2"));
    settings.WriteLighting(oldLighting with { Red = 77, Green = 88, Blue = 99 });
    var result = raw.ReadLightColor();
    Bytes([77, 88, 99], result.GetBuffer("lightModeColor1"));
    Bytes([17, 38, 59], result.GetBuffer("lightModeColor2"));
});
Test("failed lighting write is not silently replayed from an unverified cache", () =>
{
    var model = KeyboardModelSchemas.Get("K87"); using var fake = new FakeSession(model); using var session = new KeyboardSession(fake);
    var raw = new KeyboardSettingsSession(session, model); var settings = new KeyboardSettings(raw);
    var original = settings.ReadLighting();
    fake.FailNextWriteCommand = 10;
    Throws(() => settings.WriteLighting(original with { Red = 123, Green = 45, Blue = 67 }));
    settings.WriteLightingWithCurrentColor();
    Bytes([original.Red, original.Green, original.Blue], raw.ReadLightColor().GetBuffer("lightModeColor1"));
});
Test("failed lighting refresh cannot authorize a later whole-table write from stale cache", () =>
{
    var model = KeyboardModelSchemas.Get("K87"); using var fake = new FakeSession(model); using var session = new KeyboardSession(fake);
    var raw = new KeyboardSettingsSession(session, model); var settings = new KeyboardSettings(raw);
    var original = settings.ReadLighting();
    var externalChange = raw.ReadLightColor(); externalChange.SetBuffer("lightModeColor2", [11, 22, 33]); raw.WriteLightColor(externalChange);
    fake.FailNextReadCommand = 0x8A;
    Throws(() => settings.ReadLighting());
    settings.WriteLighting(original with { Red = 44, Green = 55, Blue = 66 });
    var result = raw.ReadLightColor();
    Bytes([44, 55, 66], result.GetBuffer("lightModeColor1"));
    Bytes([11, 22, 33], result.GetBuffer("lightModeColor2"));
});
Test("macro recording ignores repeats and releases held modifiers when focus is lost", () =>
{
    var recording = new MacroRecordingBuffer();
    Equal(true, recording.Record("ControlLeft", true, 0));
    Equal(true, recording.Record("a", true, 10));
    Equal(false, recording.Record("a", true, 15));
    Equal(true, recording.Record("a", false, 30));
    recording.ReleaseHeld(50);
    Equal(4, recording.Steps.Count);
    Equal(10, recording.Steps[0].DelayMs); Equal(20, recording.Steps[1].DelayMs);
    Equal(20, recording.Steps[2].DelayMs); Equal(0, recording.Steps[3].DelayMs);
    Equal("ControlLeft", recording.Steps[^1].Key); Equal(false, recording.Steps[^1].Down);
    recording.ReleaseHeld(60); Equal(4, recording.Steps.Count);
});
Test("macro recorder reserves release events at the hardware event limit", () =>
{
    var recording = new MacroRecordingBuffer();
    for (int i = 0; i < 7499; i++) { recording.Record("a", true, i * 2); recording.Record("a", false, i * 2 + 1); }
    Equal(14998, recording.Steps.Count);
    Equal(true, recording.Record("ControlLeft", true, 15000));
    Equal(false, recording.Record("b", true, 15001)); Equal(true, recording.CapacityReached);
    recording.ReleaseHeld(15002); Equal(MacroRecordingBuffer.MaxEvents, recording.Steps.Count);
    var held = new HashSet<string>();
    foreach (var step in recording.Steps)
        Equal(true, step.Down ? held.Add(step.Key) : held.Remove(step.Key));
    Equal(0, held.Count);
    Equal(MacroRecordingBuffer.MaxEvents * 4, KeyboardMacroCodec.EncodeEvents(recording.Steps).Length);
});
Test("macro recording rejects invalid input and clamps long delays without stale state after clear", () =>
{
    var recording = new MacroRecordingBuffer();
    Equal(false, recording.Record("a", false, 0)); Equal(0, recording.Steps.Count);
    Throws(() => recording.Record("unknown", true, 0));
    recording.Record("a", true, 0); recording.Record("a", false, 70000);
    Equal(60000, recording.Steps[0].DelayMs);
    recording.Clear(); Equal(0, recording.Steps.Count); Equal(false, recording.CapacityReached);
    Equal(true, recording.Record("a", true, 0)); recording.ReleaseHeld(1); Equal(2, recording.Steps.Count);
});
Test("full profile restore verifies inactive lighting slots and every independent key layer", () =>
{
    var model = KeyboardModelSchemas.Get("K87"); using var fake = new FakeSession(model); using var session = new KeyboardSession(fake);
    var settings = new KeyboardSettingsSession(session, model); var profile = settings.BackupProfile();
    profile.Performance.Numbers["brightness2"] = 42;
    profile.Performance.Buffers["space11"][3] = 0x87;
    for (int layer = 0; layer < 3; layer++) profile.KeyLayers[layer].Tables["#keyList"][70] = [0, 0, 0, (byte)(0x87 + layer)];
    settings.RestoreProfile(profile); Equal(6, fake.Writes);
    var actual = settings.BackupProfile(); Equal(42, actual.Performance.Numbers["brightness2"]);
    Bytes(profile.Performance.Buffers["space11"], actual.Performance.Buffers["space11"]);
    for (int layer = 0; layer < 3; layer++) Bytes(profile.KeyLayers[layer].Tables["#keyList"][70], actual.KeyLayers[layer].Tables["#keyList"][70]);
});
Test("full restore reports an ignored inactive light field without another write", () =>
{
    var model = KeyboardModelSchemas.Get("K87"); using var fake = new FakeSession(model) { IgnorePerformanceField = "brightness2" };
    using var session = new KeyboardSession(fake); var settings = new KeyboardSettingsSession(session, model);
    var profile = settings.BackupProfile(); profile.Performance.Numbers["brightness2"] = 42;
    InvalidDataException? failure = null;
    try { settings.RestoreProfile(profile); } catch (InvalidDataException ex) { failure = ex; }
    Equal(true, failure?.Message.Contains("Performance.brightness2", StringComparison.Ordinal) == true);
    Equal(6, fake.Writes); Equal(100, settings.ReadPerformance().GetNumber("brightness2"));
});
Console.WriteLine($"{passed} checks passed; no physical device writes.");

sealed class FakeTransport(IReadOnlyList<HidDeviceInfo> devices) : IHidTransport
{
    public IReadOnlyList<HidDeviceInfo> Enumerate(IReadOnlyCollection<ushort>? vendorIds=null)=>devices;
    public IHidSession Open(HidDeviceInfo device)=>throw new NotSupportedException();
    public void Dispose(){}
}

sealed class FakeWirelessSession : IHidSession
{
    private readonly Dictionary<byte,byte[]> values=[];
    private readonly Dictionary<byte,byte[]> layers=[];
    private readonly Dictionary<byte,byte[]> chunks=[];
    public bool TruncatePerformance {get;init;}
    public bool SuppressReplies {get;init;}
    public ManualResetEventSlim RequestStarted {get;}=new(false);
    public int FeatureCalls {get;private set;}
    public int Writes {get;private set;}
    public List<byte[]> OutputPackets {get;}=[];
    public HidDeviceInfo Device {get;}=new(){Path="wireless-fake",VendorId=0xA8A5,ProductId=0x2255,ProductName="MCHOSE 2.4G Wireless",UsagePage=0xFF00,InputReportLength=20,OutputReportLength=20};
    public bool IsOpen {get;private set;}=true;
    public event EventHandler<HidInputReport>? InputReportReceived;
    public FakeWirelessSession()
    {
        var model=KeyboardModelSchemas.Get("K87");using var fixture=new FakeSession(model);
        foreach(var (code,schema,header) in new[]{((byte)0x44,model.ReadPerformance,new byte[]{0,0,1,0,128,0}),((byte)0x49,model.ReadLightColor,new byte[]{0,0,1,0,0,2}),((byte)0x42,model.ReadDiyLight,new byte[]{0,0,1,0,122,1})})
            values[code]=KeyboardPayloadCodec.Encode(new CommandSchema{Command="fixture",Fields=schema.Fields},fixture.Values(schema,header),0)[7..];
        foreach(byte layer in new byte[]{0,1,2})layers[layer]=new byte[504];
        values[5]=[3,0,0,0,1,0x35];values[0x4A]=[50,0];values[0x43]=[0,0,1,0,4,0,0,0,0,0];
    }
    public void SendOutput(byte reportId,byte[] payload)
    {
        if(reportId!=19||payload.Length!=19||payload[^1]!=KeyboardWirelessProtocol.Checksum(payload.AsSpan(0,18)))throw new InvalidDataException("Bad output packet");
        OutputPackets.Add(payload.ToArray());byte code=payload[0];
        RequestStarted.Set();if(SuppressReplies)return;
        if(code is 5 or >=0x40)
        {
            var data=code==0x41?layers[(byte)(payload[3]>>4)]:values[code];
            if(code==0x44&&TruncatePerformance)data=data[..5];
            int total=(data.Length+13)/14;
            for(int i=total-1;i>=0;i--)
            {
                var packet=new byte[19];packet[0]=code;packet[1]=(byte)total;packet[2]=(byte)i;packet[3]=14;
                data.AsSpan(i*14,Math.Min(14,data.Length-i*14)).CopyTo(packet.AsSpan(4));
                packet[^1]=KeyboardWirelessProtocol.Checksum(packet.AsSpan(0,18));InputReportReceived?.Invoke(this,new HidInputReport(19,packet));
                // Duplicate delivery is possible on a retry; matching duplicates must be harmless.
                InputReportReceived?.Invoke(this,new HidInputReport(19,packet));
            }
            return;
        }
        if(payload[2]==0)chunks.Clear();chunks[payload[2]]=payload[4..18];
        if(chunks.Count==payload[1])
        {
            Writes++;var data=Enumerable.Range(0,chunks.Count).SelectMany(i=>chunks[(byte)i]).ToArray();
            if(code==1)layers[(byte)(payload[3]>>4)]=data;
            else if(code==3)values[0x43]=[0,0,1,0,4,0,..data[..4]];
            else values[code switch{2=>(byte)0x42,4=>(byte)0x44,9=>(byte)0x49,_=>throw new InvalidDataException()}]=data;
        }
        var ack=new byte[19];ack[0]=code;ack[2]=payload[2];InputReportReceived?.Invoke(this,new HidInputReport(19,ack));
    }
    public void SendFeature(byte reportId,byte[] payload){FeatureCalls++;throw new NotSupportedException();}
    public byte[] GetFeature(byte reportId,int length){FeatureCalls++;throw new NotSupportedException();}
    public void Dispose(){IsOpen=false;RequestStarted.Dispose();}
}

sealed class FakeSession(KeyboardModelSchema model) : IHidSession
{
    private byte[] last = [];
    private readonly Dictionary<byte,byte[]> stored = [];
    private readonly Dictionary<byte,byte[]> storedKeys = [];
    public int Sends {get;private set;}
    public int Writes {get;private set;}
    public int ApplyAfterWrites {get;init;}=1;
    public byte? FailNextWriteCommand {get;set;}
    public byte? FailNextReadCommand {get;set;}
    public string? IgnorePerformanceField {get;init;}
    public IReadOnlyDictionary<int,byte[]> InitialKeyValues {get;init;}=new Dictionary<int,byte[]>();
    public HidDeviceInfo Device {get;}=new(){Path="fake",VendorId=0x05AC,ProductId=0x024F,ProductName="MCHOSE "+model.Model+" Gaming Keyboard",UsagePage=0xFF00,FeatureReportLength=520};
    public bool IsOpen {get;private set;}=true;
    public event EventHandler<HidInputReport>? InputReportReceived {add{} remove{}}
    public void Dispose()=>IsOpen=false;
    public void SendOutput(byte reportId,byte[] payload)=>throw new NotSupportedException();
    public void SendFeature(byte reportId,byte[] payload)
    {
        Sends++;last=payload.ToArray();if(payload[0]>=128)return;Writes++;
        if (FailNextWriteCommand == payload[0]) { FailNextWriteCommand = null; throw new InvalidDataException("Injected configuration write failure."); }
        if(Writes>=ApplyAfterWrites)
        {
            byte[] report = [reportId, ..payload];
            if (payload[0] == 4 && IgnorePerformanceField is { } ignored)
            {
                var old = stored.TryGetValue(0x84, out var previous)
                    ? KeyboardPayloadCodec.Decode(model.ReadPerformance, previous)
                    : Values(model.ReadPerformance, payload[1..7]);
                var incoming = KeyboardPayloadCodec.Decode(model.ReadPerformance, report);
                incoming.SetNumber(ignored, old.GetNumber(ignored));
                var encoded = KeyboardPayloadCodec.Encode(model.WritePerformance, incoming, 4);
                report = new byte[520]; report[0] = reportId; encoded.CopyTo(report, 1);
            }
            if (payload[0] == 3) storedKeys[payload[1]] = report;
            else stored[(byte)(payload[0]|128)] = report;
        }
    }
    public byte[] GetFeature(byte reportId,int length)
    {
        if (FailNextReadCommand == last[0]) { FailNextReadCommand = null; throw new InvalidDataException("Injected configuration read failure."); }
        byte[]? saved;
        bool found = last[0] == 0x83 ? storedKeys.TryGetValue(last[1], out saved) : stored.TryGetValue(last[0], out saved);
        if (found) { var copy = saved!.ToArray(); last.AsSpan(0, 7).CopyTo(copy.AsSpan(1)); return copy; }
        var schema=last[0] switch {0x84=>model.ReadPerformance,0x8A=>model.ReadLightColor,0x86=>model.ReadDiyLight,0x83=>model.ReadKeySetting,_=>null};
        if(schema is null){var report=new byte[520];report[0]=reportId;last.AsSpan(0,7).CopyTo(report.AsSpan(1));report[8]=50;return report;}
        var values=Values(schema,last[1..7]);var writeLike=new CommandSchema{Command="fixture",Fields=schema.Fields};
        var payload=KeyboardPayloadCodec.Encode(writeLike,values,last[0]);var response=new byte[520];response[0]=reportId;payload.CopyTo(response,1);return response;
    }
    public PayloadValues Values(CommandSchema schema,byte[] header)
    {
        var v=new PayloadValues();foreach(var field in schema.Fields)
        {
            switch(field.Kind)
            {
                case FieldKind.Uint8: case FieldKind.Bit4:
                    v.SetNumber(field.Name,field.Name switch{"rateVal"=>3,"latencySwitch"=>2,"lightMode"=>1,_=>field.Name.StartsWith("brightness",StringComparison.Ordinal)?100:0});break;
                case FieldKind.FixedArray:v.SetBuffer(field.Name,field.Name=="space0"?header:new byte[field.Length]);break;
                case FieldKind.KeyTable:v.SetTable(field.Name,model.KeyList.Select((_,i)=>InitialKeyValues.TryGetValue(i,out var bytes)?bytes.ToArray():new byte[4]).ToList());break;
            }
        }
        return v;
    }
}
