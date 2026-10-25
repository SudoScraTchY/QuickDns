using QD.Core.Application.Catalog;
using QD.Core.Application.Networking;
using QD.Core.Cotracts.DnsObjContracts;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace QuickDNSConsole.Tui;

/// <summary>Pure render builders for the dashboard. No state, no I/O — easy to tweak.</summary>
internal static class DashboardView
{
    public static IRenderable BuildHeader(
        AdapterInfo? adapter,
        DnsObjViewModel? currentPreset,
        bool isElevated,
        Style accent)
    {
        var lines = new List<IRenderable>();

        if (adapter is null)
        {
            lines.Add(new Markup(" [red]No usable Ethernet/Wi-Fi adapter found.[/]"));
        }
        else
        {
            var status = adapter.IsUp ? "[green]● Up[/]" : "[red]● Down[/]";
            lines.Add(new Markup(
                $" [bold]Adapter[/]  [bold]{Markup.Escape(adapter.Name)}[/] [grey]· {Markup.Escape(adapter.Description)} ·[/] {status}"));
            lines.Add(new Markup(
                $" [bold]IP[/]       [grey]{Markup.Escape(adapter.IpAddresses.Count > 0 ? string.Join(",  ", adapter.IpAddresses) : "—")}[/]"));
        }

        string dnsText;
        if (currentPreset is not null)
        {
            dnsText = $"[green]{Markup.Escape(currentPreset.DnsAddresses)}[/]  [bold]{Markup.Escape(currentPreset.Name ?? "?")}[/]";
        }
        else if (adapter is not null && adapter.DnsServers.Count > 0)
        {
            dnsText = $"[yellow]{Markup.Escape(adapter.DnsSummary)}[/]  [grey](not a saved preset)[/]";
        }
        else
        {
            dnsText = "[grey](automatic / none)[/]";
        }

        lines.Add(new Markup($" [bold]DNS[/]      {dnsText}"));

        if (!isElevated)
            lines.Add(new Markup(
                " [yellow]⚠ not elevated — applying/clearing DNS needs administrator (Windows) or root (Linux)[/]"));

        return new Panel(new Rows(lines))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = accent,
            Expand = true,
            Header = new PanelHeader($" [bold yellow]QuickDNS[/] [grey]v{Program.VersionText}[/] "),
        };
    }

    public static IRenderable BuildPresetsTable(
        IReadOnlyList<DnsObjViewModel> presets,
        int selectedIndex,
        DnsObjViewModel? currentPreset,
        AppSettings settings,
        Style accent)
    {
        var selectedStyle = new Style(
            settings.HighlightForeground.ToSpectreColor(),
            settings.HighlightBackground.ToSpectreColor());

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(accent)
            .Expand();

        table.Title = new TableTitle(" [bold] DNS Presets [/] ", accent);
        table.AddColumn("[u] [/]");
        table.AddColumn("[u]Name[/]");
        table.AddColumn("[u]Servers[/]");

        if (presets.Count == 0)
        {
            table.AddRow(string.Empty, "[grey]No presets yet — press[/] [bold]C[/] [grey]to create one[/]", string.Empty);
            return table;
        }

        for (var i = 0; i < presets.Count; i++)
        {
            var preset = presets[i];
            var isSelected = i == selectedIndex;
            var isActive = currentPreset?.Id == preset.Id;

            var cursor = isSelected ? "[green]❯[/]" : " ";
            var dot = isActive ? "[green]●[/]" : " ";
            var name = Markup.Escape(preset.Name ?? "(no name)");
            var servers = Markup.Escape(preset.DnsAddresses);

            if (isSelected)
            {
                table.AddRow(
                    new Markup($"{cursor}{dot}", selectedStyle),
                    new Markup(name, selectedStyle),
                    new Markup(servers, selectedStyle));
            }
            else
            {
                table.AddRow($"{cursor}{dot}", name, servers);
            }
        }

        return table;
    }

    public static IRenderable BuildSidePanel(AdapterInfo? adapter, AppSettings settings, string backend, Style accent)
    {
        var adapterTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(accent)
            .Expand();
        adapterTable.Title = new TableTitle(" [bold] Target adapter [/] ", accent);
        adapterTable.AddColumn("[u]Key[/]");
        adapterTable.AddColumn("[u]Value[/]");

        if (adapter is null)
        {
            adapterTable.AddRow("[grey]—[/]", "[grey]no adapter[/]");
        }
        else
        {
            adapterTable.AddRow("Type", Markup.Escape(adapter.Kind.ToString()));
            adapterTable.AddRow("Status", adapter.IsUp ? "[green]Up[/]" : "[red]Down[/]");
            adapterTable.AddRow("Interface", Markup.Escape(adapter.InterfaceIndex?.ToString() ?? "—"));
            adapterTable.AddRow("Addresses", Markup.Escape(adapter.IpAddresses.Count > 0 ? string.Join(", ", adapter.IpAddresses) : "—"));
            adapterTable.AddRow("Gateway", Markup.Escape(adapter.GatewayAddresses.Count > 0 ? string.Join(", ", adapter.GatewayAddresses) : "—"));
            adapterTable.AddRow("DNS", Markup.Escape(adapter.DnsSummary));
            adapterTable.AddRow("Backend", Markup.Escape(backend));
        }

        var historyTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(accent)
            .Expand();
        historyTable.Title = new TableTitle(" [bold] Recent activity [/] ", accent);
        historyTable.AddColumn("[u]When[/]");
        historyTable.AddColumn("[u]Event[/]");

        if (settings.History.Count == 0)
        {
            historyTable.AddRow(string.Empty, "[grey]nothing yet[/]");
        }
        else
        {
            foreach (var entry in settings.History.Take(Math.Max(1, settings.HistoryLimit)))
                historyTable.AddRow(entry.When.ToString("MM-dd HH:mm"), Markup.Escape(entry.Text));
        }

        return new Rows(adapterTable, historyTable);
    }

    public static IRenderable BuildFooter(string? statusMessage, bool statusIsError, TimeSpan bootTime, bool isElevated)
    {
        IRenderable statusLine = statusMessage is null
            ? new Markup($" [grey]ready in {bootTime.TotalMilliseconds:F0} ms · elevated: {(isElevated ? "yes" : "no")}[/]")
            : statusIsError
                ? new Markup($" [red]error:[/] {Markup.Escape(statusMessage)}")
                : new Markup($" [green]done:[/] {Markup.Escape(statusMessage)}");

        var keys = string.Join("   ",
            "[b]↑↓[/] select",
            "[b]↵[/] apply",
            "[b]A[/] adapter",
            "[b]G[/] catalog",
            "[b]C[/] new",
            "[b]E[/] edit",
            "[b]D[/] delete",
            "[b]U[/] unset",
            "[b]X[/] export",
            "[b]I[/] import",
            "[b]T[/] test",
            "[b]B[/] bench",
            "[b]R[/] refresh",
            "[b]Q[/] quit");

        return new Rows(statusLine, new Markup(" " + keys));
    }

    /// <summary>Catalog browser table for the optional built-in DNS bundles.</summary>
    public static IRenderable BuildCatalogTable(
        IReadOnlyList<DnsCatalogEntry> entries,
        int selectedIndex,
        string filter,
        AppSettings settings,
        Style accent)
    {
        var selectedStyle = new Style(
            settings.HighlightForeground.ToSpectreColor(),
            settings.HighlightBackground.ToSpectreColor());

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(accent)
            .Expand();
        table.Title = new TableTitle(
            $" [bold] DNS catalog [/] [grey]filter: {Markup.Escape(filter)} (←→ change · Enter import one · A import all · Esc close)[/] ", accent);
        table.AddColumn("[u] [/]");
        table.AddColumn("[u]Group[/]");
        table.AddColumn("[u]Name[/]");
        table.AddColumn("[u]Servers[/]");
        table.AddColumn("[u]Note[/]");

        if (entries.Count == 0)
        {
            table.AddRow(string.Empty, "[grey]nothing in this group[/]", string.Empty, string.Empty, string.Empty);
            return table;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var isSelected = i == selectedIndex;
            var servers = Markup.Escape(string.IsNullOrWhiteSpace(entry.Secondary)
                ? entry.Primary
                : $"{entry.Primary} | {entry.Secondary}");
            var name = Markup.Escape(entry.Name);
            var group = Markup.Escape(entry.Group);
            var note = Markup.Escape(entry.Note);

            if (isSelected)
            {
                table.AddRow(
                    new Markup("❯", selectedStyle),
                    new Markup(group, selectedStyle),
                    new Markup(name, selectedStyle),
                    new Markup(servers, selectedStyle),
                    new Markup(note, selectedStyle));
            }
            else
            {
                table.AddRow(" ", group, name, servers, note);
            }
        }

        return table;
    }

    public static IRenderable BuildAdapterPickerTable(
        IReadOnlyList<(string? Id, string Label, AdapterInfo? Adapter)> entries,
        int selectedIndex,
        string? currentAdapterId,
        AppSettings settings,
        Style accent)
    {
        var selectedStyle = new Style(
            settings.HighlightForeground.ToSpectreColor(),
            settings.HighlightBackground.ToSpectreColor());

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(accent)
            .Expand();
        table.Title = new TableTitle(
            " [bold] Choose target adapter [/] [grey](↑↓ move · Enter choose · Esc cancel)[/] ", accent);
        table.AddColumn("[u] [/]");
        table.AddColumn("[u]Adapter[/]");
        table.AddColumn("[u]Status[/]");
        table.AddColumn("[u]Address · DNS[/]");

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var isSelected = i == selectedIndex;
            var isTarget = entry.Id is not null && entry.Id.Equals(currentAdapterId, StringComparison.OrdinalIgnoreCase);
            var status = entry.Adapter is null ? "[grey]auto[/]" : entry.Adapter.IsUp ? "[green]Up[/]" : "[red]Down[/]";
            var details = entry.Adapter is null
                ? "up + IPv4 gateway first"
                : Markup.Escape($"{entry.Adapter.IpAddresses.FirstOrDefault() ?? "—"} · {entry.Adapter.DnsSummary}");
            var label = Markup.Escape((isTarget ? "★ " : "") + entry.Label);

            if (isSelected)
            {
                table.AddRow(
                    new Markup("❯", selectedStyle),
                    new Markup(label, selectedStyle),
                    new Markup(status, selectedStyle),
                    new Markup(details, selectedStyle));
            }
            else
            {
                table.AddRow(" ", label, status, details);
            }
        }

        return table;
    }
}
