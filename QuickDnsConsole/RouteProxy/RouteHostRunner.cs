using System.Text.Json;
using QuickDNSConsole;

namespace QuickDNSConsole.RouteProxy;

/// <summary>
/// The route host entrypoint: loads the activation snapshot, refreshes the heartbeat
/// every 5 s (so RouteModeManager can detect a dead host), and serves DNS until the
/// process is killed or cancelled. Started detached by RouteModeManager.Start() as
/// <c>quickdns --route-host</c> — no external service or installer involved.
/// </summary>
public static class RouteHostRunner
{
    public static async Task<int> RunAsync()
    {
        try
        {
            var snapshot = RouteSnapshot.Load(AppPaths.RouteActiveFile);

            void Log(string line)
            {
                try
                {
                    File.AppendAllText(AppPaths.RouteLogFile, $"{DateTime.UtcNow:O} {line}{Environment.NewLine}");
                }
                catch
                {
                    // Logging is best effort — never let it kill the proxy.
                }
            }

            Log($"route host starting (pid {Environment.ProcessId}, port {snapshot.Port}, bind {snapshot.BindIp})");

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

            File.WriteAllText(
                AppPaths.RouteStateFile,
                JsonSerializer.Serialize(new RouteState(Environment.ProcessId, DateTime.UtcNow), RouteSnapshotJsonContext.Default.RouteState));

            var server = new DnsProxyServer(snapshot, Log);
            var heartbeat = HeartbeatLoopAsync(cts.Token);

            await server.RunAsync(cts.Token);
            await heartbeat;
            Log("route host stopped");
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(AppPaths.RouteLogFile, $"{DateTime.UtcNow:O} FATAL {ex}{Environment.NewLine}");
            }
            catch
            {
                // Nothing more we can do.
            }

            Console.Error.WriteLine($"route host failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(AppPaths.RouteStateFile))
                    File.SetLastWriteTimeUtc(AppPaths.RouteStateFile, DateTime.UtcNow);
            }
            catch
            {
                // Best effort.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
