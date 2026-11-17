'use strict';
const path = require('path');
const { runPSJson, runPS, psLiteral, resolveShortcutTarget } = require('./util');
const { getInstalledPrograms } = require('./inventory');
const { record } = require('./history');

// Fast enumeration of live processes using Get-Process (works reliably even without WMI/CIM permissions).
function processes() {
  return runPSJson(`
    Get-Process | Sort-Object @{Expression={ -not [string]::IsNullOrEmpty($_.MainWindowTitle) }; Descending=$true}, ProcessName | ForEach-Object {
      $p = ''
      try { $p = [string]$_.Path } catch {}
      $co = ''
      try { $co = [string]$_.Company } catch {}
      [pscustomobject]@{
        ProcessId      = [int]$_.Id
        Name           = [string]$_.ProcessName
        WindowTitle    = [string]$_.MainWindowTitle
        MemoryMB       = [math]::Round(($_.WorkingSet64 / 1MB), 1)
        Company        = $co
        ExecutablePath = $p
      }
    }
  `, { timeout: 60000 });
}

function hunt(pattern) {
  const info = resolveShortcutTarget(pattern);
  const terms = new Set();
  if (info.cleaned) terms.add(info.cleaned);
  if (info.stem) terms.add(info.stem);
  if (info.targetPath) {
    terms.add(info.targetPath);
    terms.add(path.basename(info.targetPath, path.extname(info.targetPath)));
  }

  const termList = Array.from(terms).filter(Boolean);
  const matchers = termList.map(t => new RegExp(t.replace(/[.+^${}()|[\]\\]/g, '\\$&'), 'i'));

  const allProcs = processes();
  const procs = termList.length === 0
    ? allProcs
    : allProcs.filter(p =>
        matchers.some(re =>
          re.test(p.Name || '') ||
          re.test(p.WindowTitle || '') ||
          re.test(p.ExecutablePath || '')
        )
      );

  const dirHints = new Set(
    [info.targetPath, ...procs.map(p => p.ExecutablePath)]
      .filter(Boolean)
      .map(exe => path.dirname(exe).toLowerCase())
  );

  const progs = termList.length === 0
    ? []
    : getInstalledPrograms().filter(p => {
        if (matchers.some(re => re.test(p.DisplayName || '') || re.test(p.Publisher || '') || re.test(p.UninstallString || ''))) {
          return true;
        }
        const loc = (p.InstallLocation || '').toLowerCase().replace(/"/g, '').replace(/[\\/]+$/, '');
        if (loc) {
          for (const d of dirHints) {
            if (d.startsWith(loc) || loc.startsWith(d)) return true;
          }
        }
        return false;
      });

  record('hunt', info.cleaned || pattern, `processes=${procs.length} programs=${progs.length}`);
  return {
    pattern: info.cleaned || pattern,
    resolvedTarget: info.targetPath || '',
    stem: info.stem || '',
    processes: procs,
    programs: progs,
  };
}

function findFromPath(exePath) {
  const info = resolveShortcutTarget(exePath);
  const resolved = info.targetPath || info.cleaned;
  const dir = resolved.includes('\\') ? path.dirname(resolved) : '';
  const progs = getInstalledPrograms().filter(p => {
    const loc = (p.InstallLocation || '').toLowerCase().replace(/"/g, '').replace(/[\\/]+$/, '');
    return (loc && dir.toLowerCase().startsWith(loc)) ||
      (info.stem && (p.DisplayName || '').toLowerCase().includes(info.stem.toLowerCase()));
  });
  return { executable: resolved, directory: dir, programs: progs };
}

function killProcess(pid) {
  const num = Number(pid);
  if (!Number.isInteger(num) || num <= 0) return { ok: false, error: 'Invalid PID' };
  try {
    runPS(`Stop-Process -Id ${num} -Force -ErrorAction Stop`);
    record('kill-process', String(num), 'terminated');
    return { ok: true, pid: num };
  } catch (e) {
    record('kill-process', String(num), e.message, 'error');
    return { ok: false, error: e.message };
  }
}

module.exports = { processes, hunt, findFromPath, killProcess };