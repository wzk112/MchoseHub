using System.Text.Json;
using MchoseHub.Core.Protocol;
using MchoseHub.Core.Services;

// Synthetic, deliberately nonzero reserved bytes. Never reads or writes hardware.
var model = KeyboardModelSchemas.Get("K87");
var options = new JsonSerializerOptions { WriteIndented = true };
if (args.Length == 2 && args[0] == "--validate")
{
    var candidate = JsonSerializer.Deserialize<KeyboardProfile>(File.ReadAllBytes(args[1]))!;
    KeyboardProfileValidator.Validate(candidate, model);
    var canonical = CreateProfile();
    if (JsonSerializer.Serialize(candidate, options) != JsonSerializer.Serialize(canonical, options))
    {
        // Dictionary ordering is not part of the format.
        bool Equal(JsonElement a, JsonElement b) => a.ValueKind == b.ValueKind && (a.ValueKind switch
        {
            JsonValueKind.Object => a.EnumerateObject().Count() == b.EnumerateObject().Count()
                && a.EnumerateObject().All(p => b.TryGetProperty(p.Name, out var value) && Equal(p.Value, value)),
            JsonValueKind.Array => a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(p => Equal(p.First,p.Second)),
            _ => a.ToString() == b.ToString()
        });
        if (!Equal(JsonSerializer.SerializeToElement(candidate), JsonSerializer.SerializeToElement(canonical)))
            throw new InvalidDataException("Swift roundtrip changed profile content.");
    }
    Console.WriteLine("Swift -> C# profile validated; every field matches the C# fixture.");
    return;
}
if (args.Length != 1) throw new ArgumentException("Pass output directory, or --validate <Swift roundtrip file>.");
Directory.CreateDirectory(args[0]);
var profile = CreateProfile();
KeyboardProfileValidator.Validate(profile, model);
File.WriteAllText(Path.Combine(args[0], "profile.json"), JsonSerializer.Serialize(profile, options) + "\n");
var specs = new[] { model.ReadPerformance, model.ReadLightColor, model.ReadDiyLight, model.ReadKeySetting, model.ReadKeySetting, model.ReadKeySetting };
var values = new[] { profile.Performance, profile.Lighting, profile.Diy }.Concat(profile.KeyLayers).ToArray();
var names = new[] { "performance", "lighting", "diy", "defaultKeys", "fnKeys", "fn2Keys" };
byte[] commands = [0x84,0x8A,0x86,0x83,0x83,0x83];
var responses = new Dictionary<string,byte[]>();
for (int i = 0; i < specs.Length; i++)
{
    var encoded = KeyboardPayloadCodec.Encode(specs[i], values[i].ToValues(), commands[i]);
    var response = new byte[520]; response[0]=6; encoded.CopyTo(response,1); responses[names[i]]=response;
}
var writes = new List<byte[]>();
var tail = new byte[22]; tail[^6]=0x5A; tail[^5]=0xA5;
writes.Add(KeyboardPayloadCodec.Encode(model.WriteLightColor,profile.Lighting.ToValues(),10,tail));
var diy=profile.Diy.ToValues();diy.SetBuffer("pre",model.DiyPre.ToArray());
writes.Add(KeyboardPayloadCodec.Encode(model.WriteDiyLight,diy,6));
foreach(var layer in profile.KeyLayers)
{
    var expanded=layer.ToValues();
    foreach(var (name, bytes) in model.KeyWriteCommon) expanded.SetBuffer(name,bytes);
    for(int i=0;i<model.KeyList.Count;i++) expanded.SetBuffer(model.KeyList[i].Name,layer.Tables["#keyList"][i]);
    writes.Add(KeyboardPayloadCodec.Encode(model.WriteKeySetting,expanded,3));
}
writes.Add(KeyboardPayloadCodec.Encode(model.WritePerformance,profile.Performance.ToValues(),4));
File.WriteAllText(Path.Combine(args[0],"wire.json"),JsonSerializer.Serialize(new { Responses=responses, Writes=writes },options)+"\n");
var allModels = new List<object>();
foreach (var name in KeyboardModelSchemas.Models.Order(StringComparer.Ordinal))
{
    model=KeyboardModelSchemas.Get(name);var p=CreateProfile();KeyboardProfileValidator.Validate(p,model);
    var plans=new List<byte[]>();var t=new byte[new[]{"G98_Pro","G98_V2","G87_V2","UT98","Z75","Z75S"}.Contains(name)?29:22];t[^6]=0x5A;t[^5]=0xA5;
    plans.Add(KeyboardPayloadCodec.Encode(model.WriteLightColor,p.Lighting.ToValues(),10,t));
    var d=p.Diy.ToValues();d.SetBuffer("pre",model.DiyPre.ToArray());plans.Add(KeyboardPayloadCodec.Encode(model.WriteDiyLight,d,6));
    foreach(var l in p.KeyLayers)
    {
        var v=l.ToValues();foreach(var (k,b) in model.KeyWriteCommon)v.SetBuffer(k,b);
        for(int i=0;i<model.KeyList.Count;i++)v.SetBuffer(model.KeyList[i].Name,l.Tables["#keyList"][i]);
        plans.Add(KeyboardPayloadCodec.Encode(model.WriteKeySetting,v,3));
    }
    plans.Add(KeyboardPayloadCodec.Encode(model.WritePerformance,p.Performance.ToValues(),4));
    allModels.Add(new { Profile=p,Writes=plans });
}
File.WriteAllText(Path.Combine(args[0],"all-models.json"),JsonSerializer.Serialize(allModels,options)+"\n");
var wireless=new List<object>();
foreach(byte c in new byte[]{1,2,3,4,6,9})foreach(byte layer in new byte[]{0,2})foreach(int length in new[]{1,14,15,29,513})
{
    var d=Enumerable.Range(0,length).Select(i=>(byte)(i%251)).ToArray();
    wireless.Add(new { Command=c,Layer=layer,Data=d,Packets=KeyboardWirelessProtocol.WritePackets(c,d,layer) });
}
var macros=new[]{new KeyboardMacro("测试 A",MacroTrigger.Once,[new("a",true,50),new("a",false,60000),new("ControlLeft",true,10),new("ControlLeft",false,0),new("Left",true,10,true),new("Left",false,20,true)])};
File.WriteAllText(Path.Combine(args[0],"extended.json"),JsonSerializer.Serialize(new { Wireless=wireless,Macros=macros,Bank=KeyboardMacroCodec.EncodeBank(macros),Pages=KeyboardMacroCodec.BuildPages(KeyboardMacroCodec.EncodeBank(macros)) },options)+"\n");
Console.WriteLine("Generated synthetic C# K87 profile, six responses and six restore payloads.");

