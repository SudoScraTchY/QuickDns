# QuickDNS

**Fast, keyboard-first DNS switcher for Windows and Linux** — a single, self-contained console app that applies saved DNS presets to the network adapter you choose, in milliseconds.

```
╭─ QuickDNS v2.0.0 ───────────────────────────────────────────────────────────╮
│ Adapter  Ethernet · Realtek PCIe GbE Family Controller · ● Up              │
│ IP       192.168.1.4                                                       │
│ DNS      1.1.1.1 | 1.0.0.1  Cloudflare                                     │
╰────────────────────────────────────────────────────────────────────────────╯
╭─ DNS Presets ───────────────╮ ╭─ Target adapter ───────────────────────────╮
│   ● Cloudflare  1.1.1.1…    │ │ Type       Ethernet                        │
│ ❯   Google      8.8.8.8…    │ │ Status     Up                              │
│     Quad9       9.9.9.9…    │ │ Interface  12                              │
│     OpenDNS     208.67…     │ │ Addresses  192.168.1.4                     │
│     AdGuard     94.140…     │ │ Gateway    192.168.1.1                     │
╰─────────────────────────────╯ │ Backend    WMI (…SetDNSServerSearchOrder)  │
                                 ╰────────────────────────────────────────────╯
 ready in 31 ms · elevated: yes
 ↑↓ select   Home/End jump   ↵ apply   A adapter   C new   E edit   D delete   U unset   R refresh   Q quit
```

## Highlights

- **Fast boot by design** — no artificial delays, no generic host, no WMI enumeration at startup. The dashboard is ready in tens of milliseconds; everything platform-specific is lazy and created on first use.
- **Per-adapter DNS** — every Ethernet / Wi-Fi adapter is listed with status, IPs and current DNS; pick a target explicitly or let *Auto* choose the adapter that is up with an IPv4 gateway. Your choice persists in `settings.json`.
- **Cross-platform** — Windows (WMI `Win32_NetworkAdapterConfiguration`) and Linux (NetworkManager `nmcli` → `systemd-resolved` `resolvectl` → `/etc/resolv.conf` fallback, auto-detected).
- **Real TUI** — one flicker-free live dashboard (Spectre.Console), section panels, active-preset marker, persisted activity history, inline success/error feedback, validation on every input, Esc cancels anywhere.
- **Safe changes** — deletions ask for confirmation; applying without elevation is detected up front and reported clearly instead of failing with a raw WMI error.
- **First-run friendly** — popular presets (Cloudflare, Google, Quad9, OpenDNS, AdGuard) are seeded automatically.

## Requirements

| | |
|---|---|
| Windows | Windows 10/11, **Run as administrator** to change DNS |
| Linux | Any distro with .NET 9 runtime; root/sudo for DNS changes. Persistent changes need NetworkManager (`nmcli`); otherwise `resolvectl` (runtime-only) or a writable `/etc/resolv.conf` |

## Troubleshooting: "the app won't open at all"

**Run the self-contained build.** If you launch a *framework-dependent* build (e.g. `bin\Debug\net9.0\QuickDNSConsole.exe`) and the machine has a stale `DOTNET_ROOT` environment variable pointing at an older .NET runtime, the exe aborts instantly with "You must install or update .NET to run this application" — before any QuickDNS code runs. This is a launcher-level failure:

- Self-contained single-file builds (CI artifacts, `publish/` output) are immune — they carry their own runtime. Use those.
- If the window flash-closes for any other reason, check `%APPDATA%\QuickDns\crash.log` (Linux: `~/.config/QuickDns/crash.log`) — every startup failure writes the full stack there, and an interactive run stays open with "Press any key to close…" so the error is readable.
- `QuickDNS --diagnostics` is a safe, non-interactive way to verify platform, elevation, DNS backend and preset store health.

## Usage

Launch the app and use the keybar:

| Key | Action |
|---|---|
| `↑` `↓` / `Home` `End` / `PgUp` `PgDn` | Move selection |
| `Enter` | Apply the selected preset to the target adapter |
| `A` | Choose target adapter (or Auto) |
| `C` / `E` / `D` | Create / edit / delete preset (`Y` confirms delete) |
| `U` | Clear DNS — back to automatic (DHCP) |
| `X` / `I` | Export presets to a JSON file / import from one | 
| `R` / `F5` | Refresh adapter + DNS state |
| `Q` / `Esc` | Quit |

Command-line:

