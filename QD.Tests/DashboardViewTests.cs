using QD.Core.Application.Networking;
using QD.Core.Cotracts.DnsObjContracts;
using QuickDNSConsole;
using QuickDNSConsole.Tui;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using QD.Tests.Fakes;

namespace QD.Tests;

/// <summary>
/// Off-screen rendering tests for the dashboard renderables — they verify the TUI
/// view layer produces sane output without needing an interactive terminal.
/// </summary>
public class DashboardViewTests
{
    private static readonly Style Accent = new(new Color(255, 255, 0));

    private static string Render(IRenderable renderable)
    {
        using var console = new TestConsole();
        console.Profile.Width = 110;
        console.Profile.Height = 40;
        console.Write(renderable);
        return console.Output;
    }

    private static AppSettings Settings() => new();

    [Fact]
    public void Header_ShowsAdapterNameAndCurrentPreset()
    {
        var adapter = TestAdapters.Create("eth0", "Ethernet 2", isUp: true, hasGateway: true, "1.1.1.1");
        var preset = new DnsObjViewModel(1, "Cloudflare", "1.1.1.1", "1.0.0.1");

        var output = Render(DashboardView.BuildHeader(adapter, preset, isElevated: true, Accent));

        Assert.Contains("Ethernet 2", output);
        Assert.Contains("Cloudflare", output);
        Assert.Contains("QuickDNS", output);
        Assert.DoesNotContain("not elevated", output);
    }

    [Fact]
    public void Header_WarnsWhenNotElevated()
    {
        var adapter = TestAdapters.Create("eth0", "Ethernet", isUp: true, hasGateway: true);

        var output = Render(DashboardView.BuildHeader(adapter, null, isElevated: false, Accent));

        Assert.Contains("not elevated", output);
    }

    [Fact]
    public void Header_NoAdapter_ShowsError()
    {
        var output = Render(DashboardView.BuildHeader(null, null, isElevated: true, Accent));

        Assert.Contains("No usable", output);
    }

    [Fact]
    public void PresetsTable_ShowsCursorAndActiveMarker()
    {
        var presets = new List<DnsObjViewModel>
        {
            new(1, "Cloudflare", "1.1.1.1", "1.0.0.1"),
            new(2, "Google", "8.8.8.8", "8.8.4.4"),
        };
        var current = presets[1];

        var output = Render(DashboardView.BuildPresetsTable(presets, 1, current, Settings(), Accent));

        Assert.Contains("Cloudflare", output);
        Assert.Contains("Google", output);
        Assert.Contains("❯", output);
    }

    [Fact]
    public void PresetsTable_Empty_ShowsCreateHint()
    {
        var output = Render(DashboardView.BuildPresetsTable([], 0, null, Settings(), Accent));

        Assert.Contains("No presets yet", output);
        Assert.Contains("C", output);
    }

    [Fact]
    public void SidePanel_ShowsAdapterDetailsAndBackend()
    {
        var adapter = TestAdapters.Create("eth0", "Ethernet", isUp: true, hasGateway: true, "1.1.1.1");
        var settings = Settings();
        settings.History.Add(new HistoryEntry { When = DateTime.Now, Text = "Applied Cloudflare" });

        var output = Render(DashboardView.BuildSidePanel(adapter, settings, "WMI test backend", Accent));

        Assert.Contains("Ethernet", output);
        Assert.Contains("WMI test backend", output);
        Assert.Contains("Applied Cloudflare", output);
    }

    [Fact]
    public void Footer_ShowsKeybindingsAndBootTime()
    {
        var output = Render(DashboardView.BuildFooter(null, false, TimeSpan.FromMilliseconds(42), isElevated: true));

        Assert.Contains("apply", output);
        Assert.Contains("adapter", output);
        Assert.Contains("42 ms", output);
    }

    [Fact]
    public void Footer_StatusMessage_IsShownWithErrorStyle()
    {
        var output = Render(DashboardView.BuildFooter("WMI exploded", statusIsError: true, TimeSpan.Zero, isElevated: true));

        Assert.Contains("WMI exploded", output);
        Assert.Contains("error:", output);
    }

    [Fact]
    public void AdapterPicker_ShowsAutoEntryAndAdapters()
    {
        var entries = new List<(string? Id, string Label, AdapterInfo? Adapter)>
        {
            (null, "Auto — detect best adapter (recommended)", null),
            ("eth0", "Ethernet — Realtek", TestAdapters.Create("eth0", "Ethernet", isUp: true, hasGateway: true)),
        };

        var output = Render(DashboardView.BuildAdapterPickerTable(entries, 0, "eth0", Settings(), Accent));

        Assert.Contains("Auto — detect best adapter", output);
        Assert.Contains("Ethernet — Realtek", output);
        Assert.Contains("★", output);
    }

    [Fact]
    public void Renderables_NeverEmitRawMarkupBrackets()
    {
        // Adapter descriptions can contain '[' (e.g. "Wireless-[AC 8265]"); they must be escaped.
        var adapter = new AdapterInfo
        {
            Id = "wifi0",
            Name = "Wi-Fi",
            Description = "Intel Wireless-[AC 8265]",
            Kind = AdapterKind.Wireless,
            IsUp = true,
            HasGateway = true,
        };

        var output = Render(DashboardView.BuildHeader(adapter, null, isElevated: true, Accent));

        Assert.Contains("Wireless-[AC 8265]", output);
    }
}
