using QD.Core.Application.Networking;
using QD.Core.Cotracts.DnsObjContracts;
using QD.Core.Helper;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace QuickDNSConsole.Tui;

internal enum DashboardAction
{
    Exit,
    Refresh,
    Create,
    Edit,
    Delete,
    AdapterPicker,
    Export,
    Import,
    Test,
    Benchmark,
}

/// <summary>
/// The main interactive loop. The dashboard lives in a single <see cref="LiveDisplayContext"/>
/// session (no flicker on navigation); modal screens (forms, pickers) temporarily take over
/// the console and return here afterwards. Data fetches are defensive: a transient store or
/// adapter error degrades to a status message instead of killing the app.
/// </summary>
public sealed class DnsTui
{
    private readonly IDnsObjApplication _dnsApp;
    private readonly INetworkAdapterService _adapterService;
    private readonly IDnsProbeService _probeService;
    private readonly AppSettings _settings;
    private readonly TimeSpan _bootTime;

    private int _selectedIndex;
    private string? _statusMessage;
    private bool _statusIsError;

    public DnsTui(
        IDnsObjApplication dnsApp,
        INetworkAdapterService adapterService,
        AppSettings settings,
        TimeSpan bootTime,
        IDnsProbeService probeService,
        string? startupWarning = null)
    {
        _dnsApp = dnsApp ?? throw new ArgumentNullException(nameof(dnsApp));
        _adapterService = adapterService ?? throw new ArgumentNullException(nameof(adapterService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _probeService = probeService ?? throw new ArgumentNullException(nameof(probeService));
        _bootTime = bootTime;

        if (!string.IsNullOrEmpty(startupWarning))
        {
            _statusMessage = startupWarning;
            _statusIsError = true;
        }
    }

    public void Run()
    {
        while (true)
        {
            var presets = FetchPresets();
            _selectedIndex = presets.Count == 0 ? 0 : Math.Clamp(_selectedIndex, 0, presets.Count - 1);

            var adapter = FetchAdapter();
            var currentPreset = FetchCurrentPreset();

            switch (RunDashboard(presets, adapter, currentPreset))
            {
                case DashboardAction.Exit:
                    return;

                case DashboardAction.Create:
                    RunCreateForm();
                    continue;

                case DashboardAction.Edit:
                    RunEditForm(presets[_selectedIndex]);
                    continue;

                case DashboardAction.Delete:
                    RunDeleteForm(presets[_selectedIndex]);
                    continue;

                case DashboardAction.AdapterPicker:
                    RunAdapterPicker();
                    continue;

                case DashboardAction.Test:
                    RunTestForm(presets[_selectedIndex]);
                    continue;

                case DashboardAction.Benchmark:
                    RunBenchmarkForm(presets);
                    continue;

                case DashboardAction.Export:
                    RunExportForm();
                    continue;

                case DashboardAction.Import:
                    RunImportForm();
                    continue;
            }
        }
    }

    // ---- defensive data fetches ---------------------------------------------

    private IReadOnlyList<DnsObjViewModel> FetchPresets()
    {
        try
        {
            return _dnsApp.GetAll();
        }
        catch (Exception ex)
        {
            SetError($"Preset store error: {ex.Message}");
            return [];
        }
    }

    private AdapterInfo? FetchAdapter()
    {
        try
        {
            return _adapterService.ResolveTarget(_settings.AdapterId);
        }
        catch (Exception ex)
        {
            SetError($"Adapter enumeration failed: {ex.Message}");
            return null;
        }
    }

    private DnsObjViewModel? FetchCurrentPreset()
    {
        try
        {
            return _dnsApp.GetCurrentDns(_settings.AdapterId);
        }
        catch (Exception ex)
        {
            SetError($"Could not read current DNS: {ex.Message}");
            return null;
        }
    }

    private void SetError(string message)
    {
        _statusMessage = message;
        _statusIsError = true;
    }

    // ---- dashboard -------------------------------------------------------

    private DashboardAction RunDashboard(
        IReadOnlyList<DnsObjViewModel> presets,
        AdapterInfo? adapter,
        DnsObjViewModel? currentPreset)
    {
        var accent = new Style(_settings.AccentColor.ToSpectreColor());

        var layout = new Layout("Root").SplitRows(
            new Layout("Header").Ratio(3),
            new Layout("Content").Ratio(9),
            new Layout("Footer").Ratio(2));
        layout["Content"].SplitColumns(
            new Layout("Left").Ratio(3),
            new Layout("Right").Ratio(2));

        var action = DashboardAction.Refresh;

        AnsiConsole.Live(layout).Start(ctx =>
        {
            while (true)
            {
                layout["Header"].Update(DashboardView.BuildHeader(
                    adapter, currentPreset, _adapterService.IsElevated, accent));
                layout["Left"].Update(DashboardView.BuildPresetsTable(
                    presets, _selectedIndex, currentPreset, _settings, accent));
                layout["Right"].Update(DashboardView.BuildSidePanel(
                    adapter, _settings, _adapterService.BackendDescription, accent));
                layout["Footer"].Update(DashboardView.BuildFooter(
                    _statusMessage, _statusIsError, _bootTime, _adapterService.IsElevated));
                ctx.Refresh();

                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        if (presets.Count > 0)
                            _selectedIndex = Math.Max(0, _selectedIndex - 1);
                        break;

                    case ConsoleKey.DownArrow:
                        if (presets.Count > 0)
                            _selectedIndex = Math.Min(presets.Count - 1, _selectedIndex + 1);
                        break;

                    case ConsoleKey.Home:
                        _selectedIndex = 0;
                        break;

                    case ConsoleKey.End:
                        if (presets.Count > 0)
                            _selectedIndex = presets.Count - 1;
                        break;

                    case ConsoleKey.PageUp:
                        _selectedIndex = Math.Max(0, _selectedIndex - 5);
                        break;

                    case ConsoleKey.PageDown:
                        if (presets.Count > 0)
                            _selectedIndex = Math.Min(presets.Count - 1, _selectedIndex + 5);
                        break;

                    case ConsoleKey.Enter:
                        if (presets.Count > 0)
                        {
                            ApplyPreset(layout, ctx, presets[_selectedIndex]);
                            action = DashboardAction.Refresh;
                            return;
                        }
                        break;

                    case ConsoleKey.U:
                        UnsetDns(layout, ctx);
                        action = DashboardAction.Refresh;
                        return;

                    case ConsoleKey.A:
                        action = DashboardAction.AdapterPicker;
                        return;

                    case ConsoleKey.C:
                        action = DashboardAction.Create;
                        return;

                    case ConsoleKey.E:
                        if (presets.Count > 0)
                        {
                            action = DashboardAction.Edit;
                            return;
                        }
                        break;

                    case ConsoleKey.D:
                        if (presets.Count > 0)
                        {
                            action = DashboardAction.Delete;
                            return;
                        }
                        break;

                    case ConsoleKey.X:
                        action = DashboardAction.Export;
                        return;

                    case ConsoleKey.I:
                        action = DashboardAction.Import;
                        return;

                    case ConsoleKey.T:
                        if (presets.Count > 0)
                        {
                            action = DashboardAction.Test;
                            return;
                        }
                        break;

                    case ConsoleKey.B:
                        if (presets.Count > 0)
                        {
                            action = DashboardAction.Benchmark;
                            return;
                        }
                        break;

                    case ConsoleKey.R:
                    case ConsoleKey.F5:
                        action = DashboardAction.Refresh;
                        return;

                    case ConsoleKey.Q:
                    case ConsoleKey.Escape:
                        action = DashboardAction.Exit;
                        return;
                }
            }
        });

        return action;
    }

    // ---- inline actions ---------------------------------------------------

    private void ApplyPreset(Layout layout, LiveDisplayContext ctx, DnsObjViewModel preset)
    {
        _statusMessage = $"applying '{preset.Name}'…";
        _statusIsError = false;
        layout["Footer"].Update(DashboardView.BuildFooter(
            _statusMessage, _statusIsError, _bootTime, _adapterService.IsElevated));
        ctx.Refresh();

        var result = Safe(() => _dnsApp.SetDns(preset.Id, _settings.AdapterId));

        _statusIsError = !result.IsSucceeded;
        if (result.IsSucceeded)
        {
            _statusMessage = $"applied '{preset.Name}' → {preset.DnsAddresses}";
            AddHistory($"Applied {preset.Name} ({preset.DnsAddresses})");
        }
        else
        {
            _statusMessage = result.Message;
        }
    }

    private void UnsetDns(Layout layout, LiveDisplayContext ctx)
    {
        _statusMessage = "clearing DNS…";
        _statusIsError = false;
        layout["Footer"].Update(DashboardView.BuildFooter(
            _statusMessage, _statusIsError, _bootTime, _adapterService.IsElevated));
        ctx.Refresh();

        var result = Safe(() => _dnsApp.UnSetDns(_settings.AdapterId));

        _statusIsError = !result.IsSucceeded;
        if (result.IsSucceeded)
        {
            _statusMessage = result.Message;
            AddHistory("Cleared DNS (back to automatic)");
        }
        else
        {
            _statusMessage = result.Message;
        }
    }

    private OperationResult Safe(Func<OperationResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            return new OperationResult().Failed(ex.Message);
        }
    }

