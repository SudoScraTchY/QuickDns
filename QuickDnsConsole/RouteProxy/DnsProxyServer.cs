using System.Net;
using System.Diagnostics;
using System.Net.Sockets;
using QD.Core.Application.Networking;
using QD.Core.Application.Routing;

namespace QuickDNSConsole.RouteProxy;

/// <summary>
/// The route-based DNS proxy: a small forwarding resolver bound to a free loopback address
/// on port 53 (127.0.0.2 by default). The system's adapters point their DNS at it during route mode;
/// every query is matched against the route rules and relayed to the matching upstream.
///
/// Design rules (verified against RFC 7766 / RFC 6891 and real-world forwarders):
/// - query bytes are relayed <b>verbatim</b> — EDNS0/OPT records are never touched, and the
///   upstream itself truncates oversized UDP responses with TC=1 (the client retries TCP);
/// - a TC=1 answer is retried over TCP here so clients that do not retry still work;
/// - UDP receive buffers are 65,527 bytes — .NET throws on datagrams larger than the buffer;
/// - TCP uses the 2-byte big-endian length prefix, and multiple queries per connection are
///   served (RFC 7766 §5/§8);
/// - the bind IP is a free loopback address (127.0.0.2 by default): 127.0.0.0/8 is all
///   loopback, so route mode coexists with other local DNS tools owning 127.0.0.1.
/// </summary>
public sealed class DnsProxyServer
{
    private const int MaxDnsMessage = 65527;
    private static readonly TimeSpan UdpTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TcpTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TcpIdleTimeout = TimeSpan.FromSeconds(30);

    private readonly RouteSnapshot _snapshot;
    private readonly DnsRouteEngine _engine;
    private readonly Action<string> _log;

    public DnsProxyServer(RouteSnapshot snapshot, Action<string> log)
    {
        _snapshot = snapshot;
        _log = log;

        var rules = snapshot.Rules
            .Where(r => DnsRouteEngine.IsValidPattern(r.Pattern) && IPAddress.TryParse(r.Primary, out _))
            .Select(r => new DnsRouteServer(r.Pattern, r.Primary, r.Secondary))
            .ToList();

        _engine = new DnsRouteEngine(rules);
        foreach (var r in rules)
            _log($"rule: {r.Pattern} -> {r.Primary}{(r.Secondary is null ? string.Empty : " | " + r.Secondary)}");
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(_snapshot.BindIp, out var bindIp))
            throw new InvalidOperationException($"Invalid bind IP '{_snapshot.BindIp}' in the route snapshot.");

        var port = _snapshot.Port;
        var v4 = new IPEndPoint(bindIp, port);

