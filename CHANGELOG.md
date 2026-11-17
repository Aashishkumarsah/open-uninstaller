# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/) and the project uses [Semantic Versioning](https://semver.org/).

## [0.6.0] - Unreleased

### Added
- Native WinForms desktop app with sidebar navigation, themes (dark, midnight, light) and app icons
- Guided uninstall wizard with `built-in`, `safe`, `moderate` and `advanced` leftover scan modes
- Forced uninstall wizard
- Install tracing (before/after snapshots) with JSON/HTML export and import
- Floating window-targeting crosshair
- Per-file junk scanner, multi-file shredder and drive free-space wipe
- Backup management (restore, delete, purge old backups)
- Autorun launch, kill and bulk invalid-entry cleanup
- Store app cache reset
- Smoke tests, CI workflow and contribution docs

### Changed
- The reference database is now optional and user-supplied; nothing is bundled
- Leftover scans now keep program details captured before uninstall, so they still work after the program removes its own registry entry

### Known issues
- See "Known limitations" in the README
