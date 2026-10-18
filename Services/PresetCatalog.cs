using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HandBrakeTreemap.Services;

/// <summary>One preset as shown in the combo box.</summary>
public sealed class PresetItem
{
    /// <summary>Name passed to <c>--preset</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Folder/category ("General", "My Presets", …).</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>Preset file to pass via <c>--preset-import-file</c>; null for built-in presets.</summary>
    public string? ImportFile { get; init; }

    public bool IsCustom => ImportFile != null;

    /// <summary>Windows user the preset file belongs to, when it's not the current user.</summary>
    public string? OtherUser
    {
        get
        {
            if (ImportFile == null) return null;
            var parts = ImportFile.Split('\\', '/');
            for (int i = 0; i < parts.Length - 1; i++)
                if (parts[i].Equals("Users", StringComparison.OrdinalIgnoreCase))
                    return parts[i + 1].Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase) ? null : parts[i + 1];
            return null;
        }
    }

    public string Display
    {
        get
        {
            if (!IsCustom)
                return string.IsNullOrEmpty(Category) ? Name : $"{Name}   ({Category})";
            var extra = "eigen";
            if (OtherUser is { } u) extra += " • gebruiker " + u;
            if (!string.IsNullOrEmpty(Category)) extra += " • " + Category;
            return $"★ {Name}   ({extra})";
        }
    }

    public override string ToString() => Display;
}

/// <summary>
/// Collects presets: HandBrake's built-in ones (from <c>HandBrakeCLI --preset-list</c>) and the
/// user's own custom presets from the HandBrake GUI preset store and/or a chosen .json file.
/// </summary>
public static class PresetCatalog
{
    public const string Default = "H.265 MKV 1080p30";

    /// <summary>Fallback when HandBrakeCLI can't be queried.</summary>
    public static readonly IReadOnlyList<string> FallbackBuiltIn = new List<string>
    {
        "H.265 MKV 2160p60", "H.265 MKV 1080p30", "H.265 MKV 720p30", "H.265 MKV 480p30",
        "Fast 1080p30", "Fast 720p30", "HQ 1080p30 Surround", "HQ 720p30 Surround",
        "Super HQ 1080p30 Surround", "Super HQ 720p30 Surround", "Very Fast 1080p30", "Very Fast 720p30",
        "Production Max", "Production Standard",
    };

    /// <summary>Where the HandBrake GUI keeps its presets (custom presets live here).</summary>
    public static IEnumerable<string> GuiPresetFileCandidates(string? handBrakeCliPath)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(appData, "HandBrake", "presets.json");
            yield return Path.Combine(appData, "HandBrake Nightly", "presets.json");
            yield return Path.Combine(local, "HandBrake", "presets.json");

            // Presets made under another Windows account on this pc.
            yield return @"C:\Users\Backup\AppData\Roaming\HandBrake\presets.json";
            foreach (var other in OtherProfiles(home))
            {
                yield return Path.Combine(other, "AppData", "Roaming", "HandBrake", "presets.json");
                yield return Path.Combine(other, "AppData", "Roaming", "HandBrake Nightly", "presets.json");
            }

            // Portable installs keep a "storage" folder next to HandBrake.exe.
            if (!string.IsNullOrWhiteSpace(handBrakeCliPath) && Path.IsPathRooted(handBrakeCliPath))
            {
                var dir = Path.GetDirectoryName(handBrakeCliPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    yield return Path.Combine(dir, "storage", "presets.json");
                    yield return Path.Combine(dir, "presets.json");
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library", "Application Support", "HandBrake", "UserPresets.json");
        }
        else
        {
            yield return Path.Combine(home, ".config", "ghb", "presets.json");
            yield return Path.Combine(home, ".var", "app", "fr.handbrake.ghb", "config", "ghb", "presets.json");
        }
    }

    /// <summary>Other user profile folders next to the current one (C:\Users\*).</summary>
    private static IEnumerable<string> OtherProfiles(string currentHome)
    {
        var list = new List<string>();
        try
        {
            var usersDir = Path.GetDirectoryName(currentHome.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(usersDir) || !Directory.Exists(usersDir)) return list;
            foreach (var d in Directory.EnumerateDirectories(usersDir))
            {
                var name = Path.GetFileName(d);
                if (string.Equals(d.TrimEnd('\\'), currentHome.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                if (name is "Public" or "Default" or "Default User" or "All Users") continue;
                list.Add(d);
            }
        }
        catch { /* no access to the Users folder */ }
        return list;
    }

    /// <summary>"bestaat" / "niet gevonden" / "geen toegang" — for the log.</summary>
    public static string Describe(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                // File.Exists also returns false when we may not look inside the folder.
                var dir = Path.GetDirectoryName(file);
                try { if (dir != null && Directory.Exists(dir)) Directory.EnumerateFiles(dir).GetEnumerator().MoveNext(); }
                catch (UnauthorizedAccessException) { return "geen toegang"; }
                return "niet gevonden";
            }
            using var _ = File.OpenRead(file);
            return "gevonden";
        }
        catch (UnauthorizedAccessException) { return "geen toegang"; }
        catch (Exception ex) { return "fout: " + ex.Message; }
    }

