'use strict';
const { execFileSync } = require('child_process');
const path = require('path');

// Run a PowerShell script block using -EncodedCommand (no shell quoting or $ hazards).
function runPS(script, opts = {}) {
  const full = `$ProgressPreference='SilentlyContinue'\n${script}`;
  const encoded = Buffer.from(full, 'utf16le').toString('base64');
  const args = ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', encoded];
  const out = execFileSync('powershell.exe', args, {
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
    windowsHide: true,
    timeout: opts.timeout || 120000,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  return out.replace(/^\uFEFF/, '').trim();
}

// Run a PowerShell script and parse its ConvertTo-Json output into an array.
function runPSJson(script, opts = {}) {
  const wrapped = `$ErrorActionPreference='SilentlyContinue'\n$__ouResult = @(& {\n${script}\n})\n$__ouResult | ConvertTo-Json -Depth 6 -Compress`;
  const raw = runPS(wrapped, opts);
  if (!raw || raw === 'null') return [];
  let parsed;
  try { parsed = JSON.parse(raw); } catch (e) {
    throw new Error('PowerShell JSON parse failed: ' + e.message + '\nRaw: ' + raw.slice(0, 500));
  }
  if (parsed === null || parsed === '') return [];
  return Array.isArray(parsed) ? parsed : [parsed];
}

function psLiteral(s) {
  return "'" + String(s ?? '').replace(/'/g, "''") + "'";
}

function pct(n, total) {
  return total ? ((n / total) * 100).toFixed(1) + '%' : '0.0%';
}

function resolveShortcutTarget(rawInput) {
  const cleaned = String(rawInput || '').trim().replace(/^"+|"+$/g, '');
  if (!cleaned) return { cleaned: '', stem: '', targetPath: '' };
  const ext = path.extname(cleaned).toLowerCase();
  const stem = path.basename(cleaned, ext || undefined);
  if (ext !== '.lnk') {
    return { cleaned, stem, targetPath: ext === '.exe' ? cleaned : '' };
  }
  try {
    const out = runPS(`
      $p = ${psLiteral(cleaned)}
      if (Test-Path -LiteralPath $p) {
        $sh = New-Object -ComObject WScript.Shell
        $sc = $sh.CreateShortcut($p)
        [pscustomobject]@{ targetPath = [string]$sc.TargetPath; args = [string]$sc.Arguments; workingDir = [string]$sc.WorkingDirectory } | ConvertTo-Json -Compress
      }
    `);
    if (out) {
      const info = JSON.parse(out);
      return { cleaned, stem, targetPath: info.targetPath || '', workingDir: info.workingDir || '' };
    }
  } catch (_) {}
  return { cleaned, stem, targetPath: '' };
}

function isProtectedPath(targetPath) {
  if (!targetPath || typeof targetPath !== 'string') return true;
  const norm = path.normalize(path.resolve(targetPath)).toLowerCase().replace(/[\\/]+$/, '');
  const roots = new Set([
    'c:', 'd:', 'e:', 'c:\\windows', 'c:\\windows\\system32', 'c:\\windows\\syswow64',
    'c:\\program files', 'c:\\program files (x86)', 'c:\\programdata', 'c:\\users',
  ]);
  if (roots.has(norm)) return true;
  for (const envVar of ['USERPROFILE', 'APPDATA', 'LOCALAPPDATA', 'TEMP', 'TMP']) {
    const v = (process.env[envVar] || '').toLowerCase().replace(/[\\/]+$/, '');
    if (v && norm === v) return true;
  }
  if (norm.startsWith('c:\\windows\\')) return true;
  return false;
}

function isProtectedRegKey(regPath) {
  if (!regPath || typeof regPath !== 'string') return true;
  const norm = regPath.trim().toLowerCase().replace(/[\\/]+$/, '');
  const parts = norm.split('\\').filter(Boolean);
  if (parts.length <= 3) return true;
  const protectedKeys = new Set([
    'hklm\\software\\microsoft\\windows',
    'hklm\\software\\wow6432node\\microsoft\\windows',
    'hklm\\software\\microsoft\\windows\\currentversion\\uninstall',
    'hklm\\software\\wow6432node\\microsoft\\windows\\currentversion\\uninstall',
    'hkcu\\software\\microsoft\\windows\\currentversion\\uninstall',
  ]);
  return protectedKeys.has(norm);
}

module.exports = {
  runPS,
  runPSJson,
  psLiteral,
  pct,
  resolveShortcutTarget,
  isProtectedPath,
  isProtectedRegKey,
};