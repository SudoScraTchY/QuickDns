# QuickDNS

**A fast, keyboard-first DNS switcher for Windows and Linux.**
Pick a saved DNS preset, pick an adapter, press Enter — done. One self-contained executable, no installer, no runtime dependencies.

[![Build QuickDns](https://github.com/SudoScraTchY/QuickDns/actions/workflows/main.yml/badge.svg)](https://github.com/SudoScraTchY/QuickDns/actions/workflows/main.yml)
[![Project website](https://img.shields.io/badge/project%20website-sudoscratchy.github.io%2FQuickDns-0b0c10)](https://sudoscratchy.github.io/QuickDNS/)

```
╭─ QuickDNS v2.1.0 ──────────────────────────────────────────────────────────╮
│ Adapter  Ethernet · Realtek PCIe GbE Family Controller · ● Up              │
│ IP       192.168.1.4                                                       │
│ DNS      1.1.1.1 | 1.0.0.1  Cloudflare                                     │
╰────────────────────────────────────────────────────────────────────────────╯
╭─ DNS Presets ───────────────╮ ╭─ Target adapter ───────────────────────────╮
│   ● Cloudflare  1.1.1.1…    │ │ Type       Ethernet                        │
│ ❯   Google      8.8.8.8…    │ │ Status     Up                              │
│     Quad9       9.9.9.9…    │ │ Gateway    192.168.1.1                     │
│     OpenDNS     208.67…     │ │ DNS        1.1.1.1, 1.0.0.1                │
│     AdGuard     94.140…     │ │ Backend    WMI (…SetDNSServerSearchOrder)  │
╰─────────────────────────────╯ ╰────────────────────────────────────────────╯
 done: applied 'Google' → 8.8.8.8 | 8.8.4.4
 ↑↓ select  ↵ apply  A adapter  C new  E edit  D delete  U unset  X export  I import  R refresh  Q quit
```

## Features

- **Instant switching** — apply, edit, delete and unset (back to DHCP) DNS presets from a live dashboard; the active preset is marked with ●
- **Adapter targeting** — any Ethernet/Wi-Fi adapter can be the target; pick one explicitly (persisted) or let *Auto* choose the adapter that is up with an IPv4 gateway
- **Cross-platform** — Windows via WMI; Linux via NetworkManager (`nmcli`, persistent), `systemd-resolved` (`resolvectl`), or `/etc/resolv.conf` fallback — auto-detected
- **Fast boot** — ready in tens of milliseconds; trimmed, ReadyToRun, self-contained single file
- **Update-proof data** — presets live in your user profile, an auto-backup is refreshed on every clean start, and a corrupted store self-heals from it
- **Portable presets** — export/import to versioned JSON (`X`/`I` keys or CLI), de-duplicated and validated on import
- **First-run friendly** — ships with Cloudflare, Google, Quad9, OpenDNS and AdGuard presets
- **Honest feedback** — IP validation on input, confirm-before-delete, elevation warnings, inline success/error messages, `crash.log` when anything fails

## Download

Grab the latest build from the **[latest release](https://github.com/SudoScraTchY/QuickDns/releases/tag/latest)** (or the workflow artifacts). Every zip is a **self-contained** single-file executable — no .NET installation needed. Unzip and run; that's the install. Each zip has a matching `.sha256` checksum file.

| File | For |
|---|---|
| `QuickDNS-win-x64.zip` | Windows 10/11 (x64) |
| `QuickDNS-win-x86.zip` | Windows (32-bit x86) |
| `QuickDNS-win-arm64.zip` | Windows on ARM |
| `QuickDNS-linux-x64.zip` | Linux, glibc distros (x64) |
| `QuickDNS-linux-arm64.zip` | Linux, glibc (ARM64 — Raspberry Pi 5, Graviton…) |
| `QuickDNS-linux-arm.zip` | Linux, glibc (ARM32) |
| `QuickDNS-linux-musl-x64.zip` | Alpine & other musl distros (x64) |
| `QuickDNS-linux-musl-arm64.zip` | Alpine & other musl distros (ARM64) |

macOS builds are not published yet — the app refuses to run there until the adapter service gains a macOS backend, and we don't ship binaries that can't work.

## Usage

```bash
QuickDNS                          # launch the interactive dashboard
QuickDNS --diagnostics            # platform, elevation, DNS backend, adapters, store — read-only
QuickDNS --export [path]          # all presets → portable JSON (default: new file in the config dir)
QuickDNS --import <path>          # import presets; duplicates (name or server pair) are skipped
QuickDNS --version | --help
```

Dashboard keys:

| Key | Action |
|---|---|
| `↑` `↓` · `Home` `End` · `PgUp` `PgDn` | Move selection |
| `Enter` | Apply the selected preset to the target adapter |
| `A` | Choose target adapter (or Auto) |
| `C` / `E` / `D` | Create / edit / delete preset (`Y` confirms delete) |
| `U` | Clear DNS — back to automatic (DHCP) |
| `X` / `I` | Export presets to JSON / import from an export file |
| `R` / `F5` | Refresh adapter + DNS state |
| `Q` / `Esc` | Quit |

**Changing DNS requires elevation** — start the terminal *as administrator* on Windows, or run with `sudo` on Linux. Without it, QuickDNS tells you up front instead of failing with a raw system error.

## Configuration & data

Everything lives in your user profile, so reinstalling or updating the executable never touches your data:

| | Windows | Linux |
|---|---|---|
| Config directory | `%APPDATA%\QuickDns\` | `~/.config/QuickDns/` |

```
settings.json                    theme colors, history limit, target adapter (null = Auto), activity log
DnsDB.db (+ -wal/-shm)           SQLite preset store (WAL journaling)
presets.autobackup.json          silent backup, refreshed on every clean start
QuickDNS-presets-*.json          your manual exports
crash.log                        written if startup ever fails
```

**Why an update can't wipe your DNS list:**

1. Presets live in the config directory above, independent of the executable.
2. Every clean start silently refreshes the auto-backup (never overwritten while the store is empty).
3. If the SQLite store is ever damaged, it is quarantined (`DnsDB.db.corrupt-<timestamp>`), a fresh store is created and the auto-backup is restored automatically.

Coming from QuickDNS 1.x? Your old `settings.json` / `DnsDB.db` from the working directory are migrated on first start — the legacy table is imported row by row, nothing is lost.

## Troubleshooting

**"The app won't open at all"** — run the self-contained build. If you launch a framework-dependent build (e.g. `bin\Debug\...`) while the machine has a stale `DOTNET_ROOT` environment variable pointing at an older .NET runtime, the launcher aborts with *"You must install or update .NET"* before QuickDNS even starts. The release zips above are immune.

**"Something failed at startup"** — the window stays open with the error, and the full stack trace is in `crash.log` in the config directory. `--diagnostics` is always safe to run.

**Linux backends** — DNS changes are persistent with NetworkManager; `resolvectl` changes last until reboot/network restart; the `/etc/resolv.conf` fallback refuses to touch a systemd-resolved symlink (by design — use NetworkManager instead).

## Build

Requires the .NET 9 SDK.

```bash
dotnet build QuickDNS.sln
dotnet test  QuickDNS.sln
```

Portable single-file publish (exactly what CI does):

```bash
dotnet publish QuickDnsConsole/QuickDnsConsole.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true \
  -p:PublishTrimmed=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none -p:DebugSymbols=false -o publish/win-x64
# same with -r linux-x64 for Linux
```

CI runs the test suite on Windows and Linux, then builds every target in the matrix (self-contained, trimmed, ReadyToRun single files with SHA256 checksums). Pushes to `main` refresh the rolling `latest` release; `v*` tags produce versioned releases with the tag version stamped into the executable.

## Performance notes

The 1.x design paid a second of startup tax: a hard-coded `Thread.Sleep(1000)`, a generic host, full WMI enumeration in a constructor, and fire-and-forget database init. 2.x removes all of it:

- composition root is ~10 lines of `new` — no `Microsoft.Extensions.Hosting`
- SQLite opens lazily with short-lived pooled connections (WAL + busy timeout), no init races
- WMI is contacted **only** when applying/clearing DNS, via a scoped `InterfaceIndex` query
- trimmed ReadyToRun single file, reflection-free JSON, `InvariantGlobalization`

Measured on the dev machine: full `--diagnostics` (config → store → adapter scan) ≈ **130 ms**; the dashboard footer shows the real ready time on every run.

## Project layout

```
QD.Core/
  Application/Networking/    adapter abstraction: Windows (WMI) + Linux (nmcli/resolvectl) services
  Application/               preset CRUD, adapter-aware apply/clear, export/import
  Infrastructure/SQLite/     store: schema, WAL, migrations, corruption quarantine
QuickDnsConsole/
  Program.cs                 composition root, CLI, crash log
  Tui/                       dashboard state machine, renderables, input
QD.Tests/                    application logic (fakes), SQLite integration, TUI rendering
```

## Version history

- **2.1** — store redesign (WAL, migrations, self-healing), export/import + auto-backup, crash diagnostics, multi-platform CI (Windows x64/x86/ARM64, Linux x64/ARM64/ARM32/musl)
- **2.0** — cross-platform rewrite: new TUI, adapter targeting, fast boot, adapter services for Windows & Linux

## Roadmap

- macOS support (`networksetup`) — the adapter service abstraction is ready for a third implementation.

## License

No license file yet — until one is added, all rights are reserved by the repository owner.
