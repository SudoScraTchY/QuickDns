using QD.Core.Application;
using QD.Core.Application.Networking;
using QD.Core.Cotracts.DnsObjContracts;
using QD.Tests.Fakes;

namespace QD.Tests;

public class DnsWireTests
{
    [Fact]
    public void BuildQuery_HasHeaderAndQuestion()
    {
        var query = DnsWire.BuildQuery("google.com", 0x1234);

        Assert.True(query.Length > 17);
        Assert.Equal(0x12, query[0]);            // id high
        Assert.Equal(0x34, query[1]);            // id low
        Assert.Equal(0x01, query[2]);            // flags: RD
        Assert.Equal(0x00, query[3]);
        Assert.Equal(0x00, query[4]);            // QDCOUNT = 1
        Assert.Equal(0x01, query[5]);
        Assert.Equal(0x00, query[^4]);           // QTYPE = A
        Assert.Equal(0x01, query[^3]);
        Assert.Equal(0x00, query[^2]);           // QCLASS = IN
        Assert.Equal(0x01, query[^1]);
        Assert.Equal(0x00, query[^5]);           // root label terminator
    }

    [Fact]
    public void BuildQuery_RejectsOversizedLabel()
    {
        Assert.Throws<ArgumentException>(() => DnsWire.BuildQuery(new string('a', 64) + ".com", 1));
    }

    [Fact]
    public void IsValidResponse_AcceptsMatchingResponse()
    {
        var query = DnsWire.BuildQuery("google.com", 0xABCD);
        var response = new byte[] { 0xAB, 0xCD, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0, 1, 2, 3, 4 };

        Assert.True(DnsWire.IsValidResponse(response, query));
    }

    [Fact]
    public void IsValidResponse_RejectsWrongId()
    {
        var query = DnsWire.BuildQuery("google.com", 0x1234);
        var response = new byte[] { 0xFF, 0xFF, 0x81, 0x80, 0, 0, 0, 0, 0, 0, 0, 0 };

        Assert.False(DnsWire.IsValidResponse(response, query));
    }

    [Fact]
    public void IsValidResponse_RejectsQueryPacketAndGarbage()
    {
        var query = DnsWire.BuildQuery("google.com", 0x1234);

        // A packet with the QR flag unset is not a response.
        var notResponse = new byte[] { 0x12, 0x34, 0x01, 0x00, 0, 0, 0, 0, 0, 0, 0, 0 };
        Assert.False(DnsWire.IsValidResponse(notResponse, query));

        Assert.False(DnsWire.IsValidResponse(null, query));
        Assert.False(DnsWire.IsValidResponse(new byte[] { 1, 2, 3 }, query));
    }
}

public class BulkImportExportTests
{
    private static (DnsObjApplication App, FakeDnsObjRepository Repo, FakeAdapterService Adapters) Build()
    {
        var repo = new FakeDnsObjRepository();
        var adapters = new FakeAdapterService();
        return (new DnsObjApplication(repo, adapters), repo, adapters);
    }

    private static string TempPath(string suffix) =>
        Path.Combine(Path.GetTempPath(), $"quickdns-tests-{Guid.NewGuid():N}{suffix}");

    [Fact]
    public void Import_LineFormat_NamedUnnamedAndSemicolonRows()
    {
        var file = TempPath(".txt");
        try
        {
            File.WriteAllText(file,
                "# my dns list\n" +
                "Cloudflare,1.1.1.1,1.0.0.1\n" +
                "9.9.9.9,149.112.112.112\n" +
                "8.8.8.8\n" +
                "\n" +
                "Quad9;192.168.1.53\n" +
                "NameOnly\n");

            var (app, repo, _) = Build();
            var import = app.ImportPresets(file);

            Assert.True(import.IsSucceeded, import.Message);
            Assert.Equal(4, repo.Store.Count);
            Assert.Contains(repo.Store, p => p.Name == "Cloudflare" && p.FirstDns == "1.1.1.1" && p.SecondDns == "1.0.0.1");
            Assert.Contains(repo.Store, p => p.Name == "9.9.9.9" && p.SecondDns == "149.112.112.112");
            Assert.Contains(repo.Store, p => p.Name == "8.8.8.8" && p.FirstDns == "8.8.8.8");
            Assert.Contains(repo.Store, p => p.Name == "Quad9" && p.FirstDns == "192.168.1.53");
            Assert.Contains("ignored 1 invalid row", import.Message); // NameOnly has no primary
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Import_AutoDetectsJsonStill()
    {
        var file = TempPath(".json");
        try
        {
            File.WriteAllText(file, """
                {
                  "App": "QuickDNS",
                  "Schema": 1,
                  "Presets": [
                    { "Name": "Cloudflare", "PrimaryDns": "1.1.1.1", "SecondaryDns": "1.0.0.1" }
                  ]
                }
                """);

            var (app, repo, _) = Build();
            var import = app.ImportPresets(file);

            Assert.True(import.IsSucceeded, import.Message);
            Assert.Single(repo.Store);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void CsvExport_Import_RoundTrip()
    {
        var file = TempPath(".csv");
        try
        {
            var (source, sourceRepo, _) = Build();
            sourceRepo.AddPreset("Cloudflare", "1.1.1.1", "1.0.0.1");
            sourceRepo.AddPreset("Quad9", "9.9.9.9", "");

            var export = source.ExportPresetsCsv(file);
            Assert.True(export.IsSucceeded, export.Message);

            var text = File.ReadAllText(file);
            Assert.StartsWith("name,primary_dns,secondary_dns", text);

            var (target, targetRepo, _) = Build();
            var import = target.ImportPresets(file);

            Assert.True(import.IsSucceeded, import.Message);
            Assert.Equal(2, targetRepo.Store.Count);
            Assert.Contains(targetRepo.Store, p => p.Name == "Cloudflare" && p.SecondDns == "1.0.0.1");
            Assert.Contains(targetRepo.Store, p => p.Name == "Quad9" && p.FirstDns == "9.9.9.9");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Import_SkipsDuplicatesAcrossFormats()
    {
        var file = TempPath(".txt");
        try
        {
            var (app, repo, _) = Build();
            repo.AddPreset("Cloudflare", "1.1.1.1", "1.0.0.1");

            File.WriteAllText(file, "Cloudflare,1.1.1.1,1.0.0.1\n");
            var import = app.ImportPresets(file);

            Assert.True(import.IsSucceeded);
            Assert.Single(repo.Store);
            Assert.Contains("skipped 1 duplicate", import.Message);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
