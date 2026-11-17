using QD.Core.Application.Networking;
using QD.Core.Helper;
using QuickDNSConsole;

namespace QD.Tests;

public class CliAndProbeTests
{
    // ---- CliIntegration PATH helpers (OS-neutral: separators are chosen at runtime) ----

    private static string Sep => OperatingSystem.IsWindows() ? ";" : ":";

    [Fact]
    public void SplitPathEntries_SplitsAndTrims_AndDropsEmpty()
    {
        Assert.Equal(["/x", "/y"], CliIntegration.SplitPathEntries($" /x {Sep}{Sep} /y {Sep} "));
        Assert.Empty(CliIntegration.SplitPathEntries(null));
        Assert.Empty(CliIntegration.SplitPathEntries(""));
    }

    [Fact]
    public void MergePathEntry_Prepends_AndRemovesDuplicates()
    {
        Assert.Equal(["/new", "/one", "/two"], CliIntegration.MergePathEntry(["/one", "/two"], "/new"));

        // An existing duplicate (any position) is dropped; the dir ends up first.
        Assert.Equal(["/new", "/x", "/y"], CliIntegration.MergePathEntry(["/x", "/new", "/y"], "/new"));
    }

    [Fact]
    public void RemovePathEntry_KeepsOrder()
    {
        Assert.Equal(["/a", "/b"], CliIntegration.RemovePathEntry(["/a", "/gone", "/b"], "/gone"));
        Assert.Equal(["/a"], CliIntegration.RemovePathEntry(["/a"], "/missing"));
    }

    [Fact]
    public void PathContains_IgnoresTrailingSlash_AndRespectsOsCaseRules()
    {
        Assert.True(CliIntegration.PathContains(["/programs/quickdns/"], "/programs/quickdns"));
        Assert.False(CliIntegration.PathContains(["/other"], "/programs/quickdns"));

        // Windows PATH is case-insensitive; Linux is case-sensitive.
        Assert.Equal(OperatingSystem.IsWindows(),
            CliIntegration.PathContains(["/Programs/QuickDns"], "/programs/quickdns"));
    }

    [Fact]
    public void InstallDirectory_IsPerUser_AndCommandIsLowercase()
    {
        Assert.False(string.IsNullOrWhiteSpace(CliIntegration.InstallDirectory));
        Assert.Equal("quickdns", CliIntegration.CommandName);
    }

    // ---- DnsValidator.IsValidHostname ------------------------------------------

    [Theory]
    [InlineData("google.com", true)]
    [InlineData("example.org.", true)]           // trailing dot is tolerated
    [InlineData("a-b.example.co", true)]
    [InlineData("localhost", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("bad label.example.com", false)]  // space
    [InlineData("-startdash.example.com", false)]
    [InlineData("enddash-.example.com", false)]
    [InlineData("exa_mple.com", false)]           // underscore
    [InlineData("m\u00fcnchen.de", false)]        // non-ASCII
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.com", false)] // 65-char label
    public void IsValidHostname_CoversTheBasics(string value, bool expected)
    {
        Assert.Equal(expected, DnsValidator.IsValidHostname(value));
    }

    // ---- DnsWire response parsing ----------------------------------------------

    [Fact]
    public void ParseResponse_ExtractsARecords_AndRcode()
    {
        var query = DnsWire.BuildQuery("example.com", 0x1234, DnsWire.QtypeA);
        var response = BuildDnsResponse(query, rcode: 0, addresses: ["93.184.216.34"]);

        var (rcode, addresses) = DnsWire.ParseResponse(response, query);
        Assert.Equal(0, rcode);
        Assert.Equal(["93.184.216.34"], addresses);
    }

    [Fact]
    public void ParseResponse_ReportsNxdomain_WithNoAddresses()
    {
        var query = DnsWire.BuildQuery("missing.example", 0x0102, DnsWire.QtypeA);
        var response = BuildDnsResponse(query, rcode: 3, addresses: []);

        var (rcode, addresses) = DnsWire.ParseResponse(response, query);
        Assert.Equal(3, rcode);
        Assert.Empty(addresses);
    }

    [Fact]
    public void IsValidResponse_RejectsMismatchedTransactionIds()
    {
        var query = DnsWire.BuildQuery("example.com", 0x1111, DnsWire.QtypeA);
        var response = BuildDnsResponse(DnsWire.BuildQuery("example.com", 0x2222, DnsWire.QtypeA), 0, []);

        Assert.False(DnsWire.IsValidResponse(response, query));
    }

    /// <summary>Builds a minimal but well-formed DNS response around a query's question section.</summary>
    private static byte[] BuildDnsResponse(byte[] query, int rcode, IReadOnlyList<string> addresses)
    {
        var answerRecords = new List<byte[]>();
        foreach (var address in addresses)
        {
            var bytes = System.Net.IPAddress.Parse(address).GetAddressBytes();

            // Name pointer to offset 12 (the question name) + type A + class IN
            // + 32-bit TTL + 16-bit RDLENGTH + rdata.
            var record = new List<byte> { 0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x0E, 0x10, 0x00, (byte)bytes.Length };
            record.AddRange(bytes);
            answerRecords.Add([.. record]);
        }

        using var builder = new MemoryStream();
        builder.Write([query[0], query[1]]);                                     // transaction id
        builder.Write([0x81, (byte)(0x80 | rcode)]);                             // QR + recursion + rcode
        builder.Write([0x00, 0x01, 0x00, (byte)answerRecords.Count, 0x00, 0x00, 0x00, 0x00]); // counts
        builder.Write(query, 12, query.Length - 12);                             // question section
        foreach (var record in answerRecords)
            builder.Write(record);

        return builder.ToArray();
    }
}