        _log($"listening on udp/tcp {v4} ({_engine.Rules.Count} rule(s))");
        await Task.WhenAll(
            UdpLoopAsync(v4, cancellationToken),
            TcpLoopAsync(v4, cancellationToken));
    }

    // ---- UDP ---------------------------------------------------------------

    private async Task UdpLoopAsync(IPEndPoint bind, CancellationToken ct)
    {
        using var socket = new Socket(bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(bind);
        _log($"udp bound {bind}");

        var buffer = new byte[MaxDnsMessage];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, bind, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _log($"udp receive error: {ex.Message}");
                continue;
            }

            var query = buffer.AsSpan(0, result.ReceivedBytes).ToArray();
            var client = result.RemoteEndPoint;
            _ = Task.Run(() => HandleUdpAsync(socket, query, client, ct), ct);
        }
    }

    private async Task HandleUdpAsync(Socket socket, byte[] query, EndPoint client, CancellationToken ct)
    {
        var response = await ResolveAsync(query, ct) ?? BuildServFail(query);
        try
        {
            await socket.SendToAsync(response, SocketFlags.None, client, ct);
            _log($"replied {response.Length} bytes to {client}");
        }
        catch (Exception ex)
        {
            _log($"reply to {client} failed: {ex.Message}");
        }
    }

    // ---- TCP ---------------------------------------------------------------

    private async Task TcpLoopAsync(IPEndPoint bind, CancellationToken ct)
    {
        using var listener = new Socket(bind.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(bind);
        listener.Listen(128);
        _log($"tcp bound {bind}");

        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _log($"tcp accept error: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleTcpClientAsync(client, ct), ct);
        }
    }

    private async Task HandleTcpClientAsync(Socket client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var lengthBuffer = new byte[2];
                    if (!await ReadExactAsync(client, lengthBuffer, ct))
                        return;

                    var length = (lengthBuffer[0] << 8) | lengthBuffer[1];
                    if (length == 0)
                        return;

                    var query = new byte[length];
                    if (!await ReadExactAsync(client, query, ct))
                        return;

                    var response = await ResolveAsync(query, ct) ?? BuildServFail(query);
                    var frame = new byte[2 + response.Length];
                    frame[0] = (byte)(response.Length >> 8);
                    frame[1] = (byte)(response.Length & 0xFF);
                    response.CopyTo(frame, 2);
                    await client.SendAsync(frame, SocketFlags.None, ct);
                }
            }
            catch
            {
                // Connection closed, reset or timed out: drop it.
            }
        }
    }

    private static async Task<bool> ReadExactAsync(Socket socket, byte[] buffer, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TcpIdleTimeout);

        var offset = 0;
        while (offset < buffer.Length)
        {
            int received;
            try
            {
                received = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false; // idle timeout
            }

            if (received == 0)
                return false; // peer closed
            offset += received;
        }

        return true;
    }

    // ---- resolution --------------------------------------------------------

    private async Task<byte[]?> ResolveAsync(byte[] query, CancellationToken ct)
    {
        var name = DnsWire.TryParseQuestionName(query);
        if (name is null)
        {
            _log("dropped a malformed query (no parseable question name)");
            return null;
        }

        var servers = _engine.Match(name);
        if (servers is null)
        {
            _log($"no rule for {name} and no '*' fallback — replying SERVFAIL");
            return null;
        }

        var sw = Stopwatch.StartNew();
        _log($"query {name} (from client) -> rule {servers.Pattern} upstream {servers.Primary}");
        var response = await ForwardUdpAsync(servers.Primary, query, ct);
        _log($"query {name} upstream {servers.Primary}: {(response is null ? "no answer" : $"{response.Length} bytes in {sw.ElapsedMilliseconds} ms")}");
        if (response is null && servers.Secondary is not null)
            response = await ForwardUdpAsync(servers.Secondary, query, ct);

        if (response is null)
        {
            _log($"{name}: no answer from {servers.Primary}{(servers.Secondary is null ? string.Empty : " / " + servers.Secondary)}");
            return null;
        }

        if (DnsWire.IsTruncated(response))
        {
            // Retry over TCP so clients that do not retry on TC still get the full answer.
            var tcp = await ForwardTcpAsync(servers.Primary, query, ct);
            if (tcp is null && servers.Secondary is not null)
                tcp = await ForwardTcpAsync(servers.Secondary, query, ct);
            if (tcp is not null)
                return tcp;
        }

        return response;
    }

    /// <summary>Relays the query bytes verbatim to one upstream over UDP; null on timeout/failure.</summary>
    private static async Task<byte[]?> ForwardUdpAsync(string server, byte[] query, CancellationToken ct)
    {
        if (!IPAddress.TryParse(server.Trim(), out var ip))
            return null;

        try
        {
            using var socket = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(ip, 53));
            await socket.SendAsync(query, SocketFlags.None, ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(UdpTimeout);

            var buffer = new byte[MaxDnsMessage];
            var received = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
            return received > 0 ? buffer[..received] : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Relays the query over TCP (2-byte big-endian length prefix); null on failure.</summary>
    private static async Task<byte[]?> ForwardTcpAsync(string server, byte[] query, CancellationToken ct)
    {
        if (!IPAddress.TryParse(server.Trim(), out var ip))
            return null;

        try
        {
            using var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TcpTimeout);

            await socket.ConnectAsync(new IPEndPoint(ip, 53), cts.Token);

            var frame = new byte[2 + query.Length];
            frame[0] = (byte)(query.Length >> 8);
            frame[1] = (byte)(query.Length & 0xFF);
            query.CopyTo(frame, 2);
            await socket.SendAsync(frame, SocketFlags.None, cts.Token);

            var lengthBuffer = new byte[2];
            if (!await ReadExactWithTokenAsync(socket, lengthBuffer, cts.Token))
                return null;

            var length = (lengthBuffer[0] << 8) | lengthBuffer[1];
            if (length == 0)
                return null;

            var response = new byte[length];
            return await ReadExactWithTokenAsync(socket, response, cts.Token) ? response : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> ReadExactWithTokenAsync(Socket socket, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            int received;
            try
            {
                received = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (received == 0)
                return false;
            offset += received;
        }

        return true;
    }

    /// <summary>12-byte header-only SERVFAIL reply (echoes the query id, sets QR + RA + RCODE 2).</summary>
    private static byte[] BuildServFail(byte[] query)
    {
        var reply = new byte[12];
        if (query.Length > 1)
        {
            reply[0] = query[0];
            reply[1] = query[1];
        }

        reply[2] = (byte)(0x80 | (query.Length > 2 ? query[2] & 0x01 : 0)); // QR + echoed RD
        reply[3] = 0x82; // RA + SERVFAIL
        return reply;
    }
}