KeyboardProfile CreateProfile() => new(1,model.Model, Snapshot(model.ReadPerformance,[0,0,1,0,128,0]),
    Snapshot(model.ReadLightColor,[0,0,1,0,0,2]),Snapshot(model.ReadDiyLight,[0,0,1,0,122,1]),
    Enumerable.Range(0,3).Select(i=>Snapshot(model.ReadKeySetting,[(byte)i,0,1,0,248,1])).ToArray());

PayloadSnapshot Snapshot(CommandSchema schema, byte[] header)
{
    var values=new PayloadValues();int seed=7;
    foreach(var field in schema.Fields)
    {
        switch(field.Kind)
        {
            case FieldKind.Uint8: case FieldKind.Bit4:
                values.SetNumber(field.Name,field.Name switch {"rateVal"=>3,"latencySwitch"=>2,"lightMode"=>1,"macSwitch"=>1,"winKeySwitch"=>1,"lightType"=>1,
                    _=>field.Name.StartsWith("brightness",StringComparison.Ordinal)?67:field.Kind==FieldKind.Bit4?seed++%15:seed++%255});break;
            case FieldKind.FixedArray:
                values.SetBuffer(field.Name,field.Name=="space0"?header:Enumerable.Range(0,field.Length).Select(i=>(byte)((i+seed++)%251)).ToArray());break;
            case FieldKind.KeyTable:
                values.SetTable(field.Name,model.KeyList.Select((_,i)=>new byte[]{0x12,(byte)i,header[0],0x87}).ToList());break;
        }
    }
    return PayloadSnapshot.From(values);
}
