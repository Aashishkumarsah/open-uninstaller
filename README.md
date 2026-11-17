# Open Uninstaller

A free, open-source Windows program uninstaller and leftover cleaner. It ships as a native WinForms desktop app (`OpenUninstaller.exe`) backed by a small Node.js + PowerShell engine, and also exposes a command-line interface.

Everything runs locally. There is no account, no telemetry, and no network access at runtime.

> **Warning:** this tool deletes files and registry keys. It backs up what it removes and asks for confirmation, but you should still review every checked item before cleaning. Read [docs/SAFETY.md](docs/SAFETY.md) first.

## Features

| Module | What it does |
|---|---|
| Dashboard | Counters, drive usage and recent activity at a glance |
| Uninstaller | Lists installed programs with icons, filters by category, batch uninstall, repair, export to CSV/HTML |
| Guided uninstall wizard | Backs up the program's registry key, runs its own uninstaller, then scans for leftovers |
| Leftover scanner | Four modes: `built-in` (no scan), `safe`, `moderate`, `advanced`. Scans folders, shortcuts, registry keys, App Paths and startup values |
| Forced uninstall | Clean up a program that is half-removed or has no working uninstaller |
| Traced programs | Records what an installer changes (before/after snapshot) so it can be removed later |
| Window targeting | Floating crosshair: point at any window to find its program, then uninstall, scan or kill it |
| Autorun manager | Inspect, add, enable, disable, launch, kill and remove startup entries |
| Windows apps | List and remove Store/UWP apps, reset their cache |
| Tracks cleaner | Browser, Office and Windows history cleanup |
| Browser extensions | List and enable/disable Chrome, Edge, Brave, Firefox and BHO add-ons |
| Junk files | Per-file scan of temp, log, dump and cache files |
| Registry optimizer | Finds stale Uninstall, Startup and App Paths entries |
| Backup and restore | Every deletion is backed up first; restore or purge old backups |
| Evidence remover | Multi-pass file shredder and free-space wipe (see the SSD caveat in the safety doc) |
| Windows tools | One-click launcher for built-in Windows maintenance utilities |
| Logs and history | Local operation log in SQLite |
| Settings | Theme (dark, midnight, light), default scan mode, safety toggles |

## Requirements

- Windows 10 or 11
- [Node.js](https://nodejs.org/) 22.5 or newer (uses the built-in `node:sqlite`)
- Windows PowerShell 5.1 (already included in Windows)
- To build the desktop app: nothing extra. It uses the C# compiler (`csc.exe`) that ships with .NET Framework 4.x on Windows.

There are no third-party npm packages.

## Run

```powershell
git clone https://github.com/Aashishkumarsah/open-uninstaller.git
cd open-uninstaller
.\build-native.cmd      # produces OpenUninstaller.exe
.\OpenUninstaller.exe
```

Or use the CLI without building anything:

```powershell
node bin\ou.js help
node bin\ou.js list
node bin\ou.js leftovers "Some Program"
```

Optional user-level install (adds an `ou` launcher and Start Menu shortcut, no admin needed; undo with `.\uninstall.ps1`):

```powershell
.\install.ps1
```

Some operations (anything under `HKLM`, Program Files, services) need an elevated process. Start the app as administrator when you need them.

## Project layout

```text
bin/        CLI entry point (ou.js)
native/     C# WinForms desktop app (single source file)
src/        Node.js engine: inventory, leftovers, tracer, autorun, junk, registry, ...
ui/         Optional local browser UI (ou ui)
tests/      Node test runner smoke tests (pure functions only)
docs/       Architecture and safety documentation
data/       Runtime data created on first run (git-ignored)
```

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how the desktop app talks to the engine.

## Tests

```powershell
npm test
```

The tests cover pure helper functions only. They do not touch your registry or files. The desktop app has a built-in smoke check: `OpenUninstaller.exe --self-test` loads every tab and writes `data/selftest.json` plus screenshots.

## Known limitations

- Install tracing uses before/after snapshots, not a kernel driver, so it can miss short-lived changes and picks up unrelated changes made while it is running.
- Leftover detection is name-based heuristics. It can miss items and, rarely, suggest unrelated ones. That is why nothing is deleted without confirmation and a backup.
- The executable is not code-signed, so Windows SmartScreen or antivirus software may warn about it.
- Windows only.

## Optional reference database

The Logs tab can search a read-only SQLite file you provide yourself. It is off by default and nothing is bundled. Set the `OU_REFERENCE_DB` environment variable or `referenceDbPath` in `data/settings.json`.

## Contributing

Contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Security issues: see [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE). The software is provided as is, without warranty of any kind. You are responsible for what you delete.
