# Architecture

Open Uninstaller has three layers. Each can be used on its own.

```text
 OpenUninstaller.exe (C#, WinForms)        browser UI (ui/, optional)
            |                                       |
            | spawns node per call                  | HTTP on 127.0.0.1
            v                                       v
       src/bridge.js  <---- same modules ---->  src/server.js
            \                                      /
             +------------- src/*.js engine ------+
                                |
                 PowerShell (-EncodedCommand), reg.exe, SQLite
```

## Desktop app (`native/OpenUninstaller.cs`)

A single C# 5 source file compiled by the `csc.exe` that ships with Windows (`build-native.cmd`). It owns the windows, grids and dialogs. All data and actions come from the engine.

It calls `node src/bridge.js <data|action> <name> -`, writes a JSON request to stdin and reads `{ "ok": true, "data": ... }` or `{ "ok": false, "error": ... }` from stdout. Each call is logged with timing to `data/app.log`.

`OpenUninstaller.exe --self-test` loads every tab, runs a few engine probes and writes `data/selftest.json` and screenshots to `data/screenshots/`.

## Engine (`src/`)

| File | Responsibility |
|---|---|
| `util.js` | PowerShell runner (`-EncodedCommand`, UTF-16LE), `psLiteral` escaping, protected path/registry-key guards |
| `inventory.js` | Reads Uninstall keys (HKLM 64/32-bit, HKCU), categorizes programs, exports CSV/HTML/TXT, removes a stale entry (with backup) |
| `uninstall.js` | Parses and launches a program's own uninstall command |
| `leftovers.js` | The `safe`/`moderate`/`advanced` scanners and `cleanLeftovers` (backup, then delete) |
| `tracer.js` | Before/after snapshot diff stored in SQLite; JSON/HTML export and import |
| `hunter.js` | Process and shortcut (.lnk) to program lookup |
| `autorun.js`, `winapps.js`, `extensions.js`, `tracks.js`, `junk.js`, `registry.js` | One module per feature area |
| `backup.js`, `restore.js` | `.reg` exports and folder backups under `data/backups/` |
| `evidence.js` | Multi-pass shredder, free-space wipe, drive listing |
| `history.js`, `settings.js` | Local SQLite operation log and `data/settings.json` |
| `bridge.js`, `server.js` | The two front doors: stdin/stdout JSON and local HTTP |

## Leftover scan modes

- `built-in`: run the program's own uninstaller only.
- `safe`: the program's known install folder, exact vendor/product folder names, matching shortcuts, the program's own registry keys.
- `moderate`: adds name matching two levels deep in common data folders and the `Software` hives, App Paths, and startup values.
- `advanced`: adds deeper user-data folders, Temp, Prefetch entries, MuiCache, compatibility-assistant values, and services pointing into the install folder.

Program details (install location, publisher, uninstall string, registry key) are captured **before** the uninstaller runs and passed to the scan, because many uninstallers delete their own registry entry.

## Data

Everything the app writes lives in `data/` (git-ignored): `history.sqlite`, `settings.json`, `backups/`, `exports/`, `app.log`, `active-trace.json`.
