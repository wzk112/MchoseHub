using System.Text;
using System.Text.Json;
using MchoseHub.Core.Protocol;

namespace MchoseHub.Core.Services;

public sealed record KeyboardProfileEntry(string Name, string Model, DateTimeOffset UpdatedAtUtc);

/// <summary>Named local profiles, separated by model. Saving never changes hardware.</summary>
public sealed class KeyboardProfileLibrary
{
    public const int MaxFileBytes = 2_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly char[] InvalidNameCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    public string RootDirectory { get; }

    public KeyboardProfileLibrary(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public KeyboardProfileEntry Save(string name, KeyboardProfile profile)
    {
        if (profile is null) throw new InvalidDataException("配置为空。");
        var model = KeyboardModelSchemas.Get(profile.Model);
        KeyboardProfileValidator.Validate(profile, model);
        var path = ProfilePath(model.Model, name);
        byte[] contents = JsonSerializer.SerializeToUtf8Bytes(profile, JsonOptions);
        if (contents.Length > MaxFileBytes) throw new InvalidDataException("配置文件超过 2 MB。");

        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        EnsureRegularPath(directory);
        EnsureRegularPath(path);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            // Same-directory rename commits a complete file; an interrupted write leaves the old profile intact.
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return new KeyboardProfileEntry(Path.GetFileNameWithoutExtension(path), model.Model, File.GetLastWriteTimeUtc(path));
    }

    /// <summary>Lists named files. Load performs the complete content validation before use.</summary>
    public IReadOnlyList<KeyboardProfileEntry> List(string model)
    {
        string canonical = KeyboardModelSchemas.Get(model).Model;
        string directory = ModelDirectory(canonical);
        if (!Directory.Exists(directory)) return [];
        EnsureRegularPath(directory);
        var entries = new List<KeyboardProfileEntry>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            string name = Path.GetFileNameWithoutExtension(path);
            try { ValidateName(name); }
            catch (ArgumentException) { continue; }
            entries.Add(new KeyboardProfileEntry(name, canonical, File.GetLastWriteTimeUtc(path)));
        }
        return entries.OrderByDescending(p => p.UpdatedAtUtc).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public KeyboardProfile Load(string model, string name)
    {
        var schema = KeyboardModelSchemas.Get(model);
        var path = ProfilePath(schema.Model, name);
        EnsureRegularPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileBytes) throw new InvalidDataException("配置文件超过 2 MB。");
        KeyboardProfile profile;
        try
        {
            profile = JsonSerializer.Deserialize<KeyboardProfile>(stream, JsonOptions) ?? throw new InvalidDataException("配置文件为空。");
        }
        catch (JsonException ex) { throw new InvalidDataException("配置文件不是有效的配置 JSON。", ex); }
        KeyboardProfileValidator.Validate(profile, schema);
        return profile;
    }

    public bool Delete(string model, string name)
    {
        var path = ProfilePath(KeyboardModelSchemas.Get(model).Model, name);
        EnsureRegularPath(path);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private string ModelDirectory(string canonicalModel)
    {
        EnsureRegularPath(RootDirectory);
        var directory = Path.Combine(RootDirectory, canonicalModel);
        EnsureRegularPath(directory);
        return directory;
    }

    private string ProfilePath(string canonicalModel, string name)
    {
        ValidateName(name);
        return Path.Combine(ModelDirectory(canonicalModel), name.Normalize(NormalizationForm.FormC) + ".json");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name != name.Trim() || name.EndsWith('.') || name is "." or ".."
            || name.IndexOfAny(InvalidNameCharacters) >= 0 || name.Any(char.IsControl))
            throw new ArgumentException("配置名称应为 1–64 个字符，不能含路径符号、首尾空格或末尾句点。", nameof(name));
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9'))
            throw new ArgumentException("请使用其他配置名称。", nameof(name));
    }

    private static void EnsureRegularPath(string path)
    {
        // Local links/junctions must not redirect a profile operation outside the chosen library.
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("配置库路径不能是链接或目录联接。");
    }
}
