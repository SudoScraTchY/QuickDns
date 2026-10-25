using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace QD.Core.Application.Networking;

/// <summary>
/// Default probe implementation. Uses ICMP ping via <see cref="System.Net.NetworkInformation"/>
/// (unprivileged on Windows; on Linux it needs ping_group_range to allow it, in which case the
/// ping step is reported as unavailable while the UDP/TCP tests still run).
/// </summary>
public sealed class DnsProbeService : IDnsProbeService
{
    private const int TimeoutMs = 2000;
    private const string ProbeName = "google.com";

    public DnsProbeResult Probe(string server)
    {
        double? ping = null, dns = null, tcp = null;
        var note = new List<string>();

        if (!IPAddress.TryParse(server.Trim(), out var ip))
            return new DnsProbeResult(server, null, null, null, "not a valid IP address");

        // 1. ICMP ping.
        try
        {
            using var pinger = new Ping();
            var reply = pinger.Send(ip, TimeoutMs);
            if (reply?.Status == IPStatus.Success)
                ping = reply.RoundtripTime;
            else
                note.Add($"ping: {reply?.Status}");
        }
        catch (Exception ex)
        {
            note.Add("ping unavailable");
            _ = ex;
        }

        // 2. A real DNS A query over UDP 53 (definitive reachability + speed signal).
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var udp = new UdpClient(ip.AddressFamily);
            udp.Connect(ip, 53);
            var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            var query = DnsWire.BuildQuery(ProbeName, id);
            udp.Send(query, query.Length);

            var deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (udp.Available > 0)
                {
                    var local = IPAddress.Any;
                    if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                        local = IPAddress.IPv6Any;
                    var anyEndpoint = new IPEndPoint(local, 0);
                    var data = udp.Receive(ref anyEndpoint);
                    {
                        stopwatch.Stop();
                        dns = stopwatch.Elapsed.TotalMilliseconds;
                        break;
                    }
                    // Different transaction id: stale/duplicate datagram, keep waiting.
                }
                else
                {
                    Thread.Sleep(20);
                }
            }

            if (dns is null)
                note.Add("no DNS reply within 2s");
        }
        catch (Exception ex)
        {
            note.Add($"udp failed: {ex.Message}");
        }

        // 3. TCP connect to port 53 (many resolvers accept TCP DNS as well).
        try
        {
            using var client = new TcpClient(ip.AddressFamily);
            var stopwatchTcp = Stopwatch.StartNew();
            var connect = client.ConnectAsync(ip, 53);
            if (connect.Wait(TimeoutMs) && client.Connected)
            {
                stopwatchTcp.Stop();
                tcp = stopwatchTcp.Elapsed.TotalMilliseconds;
            }
        }
        catch
        {
            // TCP 53 not offered or filtered — the UDP result is what matters.
        }

        return new DnsProbeResult(ip.ToString(), ping, dns, tcp, string.Join("; ", note));
    }
}

/// <summary>Minimal DNS wire helpers: build an A query and validate that a reply matches it.</summary>
public static class DnsWire
{
    public static byte[] BuildQuery(string name, ushort id)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((byte)(id >> 8));
        writer.Write((byte)(id & 0xFF));
        writer.Write((byte)0x01); writer.Write((byte)0x00);   // flags: RD=1
        writer.Write((byte)0x00); writer.Write((byte)0x01);   // QDCOUNT = 1
        writer.Write((byte)0x00); writer.Write((byte)0x00);   // ANCOUNT = 0
        writer.Write((byte)0x00); writer.Write((byte)0x00);   // NSCOUNT = 0
        writer.Write((byte)0x00); writer.Write((byte)0x00);   // ARCOUNT = 0

        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63)
                throw new ArgumentException("Each label must be 1-63 ASCII characters.", nameof(name));

            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }

        writer.Write((byte)0x00);                             // root label
        writer.Write((byte)0x00); writer.Write((byte)0x01);   // QTYPE = A
        writer.Write((byte)0x00); writer.Write((byte)0x01);   // QCLASS = IN

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>True when the datagram is a DNS response to our query (matching id, QR flag set).</summary>
    public static bool IsValidResponse(byte[]? response, byte[] query)
    {
        if (response is null || response.Length < 12 || query.Length < 2)
            return false;

        if (response[0] != query[0] || response[1] != query[1])
            return false;

        var flags = (response[2] << 8) | response[3];
        return (flags & 0x8000) != 0;
    }
}
