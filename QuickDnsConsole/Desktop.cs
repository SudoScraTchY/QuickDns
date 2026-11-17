using System.Diagnostics;

namespace QuickDNSConsole;

/// <summary>
/// Desktop integration helpers (file manager, best effort): anything here must never
/// fail the operation that triggered it — a headless session simply skips the effect.
/// </summary>
public static class Desktop
{
    /// <summary>
    /// Opens the file's containing folder in the OS file manager, with the file selected
    /// where the platform supports it (Windows Explorer). Best effort: silently ignored
    /// when no desktop session is available.
    /// </summary>
    public static void RevealInFileManager(string filePath)
    {
        try
        {
            var full = Path.GetFullPath(filePath);
            if (!File.Exists(full))
                return;

            if (OperatingSystem.IsWindows())
            {
                // explorer.exe /select,"path" opens the containing folder and highlights the file.
                // explorer.exe reports odd exit codes even on success, so the result is ignored.
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                    Arguments = $"/select,\"{full}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            else if (OperatingSystem.IsLinux())
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    ArgumentList = { Path.GetDirectoryName(full) ?? "." },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
        }
        catch
        {
            // No desktop session or missing xdg-open: the export itself already succeeded.
        }
    }
}
