using System;
using System.Globalization;

namespace HandBrakeTreemap.Services;

/// <summary>
/// Persists user settings in the registry: HKEY_CURRENT_USER\Software\HandBrakeTreemap.
/// On non-Windows systems settings simply aren't persisted.
/// </summary>
public static class AppSettings
{
    private const string KeyPath = @"Software\HandBrakeTreemap";

    public const string HandBrakePath = "HandBrakeCliPath";
    public const string Preset = "Preset";
    public const string PresetFile = "PresetImportFile";
    public const string LastFolder = "LastFolder";
    public const string MinSavingPercent = "MinSavingPercent";
    public const string OnlyVerified = "OnlyVerified";
    public const string UseRecycleBin = "UseRecycleBin";

    public static string? GetString(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex)
        {
            Log.Warn($"Instelling '{name}' lezen mislukt: {ex.Message}");
            return null;
        }
    }

    public static void SetString(string name, string? value)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath);
            if (string.IsNullOrEmpty(value)) key.DeleteValue(name, throwOnMissingValue: false);
            else key.SetValue(name, value, Microsoft.Win32.RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Log.Warn($"Instelling '{name}' bewaren mislukt: {ex.Message}");
        }
    }

    public static bool? GetBool(string name)
        => GetString(name) is { } s && bool.TryParse(s, out var b) ? b : null;

    public static void SetBool(string name, bool value) => SetString(name, value.ToString());

    public static decimal? GetDecimal(string name)
        => GetString(name) is { } s && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    public static void SetDecimal(string name, decimal value)
        => SetString(name, value.ToString(CultureInfo.InvariantCulture));
}
