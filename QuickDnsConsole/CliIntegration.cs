using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using QD.Core.Helper;

namespace QuickDNSConsole;

/// <summary>
/// Built-in CLI integration: makes <c>quickdns</c> callable from any terminal window
/// without a separate installer or script. The running executable installs a copy of
/// itself into a per-user bin directory and puts that directory on the user's PATH
/// (Windows: %LOCALAPPDATA%\Programs\QuickDNS + HKCU Environment Path; Linux: a
/// quickdns symlink in ~/.local/bin). Initiated via <c>quickdns --setup</c> or the
/// Setup action inside the app; <c>--uninstall</c> reverts it.
/// </summary>
public static class CliIntegration
{
    /// <summary>Command name available in terminals after setup.</summary>
    public const string CommandName = "quickdns";

    /// <summary>Directory that receives the installed executable (and holds the PATH entry).</summary>
    public static string InstallDirectory { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "QuickDNS")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "bin");

    /// <summary>Path of the executable created by setup (null when the OS shape is unknown).</summary>
    public static string? InstalledExecutablePath => OperatingSystem.IsWindows()
        ? Path.Combine(InstallDirectory, "QuickDNS.exe")
        : OperatingSystem.IsLinux()
            ? Path.Combine(InstallDirectory, CommandName)
            : null;

    public sealed record Status(bool Installed, bool OnPath, string? InstalledPath, string InstallDirectory)
    {
        public bool FullyInstalled => Installed && OnPath;
    }

    /// <summary>Current integration state: executable present and directory reachable via PATH.</summary>
    public static Status GetStatus()
    {
        var installedPath = InstalledExecutablePath;
        var installed = installedPath is not null && File.Exists(installedPath);
        var onPath = PathContains(GetPathEntries(), InstallDirectory);
        return new Status(installed, onPath, installedPath, InstallDirectory);
    }

    /// <summary>
    /// Runtime files that must sit next to the executable: native libraries
    /// (e.g. e_sqlite3.dll for Microsoft.Data.Sqlite) and, for framework-dependent
    /// layouts, the managed dll + its *.deps.json / *.runtimeconfig.json.
    /// </summary>
    private static readonly string[] CompanionPatterns =
        ["*.dll", "*.so", "*.dylib", "*.deps.json", "*.runtimeconfig.json"];

    /// <summary>Companion files next to the running executable (never the exe itself).</summary>
    private static IReadOnlyList<string> GetCompanionFiles()
    {
        var source = Environment.ProcessPath;
        if (string.IsNullOrEmpty(source))
            return [];

        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(source));
        if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir))
            return [];

        var exePath = Path.GetFullPath(source);
        var sameName = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var files = new List<string>();
        foreach (var pattern in CompanionPatterns)
        {
            foreach (var file in Directory.EnumerateFiles(sourceDir, pattern))
            {
                if (!string.Equals(Path.GetFullPath(file), exePath, sameName))
                    files.Add(file);
            }
        }

        return [.. files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Installs the command: copy the running exe + ensure the PATH entry. Never needs elevation.</summary>
    public static OperationResult Install()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return new OperationResult().Failed("CLI integration supports Windows and Linux only.");

        var source = Environment.ProcessPath;
        if (string.IsNullOrEmpty(source) || !File.Exists(source))
            return new OperationResult().Failed("Could not locate the running QuickDNS executable.");

        try
        {
            var installedPath = InstalledExecutablePath;
            if (installedPath is null)
                return new OperationResult().Failed("Could not determine the install location.");

            Directory.CreateDirectory(InstallDirectory);

            var runningFromInstallDir = string.Equals(
                Path.GetFullPath(source),
                Path.GetFullPath(installedPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (!runningFromInstallDir)
            {
                // Retry briefly: copying over a running QuickDNS.exe fails while another
                // instance is executing from the install directory (Windows file locking).
                CopyWithRetry(source, installedPath);
            }

            // Companion runtime files MUST come along: NativeAOT builds load
            // e_sqlite3.dll from the exe's own directory, and framework-dependent
            // layouts need their managed dll + *.deps.json / *.runtimeconfig.json.
            // Without them the installed copy boots, but the preset store cannot open.
            var companions = GetCompanionFiles();
            var copiedCompanions = 0;
            foreach (var companion in companions)
            {
                var target = Path.Combine(InstallDirectory, Path.GetFileName(companion));
                if (string.Equals(Path.GetFullPath(companion), Path.GetFullPath(target), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    continue; // running from the install dir: already in place

                CopyWithRetry(companion, target);
                copiedCompanions++;
            }

            // RID-specific framework-dependent layouts may keep their native libraries in a
            // runtimes/<rid>/native/ tree (libe_sqlite3.so on Linux) instead of next to the
            // exe: bring the whole tree along, or the installed copy cannot open its store.
            var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(source));
            if (!string.IsNullOrEmpty(sourceDirectory))
            {
                var runtimeTree = Path.Combine(sourceDirectory, RuntimeTreeName);
                var installedTree = Path.Combine(InstallDirectory, RuntimeTreeName);
                var sameName = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (Directory.Exists(runtimeTree) && !string.Equals(Path.GetFullPath(runtimeTree), Path.GetFullPath(installedTree), sameName))
                    copiedCompanions += CopyTree(runtimeTree, installedTree);
            }

            string pathMessage;
            if (OperatingSystem.IsWindows())
            {
                var pathResult = WindowsAddToUserPath(InstallDirectory);
                if (!pathResult.IsSucceeded)
                    return pathResult;
                pathMessage = "User PATH updated.";
            }
            else
            {
                var pathResult = LinuxEnsurePathHint(InstallDirectory, out var hint);
                if (!pathResult.IsSucceeded)
                    return pathResult;
                pathMessage = hint;
            }

            var companionNote = copiedCompanions > 0
                ? $" (+{copiedCompanions} runtime file(s))"
                : string.Empty;
            return new OperationResult().Succeeded(
                $"Installed {CommandName} → {installedPath}{companionNote}. {pathMessage} Open a NEW terminal window and type {CommandName}.");
        }
        catch (Exception ex)
        {
            return new OperationResult().Failed($"CLI setup failed: {ex.Message}");
        }
    }

    /// <summary>Removes the installed command and the PATH entry.</summary>
    public static OperationResult Uninstall()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return new OperationResult().Failed("CLI integration supports Windows and Linux only.");

        try
        {
            var notes = new List<string>();

            if (OperatingSystem.IsWindows())
            {
                var pathResult = WindowsRemoveFromUserPath(InstallDirectory);
                if (!pathResult.IsSucceeded)
                    notes.Add(pathResult.Message);
            }

            var installedPath = InstalledExecutablePath;
            if (installedPath is not null && File.Exists(installedPath))
            {
                // Deleting the exe of the currently running instance is impossible on
                // Windows — if so, tell the user it will vanish on the next uninstall.
                try
                {
                    File.Delete(installedPath);
                }
                catch (IOException) when (OperatingSystem.IsWindows())
                {
                    notes.Add($"{installedPath} is in use (this instance?) — it will be removed next time.");
                }
            }

            // Companion runtime files (native dlls, deps/runtimeconfig) were installed
            // next to the exe — remove them too.
            foreach (var pattern in CompanionPatterns)
            {
                foreach (var file in Directory.EnumerateFiles(InstallDirectory, pattern))
                {
                    try { File.Delete(file); }
                    catch { /* best-effort; a locked file disappears next time */ }
                }
            }

            // The runtimes/ tree (native libraries of RID-specific layouts) goes too.
            try
            {
                var runtimeTree = Path.Combine(InstallDirectory, RuntimeTreeName);
                if (Directory.Exists(runtimeTree))
                    Directory.Delete(runtimeTree, recursive: true);
            }
            catch { /* best-effort; a locked file disappears next time */ }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(InstallDirectory).Any())
                    Directory.Delete(InstallDirectory);
            }
            catch
            {
                // Non-empty or locked dir: leave it, the installed files are already gone.
            }

            return notes.Count > 0
                ? new OperationResult().Succeeded($"Removed the {CommandName} command ({string.Join("; ", notes)})")
                : new OperationResult().Succeeded($"Removed the {CommandName} command. Open a NEW terminal window for the change to apply.");
        }
        catch (Exception ex)
        {
            return new OperationResult().Failed($"CLI uninstall failed: {ex.Message}");
        }
    }

    // ---- pure PATH helpers (unit-tested) ------------------------------------

    public static IReadOnlyList<string> SplitPathEntries(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(OperatingSystem.IsWindows() ? ';' : ':',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string JoinPathEntries(IReadOnlyList<string> entries) =>
        string.Join(OperatingSystem.IsWindows() ? ';' : ':', entries);

    public static bool PathContains(IReadOnlyList<string> entries, string directory) =>
        entries.Any(e => string.Equals(
            e.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

    /// <summary>Prepends the directory, dropping duplicates and empty entries.</summary>
    public static IReadOnlyList<string> MergePathEntry(IReadOnlyList<string> entries, string directory) =>
        [directory, .. entries.Where(e => !PathContains([e], directory))];

    /// <summary>Removes the directory; the rest keeps its original order.</summary>
    public static IReadOnlyList<string> RemovePathEntry(IReadOnlyList<string> entries, string directory) =>
        entries.Where(e => !PathContains([e], directory)).ToList();

    // ---- Windows user PATH (HKCU\Environment) --------------------------------

    [SupportedOSPlatform("windows")]
    private static OperationResult WindowsAddToUserPath(string directory)
    {
        var (current, kind) = ReadUserPath();
        if (PathContains(current, directory))
            return new OperationResult().Succeeded("PATH already contains the install directory.");

        var merged = JoinPathEntries(MergePathEntry(current, directory));
        WriteUserPath(merged, kind ?? RegistryValueKind.ExpandString);

        BroadcastEnvironmentChange();
        return new OperationResult().Succeeded("User PATH updated.");
    }

    [SupportedOSPlatform("windows")]
    private static OperationResult WindowsRemoveFromUserPath(string directory)
    {
        var (current, kind) = ReadUserPath();
        if (!PathContains(current, directory))
            return new OperationResult().Succeeded("PATH does not contain the install directory.");

        var merged = JoinPathEntries(RemovePathEntry(current, directory));
        WriteUserPath(merged, kind ?? RegistryValueKind.ExpandString);

        BroadcastEnvironmentChange();
        return new OperationResult().Succeeded("User PATH updated.");
    }

    [SupportedOSPlatform("windows")]
    private static (IReadOnlyList<string> Entries, RegistryValueKind? Kind) ReadUserPath()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment");
        if (key is null)
            return ([], null);

        var raw = key.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var kind = SafeKind(key);
        return (SplitPathEntries(raw), kind);
    }

    [SupportedOSPlatform("windows")]
    private static RegistryValueKind? SafeKind(Microsoft.Win32.RegistryKey key)
    {
        try { return key.GetValueKind("Path"); }
        catch { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static void WriteUserPath(string value, RegistryValueKind kind)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment", writable: true)
            ?? throw new InvalidOperationException("Could not open HKCU\\Environment for writing.");
        // Keep REG_EXPAND_SZ so %VAR% entries inside PATH stay functional.
        key.SetValue("Path", value, kind == RegistryValueKind.ExpandString
            ? RegistryValueKind.ExpandString
            : RegistryValueKind.String);
    }

    /// <summary>
    /// Tells running Explorer processes (and therefore newly opened terminals) that the
    /// environment changed, so the PATH update applies without signing out.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void BroadcastEnvironmentChange()
    {
        try
        {
            SendMessageTimeout(
                new IntPtr(0xffff),          // HWND_BROADCAST
                WmSettingChange,
                UIntPtr.Zero,
                "Environment",
                SmtoAbortIfHung,
                2000,
                out _);
        }
        catch
        {
            // Best-effort: terminals opened after setup always see the new PATH anyway.
        }
    }

    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

    // ---- Linux ----------------------------------------------------------------

    private static OperationResult LinuxEnsurePathHint(string directory, out string hint)
    {
        var entries = GetPathEntries();
        hint = PathContains(entries, directory)
            ? string.Empty
            : $" Add {directory} to your PATH (e.g. add it to ~/.bashrc) if `which {CommandName}` does not find it.";
        return new OperationResult().Succeeded("Symlink created.");
    }

    private static IReadOnlyList<string> GetPathEntries() =>
        SplitPathEntries(Environment.GetEnvironmentVariable("PATH"));

    /// <summary>Native-library tree of RID-specific framework-dependent layouts (runtimes/rid/native).</summary>
    private const string RuntimeTreeName = "runtimes";

    /// <summary>Copies a directory tree recursively (overwriting); returns the number of files copied.</summary>
    private static int CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyWithRetry(file, destination);
            copied++;
        }

        return copied;
    }

    private static void CopyWithRetry(string source, string target)
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(200 * attempt);
            }
        }
    }
}
