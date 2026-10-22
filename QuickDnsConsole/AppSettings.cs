using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace QuickDNSConsole;

/// <summary>
/// Application data lives in the per-user config directory
/// (%APPDATA%\QuickDns on Windows, ~/.config/QuickDns on Linux) so the
/// executable stays portable and the working directory irrelevant.
/// </summary>
public static class AppPaths
{
    public static string ConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuickDns");

    public static string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    public static string DatabaseFile => Path.Combine(ConfigDirectory, "DnsDB.db");

    public static string CrashLogFile => Path.Combine(ConfigDirectory, "crash.log");

    /// <summary>Silent backup refreshed on every successful start.</summary>
    public static string AutoBackupFile => Path.Combine(ConfigDirectory, "presets.autobackup.json");

    public static string NewExportFile() =>
        Path.Combine(ConfigDirectory, $"QuickDNS-presets-{DateTime.Now:yyyyMMdd-HHmmss}.json");

    /// <summary>Importable export files in the config directory, newest first.</summary>
    public static IReadOnlyList<string> GetExportFiles()
    {
        try
        {
            if (!Directory.Exists(ConfigDirectory))
                return [];

            return Directory.GetFiles(ConfigDirectory, "*.json")
                .Where(f => Path.GetFileName(f).StartsWith("QuickDNS-presets-", StringComparison.OrdinalIgnoreCase)
                            || Path.GetFileName(f).Equals("presets.autobackup.json", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTime)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static void EnsureConfigDirectory() => Directory.CreateDirectory(ConfigDirectory);

    /// <summary>One-time, best-effort migration from the old CWD-based layout of QuickDNS 1.x.</summary>
    public static void MigrateLegacyFiles()
    {
        EnsureConfigDirectory();
        TryCopyLegacy("settings.json", SettingsFile);
        TryCopyLegacy("DnsDB.db", DatabaseFile);
    }

    private static void TryCopyLegacy(string legacyName, string target)
    {
        if (File.Exists(target))
            return;

        try
        {
            var legacy = Path.Combine(Directory.GetCurrentDirectory(), legacyName);
            if (File.Exists(legacy))
                File.Copy(legacy, target, overwrite: false);
        }
        catch
        {
            // Migration is best-effort; a fresh config is always acceptable.
        }
    }
}

public sealed class AppSettings
{
    /// <summary>Adapter the DNS changes target. Null = auto-detect (up + IPv4 gateway first).</summary>
    public string? AdapterId { get; set; }

    public RgbColor AccentColor { get; set; } = new(255, 255, 0);
    public RgbColor HighlightBackground { get; set; } = new(0, 95, 0);
    public RgbColor HighlightForeground { get; set; } = new(255, 255, 255);
    public int HistoryLimit { get; set; } = 10;

    public List<HistoryEntry> History { get; set; } = [];
}

public sealed class HistoryEntry
{
    public DateTime When { get; set; }
    public string Text { get; set; } = string.Empty;
}

public readonly record struct RgbColor(byte R, byte G, byte B);

public static class RgbColorExtensions
{
    public static Color ToSpectreColor(this RgbColor color) => new(color.R, color.G, color.B);
}

public static class AppSettingsStore
{
    public static AppSettings Load(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize(File.ReadAllText(path), AppSettingsJsonContext.Default.AppSettings);
                if (settings is not null)
                {
                    settings.History ??= [];
                    if (settings.HistoryLimit <= 0)
                        settings.HistoryLimit = 10;
                    return settings;
                }
            }
        }
        catch
        {
            // Malformed settings fall back to defaults; the file gets rewritten on next save.
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Persistence is best-effort; never crash the TUI over settings I/O.
        }
    }
}

/// <summary>
/// Source-generated JSON contracts: reflection-free serialization that is safe
/// under IL trimming (and would be under NativeAOT).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}
