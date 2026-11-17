# Contributing

Thanks for helping improve Open Uninstaller.

## Ground rules

1. **Original work only.** Do not submit code, text, icons, screenshots, database schemas or data copied from other products (commercial or otherwise) unless the license clearly allows it and you note the source in the pull request. Do not decompile or disassemble software whose license forbids it.
2. **No other products' branding.** Do not use another product's name, logo or marketing wording in code, UI or docs.
3. **Safety first.** Anything that deletes files or registry keys must: check the protected-path/key guards in `src/util.js`, back up before deleting, and require user confirmation in the UI.
4. **No telemetry, no network calls at runtime.**

## Setup

- Windows 10/11, Node.js 22.5+, Windows PowerShell 5.1
- `.\build-native.cmd` to build the desktop app
- `npm test` to run the tests

## Code style

- Engine (`src/`, `bin/`, `ui/`): CommonJS, `'use strict'`, no third-party dependencies. Two-space indent.
- Desktop app (`native/OpenUninstaller.cs`): the built-in Windows compiler only supports **C# 5**. That means no `$""` string interpolation, no `?.`, no expression-bodied members and no `nameof`. Use `string.Format` or `+`.
- Run PowerShell through the helpers in `src/util.js` (`runPS`, `runPSJson`, `psLiteral`). Never build commands by raw string concatenation of user input.

## Making a change

1. Fork and create a branch.
2. Keep the change focused. Add or update a test in `tests/` when you add a pure function.
3. Run `npm test` and `npm run check`.
4. If you changed the desktop app, build it and run `OpenUninstaller.exe --self-test`, then say what you tested in the pull request.
5. Open a pull request using the template.

## Reporting bugs

Use the bug report template. Include your Windows version and Node version, what you clicked, and the relevant lines of `data/app.log`. Remove personal paths first.