    /// <summary>Built-in presets as reported by HandBrakeCLI (falls back to a fixed list).</summary>
    public static async Task<List<PresetItem>> LoadBuiltInAsync(HandBrakeRunner runner, CancellationToken token)
    {
        var text = await runner.GetPresetListAsync(token);
        var list = text == null ? new List<PresetItem>() : ParsePresetList(text);
        if (list.Count == 0)
            list = FallbackBuiltIn.Select(n => new PresetItem { Name = n }).ToList();
        return list;
    }

    /// <summary>
    /// Parse <c>--preset-list</c> output:
    /// <code>
    /// General/
    ///     Very Fast 1080p30
    ///         Small H.264 video (up to 1080p30) ...
    /// </code>
    /// Category lines end with "/", preset names are indented 4 spaces, descriptions 8.
    /// </summary>
    public static List<PresetItem> ParsePresetList(string text)
    {
        var result = new List<PresetItem>();
        string category = string.Empty;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = raw.Replace("\t", "    ");
            int indent = line.Length - line.TrimStart(' ').Length;
            var trimmed = line.Trim();

            if (trimmed.StartsWith("[") || trimmed.StartsWith("HandBrake ")) continue; // log noise

            if (indent == 0 && trimmed.EndsWith("/"))
                category = trimmed.TrimEnd('/');
            else if (indent >= 2 && indent <= 5 && !string.IsNullOrEmpty(category))
                result.Add(new PresetItem { Name = trimmed, Category = category });
        }
        return result;
    }

    /// <summary>Custom presets from a HandBrake preset .json (GUI store or an exported file).</summary>
    public static List<PresetItem> LoadCustom(string file)
    {
        var result = new List<PresetItem>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("PresetList", out var listEl))
                Walk(listEl, string.Empty, file, result);
            else if (root.ValueKind == JsonValueKind.Array)
                Walk(root, string.Empty, file, result);
            else if (root.ValueKind == JsonValueKind.Object)
                AddPreset(root, string.Empty, file, result); // single preset object
        }
        catch (Exception ex)
        {
            Log.Warn($"Presetbestand niet leesbaar: {file} ({ex.Message})");
        }
        return result;
    }

    private static void Walk(JsonElement list, string category, string file, List<PresetItem> result)
    {
        if (list.ValueKind != JsonValueKind.Array) return;
        foreach (var el in list.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;

            // A folder has "Folder": true. Note: real presets also carry an (empty)
            // "ChildrenArray": [] in the HandBrake GUI store, so that alone doesn't make a folder.
            bool hasChildren = el.TryGetProperty("ChildrenArray", out var ch)
                               && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0;
            bool isFolder = (el.TryGetProperty("Folder", out var f) && f.ValueKind == JsonValueKind.True) || hasChildren;
            if (isFolder)
            {
                var name = GetString(el, "PresetName") ?? category;
                if (el.TryGetProperty("ChildrenArray", out var children))
                    Walk(children, name, file, result);
                continue;
            }
            AddPreset(el, category, file, result);
        }
    }

    private static void AddPreset(JsonElement el, string category, string file, List<PresetItem> result)
    {
        var name = GetString(el, "PresetName");
        if (string.IsNullOrWhiteSpace(name)) return;

        // Type 0 = official/built-in, 1 = custom. Built-ins are already in the list.
        if (el.TryGetProperty("Type", out var t) && t.ValueKind == JsonValueKind.Number && t.GetInt32() == 0)
            return;

        result.Add(new PresetItem { Name = name, Category = category, ImportFile = file });
    }

    private static string? GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Custom presets only (GUI stores + optional extra file); built-ins only when none are found.</summary>
    public static async Task<(List<PresetItem> presets, List<string> sources)> LoadAllAsync(
        HandBrakeRunner runner, string? extraFile, CancellationToken token)
    {
        var sources = new List<string>();
        var custom = new List<PresetItem>();

        var files = GuiPresetFileCandidates(runner.ExecutablePath).ToList();
        if (!string.IsNullOrWhiteSpace(extraFile)) files.Insert(0, extraFile!);

        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(file)) continue;
            var found = await Task.Run(() => LoadCustom(file), token);
            Log.Info($"  {found.Count} eigen preset(s) in {file}");
            if (found.Count == 0) continue;
            sources.Add(file);
            foreach (var p in found)
                if (!custom.Any(c => string.Equals(c.Name, p.Name, StringComparison.OrdinalIgnoreCase)))
                    custom.Add(p);
        }

        // Only the custom presets are shown. Built-ins are only a fallback so the tool stays
        // usable when no custom preset file can be found/read.
        if (custom.Count > 0)
            return (custom, sources);

        Log.Warn("Geen eigen presets gevonden — tijdelijk de ingebouwde HandBrake-presets getoond.");
        return (await LoadBuiltInAsync(runner, token), sources);
    }
}
