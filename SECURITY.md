# Security Policy

## Reporting a vulnerability

Please do **not** open a public issue for security problems. Use GitHub's private vulnerability reporting ("Security" tab, then "Report a vulnerability") on this repository.

Include what you found, how to reproduce it, and the impact. We aim to acknowledge reports within 7 days.

## Scope

This tool runs with the privileges of the user who starts it and can delete files and registry keys. Examples of in-scope issues:

- Bypassing the protected-path or protected-registry-key guards in `src/util.js`
- Command injection through program names, paths or registry values that reach PowerShell
- Path traversal in backup, restore or export features
- Writing outside the project's `data/` directory without user action

## Supported versions

Only the latest release receives fixes.