```bash
QuickDNS --diagnostics          # platform, elevation, DNS backend, adapters, store — no changes made
QuickDNS --export               # all presets → a new QuickDNS-presets-<stamp>.json in the config dir
QuickDNS --export C:\backup.json
QuickDNS --import C:\backup.json # duplicates (by name or server pair) are skipped
QuickDNS --version
QuickDNS --help
```

### Your DNS list can't be wiped

Three independent safety nets:

1. **Location** — presets live in the per-user config directory, so installing or updating the executable never touches them.
2. **Auto-backup** — every clean start silently refreshes `presets.autobackup.json` (never overwritten while the store is empty).
3. **Self-healing store** — if the SQLite file is ever damaged, it is quarantined as `DnsDB.db.corrupt-<timestamp>`, a fresh store is created and the auto-backup is restored into it automatically at the next start.

Import is de-duplicated (by name and by server pair) and validated (every address must parse as IP), so re-importing after a partial restore is always safe.

## Configuration

Settings and presets live in the per-user config directory:

- Windows: `%APPDATA%\QuickDns\`
- Linux: `~/.config/QuickDns/`

```
settings.json                     theme colors, history limit, target AdapterId (null = Auto), activity history
DnsDB.db (+ DnsDB.db-wal/-shm)    SQLite preset store (WAL journaling)
presets.autobackup.json           silent backup, refreshed on every clean start
QuickDNS-presets-*.json           your manual exports (X key / --export)
crash.log                         written when startup fails
```

QuickDNS 1.x kept `settings.json` / `DnsDB.db` in the working directory; they are migrated automatically on first start (the legacy `Dns` table is imported into the new schema, nothing is lost).

Elevated requirement in short: **Windows** — start the terminal "as administrator"; **Linux** — `sudo QuickDNS` (or a polkit-enabled NetworkManager session).

## Build

Requires the .NET 9 SDK.

```bash
dotnet build QuickDNS.sln
dotnet test  QuickDNS.sln
```

Portable single-file publish (what CI ships):

```bash
dotnet publish QuickDnsConsole/QuickDnsConsole.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true \
  -p:PublishTrimmed=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none -p:DebugSymbols=false -o publish/win-x64

dotnet publish QuickDnsConsole/QuickDnsConsole.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true \
  -p:PublishTrimmed=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none -p:DebugSymbols=false -o publish/linux-x64
```

CI (`.github/workflows/main.yml`) runs the test suite, then publishes `QuickDNS-win-x64.zip` and `QuickDNS-linux-x64.zip`; pushes to `master` refresh the `latest` release.

## Performance notes

Boot cost was the old design's weak spot: 1 s hard-coded sleep, generic-host startup, full WMI enumeration in a constructor, and fire-and-forget SQLite init races. QuickDNS 2.0 removes all four:

- composition root is ~10 lines of `new`, no `Microsoft.Extensions.Hosting`;
- settings are read once, written atomically, and only when something changes;
- the preset store uses short-lived pooled SQLite connections (WAL + busy timeout), so no long-lived file lock and no init races;
- adapters are enumerated with `NetworkInterface.GetAllNetworkInterfaces()` (in-process, ~10 ms); WMI is contacted **only** when applying/clearing DNS, via a scoped `InterfaceIndex` query;
- release builds are trimmed, ReadyToRun single files, with `InvariantGlobalization` and reflection-free JSON (source-generated serializer contexts) for trim safety.

Measured on the dev machine: `--diagnostics` (config → DB → adapter scan) ≈ **130 ms** warm, dashboard footer shows the real ready time each run.

## Project layout

```
QD.Core/
  Application/Networking/    AdapterInfo, AdapterCatalog, INetworkAdapterService,
                             WindowsNetworkAdapterService (WMI), LinuxNetworkAdapterService
  Application/               DnsObjApplication — preset CRUD, adapter-aware apply/clear, export/import
  Domain/                    repository contract
  Infrastructure/SQLite/     DnsDatabase (schema, WAL, migrations, quarantine) + repository
QuickDnsConsole/
  Program.cs                 composition root, --diagnostics/--export/--import, crash log
  AppSettings.cs             settings model + source-generated JSON + atomic persistence
  Tui/                       DnsTui (state machine), DashboardView (renderables), Input
QD.Tests/                    xUnit: application logic (fakes), SQLite store integration
                             (CRUD, migration, corruption quarantine), validation,
                             settings round-trip, off-screen TUI rendering
```

## Roadmap

- macOS support (`networksetup -setdnsservers`) — the `INetworkAdapterService` abstraction is ready for a third implementation.

## License

MIT (as before).
