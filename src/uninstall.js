'use strict';
const { findPrograms } = require('./inventory');
const { record } = require('./history');
const { runPSJson, psLiteral } = require('./util');

function splitCommand(line) {
  const s = String(line || '').trim();
  const m = s.match(/^"([^"]+)"\s*(.*)$/);
  if (m) return { exe: m[1], args: parseArgs(m[2]), rawArgs: m[2] };
  const parts = s.split(/\s+/);
  const exe = parts.shift() || '';
  return { exe, args: parts, rawArgs: parts.join(' ') };
}

function parseArgs(str) {
  const out = [];
  const re = /"([^"]*)"|(\S+)/g;
  let m;
  while ((m = re.exec(str)) !== null) out.push(m[1] !== undefined ? m[1] : m[2]);
  return out;
}

// Launch vendor uninstaller through PowerShell Start-Process (ShellExecute) so
// GUI elevation (UAC) works reliably instead of throwing EACCES under spawn().
function runUninstall(pattern, opts = {}) {
  const matches = findPrograms(pattern);
  if (matches.length === 0) {
    record('uninstall', pattern, 'no match', 'not_found');
    return { status: 'not_found', pattern };
  }
  if (matches.length > 1 && !opts.all && !opts.exact) {
    const exactMatch = matches.find(p => p.DisplayName.toLowerCase() === String(pattern).trim().toLowerCase());
    if (!exactMatch) {
      record('uninstall', pattern, 'ambiguous: ' + matches.length, 'ambiguous');
      return { status: 'ambiguous', pattern, matches: matches.map(p => p.DisplayName) };
    }
    matches[0] = exactMatch;
  }

  const target = matches[0];
  let cmd = (opts.quiet && target.QuietUninstallString) ? target.QuietUninstallString : target.UninstallString;
  if (opts.forceMsi && /^\{[0-9A-Fa-f-]{36}\}$/.test(target.RegistryKey || '')) {
    cmd = `msiexec.exe /x ${target.RegistryKey}`;
  }
  if (!cmd) {
    record('uninstall', pattern, 'no uninstall string', 'no_command');
    return { status: 'no_command', pattern, target: target.DisplayName };
  }

  const { exe, rawArgs } = splitCommand(cmd);
  try {
    const res = runPSJson(`
      $exe = ${psLiteral(exe)}
      $arg = ${psLiteral(rawArgs)}
      if ([string]::IsNullOrWhiteSpace($arg)) {
        $p = Start-Process -FilePath $exe -PassThru
      } else {
        $p = Start-Process -FilePath $exe -ArgumentList $arg -PassThru
      }
      [pscustomobject]@{ pid = if($p){ $p.Id } else { 0 }; exe = $exe; args = $arg }
    `)[0] || {};
    record('uninstall', target.DisplayName, cmd, 'launched');
    return { status: 'launched', target: target.DisplayName, exe, args: rawArgs, pid: res.pid || 0, cmd };
  } catch (e) {
    record('uninstall', target.DisplayName, e.message, 'error');
    return { status: 'error', target: target.DisplayName, error: e.message, cmd };
  }
}

module.exports = { runUninstall, splitCommand, parseArgs };