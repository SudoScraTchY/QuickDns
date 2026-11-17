using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using QD.Core.Helper;

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

    /// <inheritdoc />
    public DnsResolveResult Resolve(string server, string domain)
    {
        if (!IPAddress.TryParse((server ?? string.Empty).Trim(), out var ip))
            return new DnsResolveResult(server ?? string.Empty, domain, false, null, [], 0, "not a valid server IP");

        var name = (domain ?? string.Empty).Trim().TrimEnd('.');
        if (!DnsValidator.IsValidHostname(name))
            return new DnsResolveResult(ip.ToString(), domain ?? string.Empty, false, null, [], 0, "not a valid domain name");

        try
        {
            using var udp = new UdpClient(ip.AddressFamily);
            udp.Connect(ip, 53);

            var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            var query = DnsWire.BuildQuery(name, id, DnsWire.QtypeA);
            udp.Send(query, query.Length);

            var stopwatch = Stopwatch.StartNew();
            var deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (udp.Available > 0)
                {
                    var local = ip.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
                    var anyEndpoint = new IPEndPoint(local, 0);
                    var data = udp.Receive(ref anyEndpoint);

                    if (!DnsWire.IsValidResponse(data, query))
                        continue; // stale/duplicate datagram, keep waiting

                    stopwatch.Stop();
                    var (rcode, addresses) = DnsWire.ParseResponse(data, query);
                    var resolved = rcode == 0 && addresses.Count > 0;
                    var error = resolved
                        ? string.Empty
                        : rcode switch
                        {
                            3 => "NXDOMAIN: the domain does not exist",
                            0 => "no A record returned",
                            _ => $"server refused (rcode {rcode})",
                        };
                    return new DnsResolveResult(ip.ToString(), name, resolved, stopwatch.Elapsed.TotalMilliseconds, addresses, rcode, error);
                }

                Thread.Sleep(20);
            }

            return new DnsResolveResult(ip.ToString(), name, false, null, [], 0, "no reply within 2s (blocked or filtered)");
        }
        catch (Exception ex)
        {
            return new DnsResolveResult(ip.ToString(), name, false, null, [], 0, ex.Message);
        }
    }
}

/// <summary>Minimal DNS wire helpers: build an A query and validate that a reply matches it.</summary>
public static class DnsWire
{
    public const ushort QtypeA = 1;
    public const ushort QtypeAaaa = 28;

    public static byte[] BuildQuery(string name, ushort id, ushort queryType = QtypeA)
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
        writer.Write((byte)(queryType >> 8)); writer.Write((byte)(queryType & 0xFF));   // QTYPE
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

    /// <summary>
    /// Parses a DNS response: returns the RCODE and the A/AAAA addresses found in the
    /// answer section (CNAME chains are skipped; compressed names are handled).
    /// </summary>
    public static (int Rcode, IReadOnlyList<string> Addresses) ParseResponse(byte[] response, byte[] query)
    {
        var addresses = new List<string>();
        if (response is null || response.Length < 12)
            return (0, addresses);

        var rcode = response[3] & 0x0F;
        var answerCount = (response[6] << 8) | response[7];

        var i = 12;

        // Skip the question section.
        var questionCount = (response[4] << 8) | response[5];
        for (var q = 0; q < questionCount && i < response.Length; q++)
        {
            i = SkipName(response, i);
            i += 4; // QTYPE + QCLASS
        }

        // Walk the answer records.
        for (var a = 0; a < answerCount && i + 10 <= response.Length; a++)
        {
            i = SkipName(response, i);
            if (i + 10 > response.Length)
                break;

            var type = (response[i] << 8) | response[i + 1];
            var rdLength = (response[i + 8] << 8) | response[i + 9];
            i += 10;

            if (i + rdLength > response.Length)
                break;

            if (type == QtypeA && rdLength == 4)
                addresses.Add($"{response[i]}.{response[i + 1]}.{response[i + 2]}.{response[i + 3]}");
            else if (type == QtypeAaaa && rdLength == 16)
                addresses.Add(new IPAddress(response.AsSpan(i, 16).ToArray()).ToString());

            i += rdLength;
        }

        return (rcode, addresses);
    }

    /// <summary>True when the response has the TC (truncation) flag set - the caller should retry over TCP.</summary>
    public static bool IsTruncated(byte[] response) =>
        response is { Length: >= 3 } && (response[2] & 0x02) != 0;

    /// <summary>
    /// Extracts the queried domain name from a DNS query's question section (offset 12,
    /// uncompressed labels). Returns null when the datagram is malformed.
    /// </summary>
    public static string? TryParseQuestionName(byte[] datagram)
    {
        if (datagram is null || datagram.Length < 12)
            return null;

        var labels = new List<string>(8);
        var i = 12;
        while (i < datagram.Length)
        {
            var length = datagram[i];
            if (length == 0)
                break; // root label - end of the name

            if ((length & 0xC0) != 0 || i + 1 + length > datagram.Length)
                return null; // compression pointers are not valid in a question section

            labels.Add(Encoding.ASCII.GetString(datagram, i + 1, length));
            i += 1 + length;
        }

        if (labels.Count == 0 || labels.Any(string.IsNullOrEmpty))
            return null;

        return string.Join('.', labels).ToLowerInvariant();
    }
    /// <summary>Skips a (possibly compressed) domain name; returns the index after it.</summary>
    private static int SkipName(byte[] buffer, int index)
    {
        while (index < buffer.Length)
        {
            var length = buffer[index];
            if (length == 0)
                return index + 1;

            if ((length & 0xC0) == 0xC0)
                return index + 2; // compression pointer

            index += length + 1;
        }

        return index;
    }
}