    // ---- modal: adapter picker ---------------------------------------------

    private void RunAdapterPicker()
    {
        var adapters = _adapterService.GetAdapters();
        var accent = new Style(_settings.AccentColor.ToSpectreColor());

        var entries = new List<(string? Id, string Label, AdapterInfo? Adapter)>
        {
            (null, "Auto — detect best adapter (recommended)", null),
        };
        entries.AddRange(adapters.Select(a => (
            (string?)a.Id,
            $"{a.Name} — {a.Description}",
            (AdapterInfo?)a)));

        var index = Math.Max(0, entries.FindIndex(e =>
            e.Id is not null && e.Id.Equals(_settings.AdapterId, StringComparison.OrdinalIgnoreCase)));

        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(DashboardView.BuildAdapterPickerTable(
                entries, index, _settings.AdapterId, _settings, accent));

            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    index = Math.Max(0, index - 1);
                    break;

                case ConsoleKey.DownArrow:
                    index = Math.Min(entries.Count - 1, index + 1);
                    break;

                case ConsoleKey.Enter:
                    _settings.AdapterId = entries[index].Id;
                    AppSettingsStore.Save(_settings);
                    _statusMessage = entries[index].Id is null
                        ? "target adapter: auto (detect best)"
                        : $"target adapter: {entries[index].Label}";
                    _statusIsError = false;
                    return;

                case ConsoleKey.Escape:
                    return;
            }
        }
    }

    // ---- modal: export / import ---------------------------------------------

    private void RunExportForm()
    {
        AnsiConsole.Clear();
        WriteFormHeader("-- Export presets --");
        AnsiConsole.MarkupLine(" [grey]json = QuickDNS format · csv = plain name,primary,secondary list[/]");

        var format = Input.Ask("Format json/csv (Enter = json)", defaultValue: "json",
            validator: v => string.Equals(v, "json", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(v, "csv", StringComparison.OrdinalIgnoreCase)
                ? null
                : "Enter json or csv.");
        if (format is null) { Cancelled(); return; }

        var isCsv = format.Equals("csv", StringComparison.OrdinalIgnoreCase);
        var defaultPath = AppPaths.NewExportFile(isCsv ? ".csv" : ".json");
        AnsiConsole.MarkupLine($" [grey]Default file:[/] {Markup.Escape(defaultPath)}");

        var path = Input.Ask("Export file (Enter = default)");
        if (path is null) { Cancelled(); return; }

        var effective = string.IsNullOrWhiteSpace(path) ? defaultPath : path.Trim();
        var result = Safe(() => isCsv ? _dnsApp.ExportPresetsCsv(effective) : _dnsApp.ExportPresets(effective));
        SetStatus(result);
        if (result.IsSucceeded)
            AddHistory($"Exported presets → {Path.GetFileName(effective)}");
        PromptAnyKey();
    }

    private void RunImportForm()
    {
        AnsiConsole.Clear();
        WriteFormHeader("-- Import presets --");

        var files = AppPaths.GetExportFiles();
        if (files.Count == 0)
        {
            SetStatus(new OperationResult().Failed("No export files found — press X to export your presets first."));
            PromptAnyKey();
            return;
        }

        var entries = files.Select(f =>
            (Path: f, Label: $"{Path.GetFileName(f)}  ({new FileInfo(f).LastWriteTime:yyyy-MM-dd HH:mm})")).ToList();
        var index = 0;

        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(BuildImportPickerTable(entries, index));

            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    index = Math.Max(0, index - 1);
                    break;

                case ConsoleKey.DownArrow:
                    index = Math.Min(entries.Count - 1, index + 1);
                    break;

                case ConsoleKey.Enter:
                    var result = Safe(() => _dnsApp.ImportPresets(entries[index].Path));
                    SetStatus(result);
                    if (result.IsSucceeded)
                        AddHistory($"Imported presets ← {Path.GetFileName(entries[index].Path)}");
                    PromptAnyKey();
                    return;

                case ConsoleKey.Escape:
                    Cancelled();
                    return;
            }
        }
    }

    private IRenderable BuildImportPickerTable(IReadOnlyList<(string Path, string Label)> entries, int selectedIndex)
    {
        var accent = new Style(_settings.AccentColor.ToSpectreColor());
        var selectedStyle = new Style(
            _settings.HighlightForeground.ToSpectreColor(),
            _settings.HighlightBackground.ToSpectreColor());

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(accent)
            .Expand();
        table.Title = new TableTitle(
            " [bold] Choose an export file [/] [grey](Enter import · Esc cancel)[/] ", accent);
        table.AddColumn("[u] [/]");
        table.AddColumn("[u]Export file[/]");

        for (var i = 0; i < entries.Count; i++)
        {
            var isSelected = i == selectedIndex;
            if (isSelected)
            {
                table.AddRow(
                    new Markup("[green]❯[/]", selectedStyle),
                    new Markup(Markup.Escape(entries[i].Label), selectedStyle));
            }
            else
            {
                table.AddRow(" ", Markup.Escape(entries[i].Label));
            }
        }

        return table;
    }

    // ---- modal: ping / speed / reachability ----------------------------------

    private void RunTestForm(DnsObjViewModel preset)
    {
        AnsiConsole.Clear();
        WriteFormHeader($"-- Test: {preset.Name} --");
        AnsiConsole.MarkupLine(" [grey]ICMP ping · real DNS A query over UDP 53 · TCP connect to port 53 · 2s timeout each[/]");
        AnsiConsole.WriteLine();

        var servers = new List<string> { preset.FirstDns };
        if (!string.IsNullOrWhiteSpace(preset.SecondDns))
            servers.Add(preset.SecondDns);

        var results = new List<(string Server, DnsProbeResult Result)>();
        foreach (var server in servers)
        {
            AnsiConsole.MarkupLine($" [grey]probing {Markup.Escape(server)} …[/]");
            results.Add((server, ProbeSafely(server)));
        }

        AnsiConsole.Clear();
        WriteFormHeader($"-- Test: {preset.Name} --");
        AnsiConsole.Write(BuildProbeTable(results.Select(r => (string.Empty, r.Server, r.Result))));
        SetStatus(results.All(r => r.Result.Reachable)
            ? new OperationResult().Succeeded($"'{preset.Name}' is reachable")
            : new OperationResult().Failed($"'{preset.Name}' did not answer"));
        PromptAnyKey();
    }

    private void RunBenchmarkForm(IReadOnlyList<DnsObjViewModel> presets)
    {
        AnsiConsole.Clear();
        WriteFormHeader("-- Benchmark presets --");
        AnsiConsole.MarkupLine(" [grey]probing the primary server of every preset …[/]");

        var results = new List<(string Name, DnsProbeResult Result)>();
        var n = 0;
        foreach (var preset in presets)
        {
            n++;
            AnsiConsole.MarkupLine($" [grey]testing {n}/{presets.Count} {Markup.Escape(preset.Name ?? "?")} …[/]");
            results.Add((preset.Name ?? "?", ProbeSafely(preset.FirstDns)));
        }

        AnsiConsole.Clear();
        WriteFormHeader("-- Benchmark: DNS query speed --");
        AnsiConsole.Write(BuildProbeTable(results
            .Select(r => (r.Name, r.Result.Server, r.Result))
            .OrderBy(r => r.Result.DnsMs ?? double.MaxValue)));
        AddHistory($"Benchmarked {results.Count} presets");
        PromptAnyKey();
    }

    private IRenderable BuildProbeTable(IEnumerable<(string Label, string Server, DnsProbeResult Result)> rows)
    {
        var accent = new Style(_settings.AccentColor.ToSpectreColor());
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(accent)
            .Expand();
        table.Title = new TableTitle(" [bold] DNS server probe [/] ", accent);
        table.AddColumn("[u]Preset[/]");
        table.AddColumn("[u]Server[/]");
        table.AddColumn("[u]Ping[/]");
        table.AddColumn("[u]DNS query[/]");
        table.AddColumn("[u]TCP 53[/]");
        table.AddColumn("[u]Verdict[/]");

        foreach (var (label, server, result) in rows)
        {
            table.AddRow(
                Markup.Escape(label),
                Markup.Escape(server),
                result.PingMs is { } p ? $"{p:F0} ms" : "[grey]n/a[/]",
                result.DnsMs is { } d ? $"[green]{d:F0} ms[/]" : "[red]timeout[/]",
                result.TcpMs is { } t ? $"{t:F0} ms" : "[grey]n/a[/]",
                result.Reachable
                    ? $"[green]{(result.DnsMs is not null ? "reachable" : "tcp only")}[/]"
                    : $"[red]unreachable[/] {Markup.Escape(result.Note)}");
        }

        return table;
    }

    /// <summary>Runs a probe, converting unexpected exceptions into an unreachable result.</summary>
    private DnsProbeResult ProbeSafely(string server)
    {
        try
        {
            return _probeService.Probe(server);
        }
        catch (Exception ex)
        {
            return new DnsProbeResult(server, null, null, null, ex.Message);
        }
    }

    // ---- modal: forms -------------------------------------------------------

    private void RunCreateForm()
    {
        AnsiConsole.Clear();
        WriteFormHeader("-- New DNS preset --");

        var name = Input.Ask("Preset name",
            validator: v => string.IsNullOrWhiteSpace(v) ? "Name cannot be empty." : null);
        if (name is null) { Cancelled(); return; }

        var first = Input.Ask("Primary DNS (e.g. 1.1.1.1)",
            validator: v => DnsValidator.IsValidIpAddress(v) ? null : "Enter a valid IPv4/IPv6 address.");
        if (first is null) { Cancelled(); return; }

        var second = Input.Ask("Secondary DNS (Enter = none)",
            validator: v => string.IsNullOrWhiteSpace(v) || DnsValidator.IsValidIpAddress(v)
                ? null
                : "Enter a valid IPv4/IPv6 address (or leave empty).");
        if (second is null) { Cancelled(); return; }

        var result = Safe(() => _dnsApp.Create(new CreateDnsObj(first.Trim(), second.Trim(), name.Trim())));
        SetStatus(result);
        if (result.IsSucceeded)
            AddHistory($"Created {name.Trim()}");
        PromptAnyKey();
    }

    private void RunEditForm(DnsObjViewModel preset)
    {
        AnsiConsole.Clear();
        WriteFormHeader($"-- Edit preset #{preset.Id} --");

        var name = Input.Ask("Preset name", defaultValue: preset.Name,
            validator: v => string.IsNullOrWhiteSpace(v) ? "Name cannot be empty." : null);
        if (name is null) { Cancelled(); return; }

        var first = Input.Ask("Primary DNS", defaultValue: preset.FirstDns,
            validator: v => DnsValidator.IsValidIpAddress(v) ? null : "Enter a valid IPv4/IPv6 address.");
        if (first is null) { Cancelled(); return; }

        var second = Input.Ask("Secondary DNS (Enter = none)", defaultValue: preset.SecondDns,
            validator: v => string.IsNullOrWhiteSpace(v) || DnsValidator.IsValidIpAddress(v)
                ? null
                : "Enter a valid IPv4/IPv6 address (or leave empty).");
        if (second is null) { Cancelled(); return; }

        var result = Safe(() => _dnsApp.Edit(new EditDnsObj(first.Trim(), second.Trim(), name.Trim(), preset.Id)));
        SetStatus(result);
        if (result.IsSucceeded)
            AddHistory($"Edited {name.Trim()}");
        PromptAnyKey();
    }

    private void RunDeleteForm(DnsObjViewModel preset)
    {
        AnsiConsole.Clear();
        WriteFormHeader("-- Delete preset --");
        AnsiConsole.MarkupLine(
            $" Preset [bold]#{preset.Id} {Markup.Escape(preset.Name ?? string.Empty)}[/] [grey]({Markup.Escape(preset.DnsAddresses)})[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(" [red]Delete this preset?[/]  [bold]Y[/] = delete  ·  [bold]N[/]/Esc = cancel");

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Y:
                    var result = Safe(() => _dnsApp.Delete(preset.Id));
                    SetStatus(result);
                    if (result.IsSucceeded)
                    {
                        AddHistory($"Deleted {preset.Name}");
                        _selectedIndex = Math.Max(0, _selectedIndex - 1);
                    }
                    PromptAnyKey();
                    return;

                case ConsoleKey.N:
                case ConsoleKey.Escape:
                    Cancelled();
                    return;
            }
        }
    }

    // ---- helpers -------------------------------------------------------------

    private void AddHistory(string text)
    {
        _settings.History.Insert(0, new HistoryEntry { When = DateTime.Now, Text = text });

        var limit = Math.Max(1, _settings.HistoryLimit);
        if (_settings.History.Count > limit)
            _settings.History.RemoveRange(limit, _settings.History.Count - limit);

        AppSettingsStore.Save(_settings);
    }

    private void SetStatus(OperationResult result)
    {
        _statusMessage = result.Message;
        _statusIsError = !result.IsSucceeded;
    }

    private void Cancelled()
    {
        _statusMessage = "cancelled";
        _statusIsError = false;
    }

    private static void WriteFormHeader(string title)
    {
        AnsiConsole.MarkupLine($"[bold yellow]{Markup.Escape(title)}[/]");
        AnsiConsole.MarkupLine("[grey]Esc at any prompt cancels the form.[/]");
        AnsiConsole.WriteLine();
    }

    private static void PromptAnyKey()
    {
        AnsiConsole.MarkupLine(" [grey]Press any key to return…[/]");
        Console.ReadKey(intercept: true);
    }
}
