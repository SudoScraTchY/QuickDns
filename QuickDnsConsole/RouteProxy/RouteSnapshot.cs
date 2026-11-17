using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickDNSConsole.RouteProxy;

/// <summary>One upstream binding in the route snapshot: pattern → servers.</summary>
public sealed class RouteServerSnapshot
{
    public string Pattern { get; set; } = string.Empty;
    public string Primary { get; set; } = string.Empty;
    public string? Secondary { get; set; }
}

/// <summary>
/// The immutable activation snapshot consumed by the route host process: which patterns
/// map to which upstream servers, and the listen port. The parent writes it to
/// routes.active.json right before spawning the host; rules changed later require a
/// route restart (by design — the snapshot is the source of truth while running).
/// </summary>
public sealed class RouteSnapshot
{
    public int Port { get; set; } = 53;

    /// <summary>
    /// Loopback address the proxy binds (127.0.0.0/8 is all loopback). The parent picks a
    /// free one (127.0.0.2… by default) so route mode coexists with other local DNS tools
    /// that already own 127.0.0.1:53 (AdGuard, Shecan/NextDNS helpers, Umbrella…).
    /// </summary>
    public string BindIp { get; set; } = "127.0.0.2";

    /// <summary>Enabled rules; the <c>*</c> catch-all (when present) is the fallback.</summary>
    public List<RouteServerSnapshot> Rules { get; set; } = [];

    public static RouteSnapshot Load(string path)
    {
        var json = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize(json, RouteSnapshotJsonContext.Default.RouteSnapshot)
            ?? throw new InvalidOperationException("Route snapshot is empty.");

        if (snapshot.Port is < 1 or > 65535)
            throw new InvalidOperationException($"Invalid port {snapshot.Port} in the route snapshot.");

        return snapshot;
    }

    public static void Save(string path, RouteSnapshot snapshot)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, RouteSnapshotJsonContext.Default.RouteSnapshot));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Source-generated JSON contracts — reflection-free, safe under IL trimming and AOT.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RouteSnapshot))]
[JsonSerializable(typeof(RouteServerSnapshot))]
[JsonSerializable(typeof(RouteState))]
internal sealed partial class RouteSnapshotJsonContext : JsonSerializerContext
{
}
