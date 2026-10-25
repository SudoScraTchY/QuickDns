using QD.Core.Helper;

namespace QD.Core.Application.Networking;

/// <summary>Result of probing one DNS server for ping, DNS speed and reachability.</summary>
public sealed record DnsProbeResult(string Server, double? PingMs, double? DnsMs, double? TcpMs, string Note)
{
    /// <summary>True when the server answered a real DNS query or accepted a TCP connection on port 53.</summary>
    public bool Reachable => DnsMs is not null || TcpMs is not null;
}

/// <summary>Runs reachability and latency probes against DNS servers.</summary>
public interface IDnsProbeService
{
    /// <summary>
    /// Probes one server: ICMP ping (when the OS allows it), a real DNS A query over UDP 53 for
    /// a well-known name (the definitive reachability + speed signal), and a TCP connect to port 53.
    /// Each step has a 2 second timeout. Never throws; failures are reported in the result.
    /// </summary>
    DnsProbeResult Probe(string server);
}
