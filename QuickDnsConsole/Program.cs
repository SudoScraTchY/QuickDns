using System.Diagnostics;
using System.Runtime.InteropServices;
using QD.Core.Application;
using QD.Core.Application.Catalog;
using QD.Core.Application.Networking;
using QD.Core.Cotracts.DnsObjContracts;
using QD.Core.Helper;
using QD.Core.Infrastructure.SQLite;
using QuickDNSConsole.Tui;

namespace QuickDNSConsole;

internal static class Program
{
    public static string VersionText =>
        typeof(Program).Assembly.GetName().Version!.ToString(3);

    private static int Main(string[] args)
    {
        // Single-option CLI: the first argument (if any) decides the mode.
        if (args.Length > 0)
            return HandleOption(args, 0);

        if (!Console.IsOutputRedirected)
        {
            try
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
            }
            catch
            {
                // The console may refuse encoding changes; Spectre falls back gracefully.
            }
        }

        try
        {
            // Deliberately not Microsoft.Extensions.Hosting: the generic host adds
            // startup cost (configuration, logging, lifetime) that a TUI does not need.
            var stopwatch = Stopwatch.StartNew();

            AppPaths.MigrateLegacyFiles();
            var settings = AppSettingsStore.Load();
            var (dnsApp, adapters, startupWarning) = CreateApplication();

            stopwatch.Stop();

            new DnsTui(dnsApp, adapters, settings, stopwatch.Elapsed, new DnsProbeService(), DnsCatalog.Embedded, startupWarning).Run();
            return 0;
        }
        catch (PlatformNotSupportedException ex)
        {
            return Fail(ex);
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    /// <summary>Handles the single CLI option (no interactive mode).</summary>
    private static int HandleOption(string[] args, int index)
    {
        switch (args[index].ToLowerInvariant())
        {
            case "--version" or "-v":
                Console.WriteLine(VersionText);
                return 0;

            case "--help" or "-h":
                PrintHelp();
                return 0;

            case "--diagnostics":
                return RunDiagnostics();

            case "--export" or "-e":
                var exportPath = index + 1 < args.Length && !args[index + 1].StartsWith('-')
                    ? args[index + 1]
                    : AppPaths.NewExportFile();
                return RunStoreOperation(app => app.ExportPresets(exportPath));

            case "--export-csv" or "-c":
                var csvPath = index + 1 < args.Length && !args[index + 1].StartsWith('-')
                    ? args[index + 1]
                    : AppPaths.NewExportFile(".csv");
                return RunStoreOperation(app => app.ExportPresetsCsv(csvPath));

            case "--test" or "-t":
                if (index + 1 >= args.Length)
                {
                    Console.Error.WriteLine("--test requires a preset name or DNS server IP (see --help).");
                    return 2;
                }
                return RunTestOperation(args[index + 1]);

            case "--import" or "-i":
                if (index + 1 >= args.Length)
                {
                    Console.Error.WriteLine("--import requires a file path (see --help).");
                    return 2;
                }
                return RunStoreOperation(app => app.ImportPresets(args[index + 1]));

            default:
                Console.Error.WriteLine($"Unknown option '{args[index]}'.");
                PrintHelp();
                return 2;
        }
    }

    /// <summary>
    /// Builds the application services. The preset store is designed to never block
    /// startup: a damaged store gets quarantined and recreated, and the silent
    /// auto-backup (written on every clean run) is restored into it automatically.
    /// </summary>
    private static (IDnsObjApplication App, INetworkAdapterService Adapters, string? Warning) CreateApplication()
    {
        var database = new DnsDatabase(AppPaths.DatabaseFile);
        string? warning = null;

        bool quarantined;
        try
        {
            quarantined = database.Initialize();
        }
        catch (Exception ex)
        {
            quarantined = false;
            warning = $"{ApplicationMessages.StoreOpenFailed} ({ex.Message})";
        }

        var repository = new DnsObjRepository(database);
        var adapters = NetworkAdapterServiceFactory.Create();
        IDnsObjApplication app = new DnsObjApplication(repository, adapters);

        if (quarantined)
        {
            warning = ApplicationMessages.StoreQuarantined;
            if (File.Exists(AppPaths.AutoBackupFile))
            {
                var restore = app.ImportPresets(AppPaths.AutoBackupFile);
                warning += restore.IsSucceeded
                    ? $" Presets restored from the automatic backup: {restore.Message}"
                    : " Automatic restore from backup failed.";
            }
            else
            {
                PresetSeeder.SeedIfEmpty(repository);
                warning += " Starting with the default presets.";
            }
        }
        else if (warning is null)
        {
            PresetSeeder.SeedIfEmpty(repository);

            // Silent safety net: refresh the auto-backup on every clean run so an
            // update or a corrupted store can never wipe the user's DNS list.
            app.WriteAutoBackup(AppPaths.AutoBackupFile);
        }

        return (app, adapters, warning);
    }

    private static int RunStoreOperation(Func<IDnsObjApplication, OperationResult> operation)
    {
        try
        {
            var (app, _, _) = CreateApplication();
            var result = operation(app);
            Console.WriteLine(result.Message);
            return result.IsSucceeded ? 0 : 1;
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    /// <summary>--test: ping, DNS query speed and reachability for a preset name or a raw IP.</summary>
    private static int RunTestOperation(string target)
    {
        try
        {
            var (app, _, _) = CreateApplication();
            IDnsProbeService probe = new DnsProbeService();

            DnsObjViewModel? preset = null;
            var isIp = DnsValidator.IsValidIpAddress(target);
            if (!isIp)
                preset = app.GetAll().FirstOrDefault(p => string.Equals(p.Name, target.Trim(), StringComparison.OrdinalIgnoreCase));

            if (!isIp && preset is null)
            {
                Console.Error.WriteLine($"No preset named '{target}'.");
                return 1;
            }

            if (preset is not null)
                Console.WriteLine($"Preset: {preset.Name} ({preset.DnsAddresses})");

            var first = preset?.FirstDns ?? target.Trim();
            PrintProbe(first, probe.Probe(first));
            if (preset is not null && !string.IsNullOrWhiteSpace(preset.SecondDns))
                PrintProbe(preset.SecondDns, probe.Probe(preset.SecondDns));

            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    private static void PrintProbe(string server, DnsProbeResult result)
    {
        Console.WriteLine($"Server {server}");
        Console.WriteLine($"  ping       {(result.PingMs is { } p ? $"{p:F0} ms" : "n/a")}");
        Console.WriteLine($"  dns query  {(result.DnsMs is { } d ? $"{d:F0} ms" : "n/a")}");
        Console.WriteLine($"  tcp 53     {(result.TcpMs is { } t ? $"{t:F0} ms" : "n/a")}");
        Console.WriteLine($"  verdict    {(result.Reachable ? "reachable" : "unreachable")}{(result.Note.Length > 0 ? $" ({result.Note})" : string.Empty)}");
        Console.WriteLine();
    }

    private static int Fail(Exception ex)
    {
        try
        {
            AppPaths.EnsureConfigDirectory();
            File.AppendAllText(
                AppPaths.CrashLogFile,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex.GetType().Name}: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Crash logging is best-effort.
        }

        Console.Error.WriteLine($"QuickDNS failed: {ex.Message}");
        Console.Error.WriteLine($"Details were written to {AppPaths.CrashLogFile}");
        Console.Error.WriteLine();

        // When the user double-clicks the exe, keep the window open long enough
        // for the error to be readable instead of flash-closing.
        if (!Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("Press any key to close…");
            try { Console.ReadKey(intercept: true); } catch { /* no console input */ }
        }

        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"QuickDNS {VersionText} — fast network DNS switcher for Windows and Linux");
        Console.WriteLine();
        Console.WriteLine("Usage: QuickDNS [option]");
        Console.WriteLine("  (no option)    Launch the interactive dashboard");
        Console.WriteLine("  --diagnostics  Print platform, elevation, DNS backend, adapters and store, then exit");
        Console.WriteLine("  --export [path]   Export all presets to a portable JSON file (default: a new file in the config dir)");
        Console.WriteLine("  --export-csv [path]  Export all presets to CSV (name,primary,secondary)");
        Console.WriteLine("  --import <path>   Import presets from a JSON or line-format file (duplicates are skipped)");
        Console.WriteLine("  --test <name|ip>  Ping, DNS query speed and reachability for a preset or server");
        Console.WriteLine("  --version      Print version and exit");
        Console.WriteLine("  --help         Show this help");
        Console.WriteLine();
        Console.WriteLine("Changing DNS needs elevation: administrator on Windows, root/sudo on Linux.");
        Console.WriteLine($"Settings, presets and backups: {AppPaths.ConfigDirectory}");
    }

    private static int RunDiagnostics()
    {
        Console.WriteLine($"QuickDNS {VersionText} diagnostics");
        Console.WriteLine($"OS:          {RuntimeInformation.RuntimeIdentifier} · {RuntimeInformation.OSDescription.Trim()}");
        Console.WriteLine($"64-bit:      {Environment.Is64BitProcess}");
        Console.WriteLine();

        INetworkAdapterService service;
        try
        {
            service = NetworkAdapterServiceFactory.Create();
        }
        catch (PlatformNotSupportedException ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }

        Console.WriteLine($"Platform:    {service.Platform}");
        Console.WriteLine($"Elevated:    {service.IsElevated}");
        Console.WriteLine($"DNS backend: {service.BackendDescription}");
        Console.WriteLine($"Config dir:  {AppPaths.ConfigDirectory}");

        try
        {
            var store = new DnsObjRepository(new DnsDatabase(AppPaths.DatabaseFile));
            Console.WriteLine($"Preset store: OK · {store.Count()} preset(s) · {AppPaths.DatabaseFile}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Preset store: ERROR — {ex.Message}");
        }

        Console.WriteLine();

        var adapters = service.GetAdapters();
        if (adapters.Count == 0)
        {
            Console.WriteLine("No usable Ethernet/Wi-Fi adapters found.");
            return 0;
        }

        foreach (var adapter in adapters)
        {
            Console.WriteLine($"[{(adapter.IsUp ? "up  " : "down")}] {adapter.Name} ({adapter.Kind}) — {adapter.Description}");
            Console.WriteLine($"        id={adapter.Id} ifIndex={adapter.InterfaceIndex?.ToString() ?? "-"}");
            Console.WriteLine($"        ip={string.Join(", ", adapter.IpAddresses.DefaultIfEmpty("-"))}");
            Console.WriteLine($"        gw={string.Join(", ", adapter.GatewayAddresses.DefaultIfEmpty("-"))}");
            Console.WriteLine($"        dns={string.Join(", ", adapter.DnsServers.DefaultIfEmpty("none"))}");
        }

        var resolved = service.ResolveTarget(preferredAdapterId: null);
        Console.WriteLine();
        Console.WriteLine($"Auto-resolved target: {(resolved is null ? "(none)" : $"{resolved.Name} ({resolved.Description})")}");

        return 0;
    }
}
