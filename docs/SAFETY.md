# Safety

Open Uninstaller can delete files and registry keys. Please read this before using it on a machine you care about.

## What is protected

- `src/util.js` refuses to delete system roots (`C:\Windows`, `C:\Program Files`, user profile roots, drive roots) and top-level registry containers (hive roots, the `Uninstall` container, `Run`, `App Paths`, `Services`, and similar).
- When removing a single registry **value** (for example a startup entry), the parent key is never deleted.
- Actions that delete ask for confirmation in the desktop app.

## What is backed up

| Action | Backup before delete? |
|---|---|
| Leftover cleanup (files, registry keys and values) | Yes, under `data/backups/leftovers-*` |
| Registry optimizer | Yes, `.reg` export under `data/backups/regopt-*` |
| Remove stale Uninstall entry | Yes, `.reg` export |
| Guided uninstall | The program's Uninstall key is exported first |
| Junk file cleaner | **No.** Files are deleted permanently |
| Tracks cleaner | **No.** History and caches are deleted permanently |
| Evidence remover (shredder, free-space wipe) | **No, by design.** Deletion is irreversible |
| Store app removal | **No** |

Restoring a registry backup imports its `.reg` file; restoring files means copying them back from the backup folder.

## Things to know

- Leftover detection is heuristic (name matching). Review the checked items. Uncheck anything you are not sure about.
- Shredding cannot guarantee physical erasure on SSDs/NVMe (wear leveling) or on journaled/compressed/snapshotted volumes. For sensitive data use full-disk encryption.
- Changes under `HKLM`, Program Files and services need an elevated process.
- Create a Windows restore point before running `advanced` scans plus a cleanup on important machines.
- The software is provided as is, without warranty (see `LICENSE`).
