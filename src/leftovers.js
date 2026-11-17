'use strict';
const fs = require('fs');
const path = require('path');
const { runPSJson, runPS, psLiteral, resolveShortcutTarget, isProtectedPath, isProtectedRegKey } = require('./util');
const { findPrograms } = require('./inventory');
const { BACKUP_DIR } = require('./backup');
const history = require('./history');
const tracer = require('./tracer');

const SCAN_ROOTS = [
  process.env.ProgramFiles || 'C:\\Program Files',
  process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)',
  process.env.ProgramData || 'C:\\ProgramData',
  process.env.LOCALAPPDATA || '',
  process.env.APPDATA || '',
  process.env.USERPROFILE ? path.join(process.env.USERPROFILE, 'AppData', 'LocalLow') : '',
].filter(Boolean);

const SHORTCUT_ROOTS = [
  process.env.USERPROFILE ? path.join(process.env.USERPROFILE, 'Desktop') : '',
  process.env.PUBLIC ? path.join(process.env.PUBLIC, 'Desktop') : '',
  process.env.APPDATA ? path.join(process.env.APPDATA, 'Microsoft', 'Windows', 'Start Menu', 'Programs') : '',
  process.env.ProgramData ? path.join(process.env.ProgramData, 'Microsoft', 'Windows', 'Start Menu', 'Programs') : '',
].filter(Boolean);

const DEEP_DATA_ROOTS = [
  process.env.LOCALAPPDATA || '',
  process.env.APPDATA || '',
  process.env.USERPROFILE ? path.join(process.env.USERPROFILE, 'AppData', 'LocalLow') : '',
  process.env.ProgramData || 'C:\\ProgramData',
].filter(Boolean);

const SCAN_REG = [
  'HKCU:\\SOFTWARE',
  'HKLM:\\SOFTWARE',
  'HKLM:\\SOFTWARE\\WOW6432Node',
];

const STOP_WORDS = new Set([
  'microsoft', 'windows', 'program', 'programs', 'software', 'corporation', 'corp', 'inc',
  'ltd', 'gmbh', 'technologies', 'solutions', 'media', 'player', 'desktop',
  'setup', 'installer', 'update', 'version', 'runtime', 'edition', 'client',
  'x64', 'x86', '64-bit', '32-bit', 'bit', 'free', 'pro', 'home', 'app', 'tool',
]);

const SHARED_VENDORS = new Set([
  'microsoft', 'classes', 'policies', 'clients', 'wow6432node', 'registeredapplications',
  'google', 'mozilla', 'adobe', 'intel', 'nvidia', 'amd', 'realtek', 'apple', 'oracle',
  'valve', 'steam', 'common files', 'windows nt', 'internet explorer', 'reference assemblies',
]);

const GENERIC_EXES = new Set([
  'uninstall', 'uninst', 'unins000', 'unins001', 'setup', 'install', 'msiexec',
  'helper', 'updater', 'launcher', 'crashreporter', 'cmd', 'powershell',
]);

function stripVersionAndArch(name) {
  return String(name || '')
    .replace(/\((64-bit|32-bit|x64|x86)\)/gi, '')
    .replace(/\bv?\d+(\.\d+){1,3}\b/g, '')
    .replace(/\s{2,}/g, ' ')
    .trim();
}

function cleanPublisher(pub) {
  return String(pub || '')
    .replace(/,?\s*(inc\.?|ltd\.?|corp\.?|corporation|llc|gmbh|co\.?|sarl|s\.r\.o\.?)$/gi, '')
    .trim();
}

function extractExeStemFromCmd(raw) {
  const s = String(raw || '').trim();
  if (!s) return '';
  let exe = '';
  const m = s.match(/^"([^"]+)"/);
  if (m) exe = m[1];
  else {
    const idx = s.toLowerCase().indexOf('.exe');
    if (idx > 0) exe = s.slice(0, idx + 4);
  }
  exe = exe.replace(/,\s*-?\d+\s*$/, '').trim();
  if (!exe || !/\.exe$/i.test(exe)) return '';
  return path.basename(exe, '.exe');
}

function extractDirFromCmd(raw) {
  const s = String(raw || '').trim();
  if (!s) return '';
  let exe = '';
  const m = s.match(/^"([^"]+)"/);
  if (m) exe = m[1];
  else {
    const idx = s.toLowerCase().indexOf('.exe');
    if (idx > 0) exe = s.slice(0, idx + 4);
  }
  if (!exe) return '';
  const dir = path.dirname(exe.replace(/,\s*-?\d+\s*$/, '').trim());
  if (!dir || dir === '.' || isProtectedPath(dir)) return '';
  if (/unins|setup|install/i.test(path.basename(exe)) || !/system32|syswow64|windows/i.test(dir)) {
    return dir;
  }
  return '';
}

function buildTokens(pattern, extraNames = [], exeStems = []) {
  const info = resolveShortcutTarget(pattern);
  const rawBase = info.stem || info.cleaned || String(pattern || '').trim();
  const candidates = [rawBase, stripVersionAndArch(rawBase), ...extraNames.map(x => stripVersionAndArch(x))];
  const tokens = new Set();

  for (const cand of candidates) {
    const base = String(cand || '').trim();
    if (!base) continue;
    if (base.length >= 3 && !STOP_WORDS.has(base.toLowerCase())) tokens.add(base.toLowerCase());
    const words = base.split(/[\s\-_.()]+/).map(w => w.trim()).filter(Boolean);
    if (words.length >= 2) {
      const two = (words[0] + ' ' + words[1]).toLowerCase();
      if (two.length >= 5 && !STOP_WORDS.has(two)) tokens.add(two);
    }
    const first = words.find(w => w.length >= 3 && !STOP_WORDS.has(w.toLowerCase()) && !/^\d+$/.test(w));
    if (first) tokens.add(first.toLowerCase());
    for (const w of words) {
      const lw = w.toLowerCase();
      if (lw.length >= 4 && !STOP_WORDS.has(lw) && !/^\d+$/.test(lw)) tokens.add(lw);
    }
  }

  for (const es of exeStems) {
    const lw = String(es || '').trim().toLowerCase();
    if (lw.length >= 3 && !GENERIC_EXES.has(lw) && !STOP_WORDS.has(lw)) tokens.add(lw);
  }
  if (info.targetPath) {
    const lw = path.basename(info.targetPath, path.extname(info.targetPath)).toLowerCase();
    if (lw.length >= 3 && !GENERIC_EXES.has(lw)) tokens.add(lw);
  }

  return Array.from(tokens).slice(0, 8);
}

function buildScanContext(pattern, hints = {}) {
  const cleanPattern = String(pattern || '').trim();
  const info = resolveShortcutTarget(cleanPattern);
  const liveProgs = cleanPattern ? findPrograms(cleanPattern) : [];

  let refItems = [];
  try {
    if (cleanPattern) refItems = (history.queryReferenceLogs(cleanPattern, 8).items || []);
  } catch (_) {}

  const explicitDirs = new Set();
  const explicitRegKeys = new Set();
  const publishers = new Set();
  const productNames = new Set();
  const exeStems = new Set();
  const traceFiles = [];
  const traceRegKeys = [];

  const addProduct = (v) => {
    const s = stripVersionAndArch(v);
    if (s && s.length >= 2) productNames.add(s);
    const raw = String(v || '').trim();
    if (raw && raw.length >= 2) productNames.add(raw);
  };
  const addPub = (v) => {
    const p1 = String(v || '').trim();
    const p2 = cleanPublisher(p1);
    if (p1 && p1.length >= 2) publishers.add(p1);
    if (p2 && p2.length >= 2) publishers.add(p2);
  };
  const addExe = (stem) => {
    const s = String(stem || '').trim().replace(/\.exe$/i, '');
    if (s && s.length >= 2 && !GENERIC_EXES.has(s.toLowerCase())) exeStems.add(s);
  };

  if (cleanPattern) addProduct(info.stem || cleanPattern);
  if (info.targetPath) {
    const dir = path.dirname(info.targetPath);
    if (!isProtectedPath(dir)) explicitDirs.add(path.resolve(dir));
    addExe(path.basename(info.targetPath, path.extname(info.targetPath)));
  }

  // Apply pre-captured hints (critical after built-in uninstaller deletes Uninstall registry key)
  if (hints && typeof hints === 'object') {
    if (hints.displayName) addProduct(hints.displayName);
    if (hints.publisher) addPub(hints.publisher);
    if (hints.installLocation && !isProtectedPath(hints.installLocation)) {
      explicitDirs.add(path.resolve(hints.installLocation));
    }
    if (hints.uninstallString) {
      const ud = extractDirFromCmd(hints.uninstallString);
      if (ud) explicitDirs.add(path.resolve(ud));
      addExe(extractExeStemFromCmd(hints.uninstallString));
    }
    if (hints.displayIcon) {
      addExe(extractExeStemFromCmd(hints.displayIcon));
    }
    if (hints.hivePath && hints.registryKey) {
      explicitRegKeys.add(`${hints.hivePath}\\${hints.registryKey}`);
    }
  }

  // Merge live Uninstall registry matches
  for (const p of liveProgs) {
    addProduct(p.DisplayName);
    addPub(p.Publisher);
    if (p.InstallLocation && !isProtectedPath(p.InstallLocation)) explicitDirs.add(path.resolve(p.InstallLocation));
    if (p.UninstallString) {
      const ud = extractDirFromCmd(p.UninstallString);
      if (ud) explicitDirs.add(path.resolve(ud));
      addExe(extractExeStemFromCmd(p.UninstallString));
    }
    if (p.DisplayIcon) addExe(extractExeStemFromCmd(p.DisplayIcon));
    if (p.HivePath && p.RegistryKey) explicitRegKeys.add(`${p.HivePath}\\${p.RegistryKey}`);
  }

  // Merge optional local SQLite reference logs when explicitly configured
  for (const r of refItems) {
    if (r.Publisher) addPub(r.Publisher);
    if (r.Name) addProduct(r.Name);
    if (r.RKey) {
      explicitRegKeys.add(`HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\${r.RKey}`);
      explicitRegKeys.add(`HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\${r.RKey}`);
      explicitRegKeys.add(`HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\${r.RKey}`);
    }
  }

  // Merge Traced Programs logs matching pattern or explicitDir
  try {
    const patLow = (stripVersionAndArch(cleanPattern) || cleanPattern).toLowerCase();
    const dirList = Array.from(explicitDirs).map(d => d.toLowerCase());
    const matchedTraces = tracer.listTraces().filter(t =>
      (patLow && String(t.name || '').toLowerCase().includes(patLow)) ||
      (t.InstallLocation && dirList.some(d => String(t.InstallLocation).toLowerCase().includes(d)))
    ).slice(0, 3);
    for (const tr of matchedTraces) {
      const det = tracer.getTraceItems(tr.id);
      for (const it of (det.items || [])) {
        if ((it.Kind === 'Dir' || it.Kind === 'File') && it.Path) traceFiles.push(it.Path);
        else if (it.Kind === 'RegKey' && it.Path) traceRegKeys.push(it.Path);
      }
    }
  } catch (_) {}

  // Pick up any remaining .exe inside explicitDirs
  for (const d of explicitDirs) {
    if (!fs.existsSync(d)) continue;
    try {
      for (const ent of fs.readdirSync(d, { withFileTypes: true })) {
        if (ent.isFile() && /\.exe$/i.test(ent.name)) addExe(ent.name);
      }
    } catch (_) {}
  }

  const tokens = buildTokens(cleanPattern, Array.from(productNames), Array.from(exeStems));
  return {
    pattern: cleanPattern,
    tokens,
    productNames: Array.from(productNames),
    publishers: Array.from(publishers),
    explicitDirs: Array.from(explicitDirs),
    explicitRegKeys: Array.from(explicitRegKeys),
    exeStems: Array.from(exeStems),
    traceFiles,
    traceRegKeys,
  };
}

function scanFilesystem(pattern, modeOrDepth = 'moderate', hintsOrMode = {}) {
  let mode = 'moderate';
  let hints = {};
  if (typeof modeOrDepth === 'number') {
    mode = typeof hintsOrMode === 'string' ? hintsOrMode : 'moderate';
  } else {
    mode = String(modeOrDepth || 'moderate').toLowerCase();
    hints = (hintsOrMode && typeof hintsOrMode === 'object') ? hintsOrMode : {};
  }
  if (mode === 'built-in') return [];

  const ctx = buildScanContext(pattern, hints);
  if (!ctx.tokens.length && !ctx.explicitDirs.length) return [];

  const out = [];
  const seen = new Set();

  function addEntry(fullPath, kind, source) {
    if (!fullPath || isProtectedPath(fullPath) || !fs.existsSync(fullPath)) return;
    const lower = path.resolve(fullPath).toLowerCase();
    if (seen.has(lower)) return;
    seen.add(lower);
    out.push({ Path: path.resolve(fullPath), Name: path.basename(fullPath), Kind: kind, Source: source });
  }

  // SAFE LAYER (always runs in safe, moderate, advanced):
  // 1) Explicit InstallLocation / uninstall dirs & Traced Programs paths
  for (const ep of ctx.explicitDirs) addEntry(ep, 'directory', 'InstallLocation');
  for (const tf of ctx.traceFiles) {
    if (fs.existsSync(tf)) {
      try {
        addEntry(tf, fs.statSync(tf).isDirectory() ? 'directory' : 'file', 'TracedProgram');
      } catch (_) {}
    }
  }

  // 2) Exact direct <Root>\<Product> and <Root>\<Publisher>\<Product> across user & program roots
  for (const root of SCAN_ROOTS) {
    if (!fs.existsSync(root)) continue;
    for (const prod of ctx.productNames) {
      addEntry(path.join(root, prod), 'directory', root);
      for (const pub of ctx.publishers) {
        addEntry(path.join(root, pub, prod), 'directory', path.join(root, pub));
      }
    }
    for (const ex of ctx.exeStems) {
      addEntry(path.join(root, ex), 'directory', root);
    }
  }

  // 3) Matching Start Menu & Desktop shortcuts (.lnk / .url) and app Start Menu folder
  const scanShortcuts = (dir, depth) => {
    if (!dir || depth < 0 || !fs.existsSync(dir)) return;
    let ents = [];
    try { ents = fs.readdirSync(dir, { withFileTypes: true }); } catch (_) { return; }
    for (const e of ents) {
      const full = path.join(dir, e.name);
      const ln = e.name.toLowerCase();
      if (e.isDirectory()) {
        if (ctx.tokens.some(t => ln.includes(t))) addEntry(full, 'directory', 'StartMenu');
        if (depth > 0) scanShortcuts(full, depth - 1);
      } else if (e.isFile() && /\.(lnk|url)$/i.test(ln)) {
        const stem = path.basename(ln, path.extname(ln));
        if (ctx.tokens.some(t => stem.includes(t))) {
          addEntry(full, 'file', 'Shortcut');
        }
      }
    }
  };
  for (const sr of SHORTCUT_ROOTS) scanShortcuts(sr, 2);

  // MODERATE & ADVANCED LAYERS: recursive token search
  if (mode === 'moderate' || mode === 'advanced') {
    const scanTree = (dir, maxD, curD) => {
      if (!dir || curD > maxD || !fs.existsSync(dir)) return;
      let entries = [];
      try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch (_) { return; }
      for (const e of entries) {
        const ln = e.name.toLowerCase();
        if (ln === 'temp' || ln === 'tmp' || ln === 'cache' || ln === 'windows') continue;
        const full = path.join(dir, e.name);
        if (ctx.tokens.some(t => ln.includes(t))) {
          addEntry(full, e.isDirectory() ? 'directory' : 'file', dir);
        } else if (e.isDirectory() && curD < maxD) {
          scanTree(full, maxD, curD + 1);
        }
      }
    };

    for (const root of SCAN_ROOTS) scanTree(root, 2, 1);

    if (mode === 'advanced') {
      for (const root of DEEP_DATA_ROOTS) scanTree(root, 3, 1);

      // Check $env:TEMP direct folders/files and Windows\Prefetch .pf entries
      const tempDir = process.env.TEMP || '';
      if (tempDir && fs.existsSync(tempDir)) {
        try {
          for (const e of fs.readdirSync(tempDir, { withFileTypes: true })) {
            const ln = e.name.toLowerCase();
            if (ctx.tokens.some(t => t.length >= 4 && ln.includes(t))) {
              addEntry(path.join(tempDir, e.name), e.isDirectory() ? 'directory' : 'file', 'TEMP');
            }
          }
        } catch (_) {}
      }

      const prefetchDir = 'C:\\Windows\\Prefetch';
      if (ctx.exeStems.length && fs.existsSync(prefetchDir)) {
        try {
          for (const e of fs.readdirSync(prefetchDir, { withFileTypes: true })) {
            if (!e.isFile() || !/\.pf$/i.test(e.name)) continue;
            const ln = e.name.toLowerCase();
            if (ctx.exeStems.some(ex => ln.startsWith(ex.toLowerCase() + '-'))) {
              addEntry(path.join(prefetchDir, e.name), 'file', 'Prefetch');
            }
          }
        } catch (_) {}
      }
    }
  }

  return out.slice(0, 250);
}

function scanRegistry(pattern, mode = 'moderate', hints = {}) {
  const m = String(mode || 'moderate').toLowerCase();
  if (m === 'built-in') return [];

  const ctx = buildScanContext(pattern, hints);
  if (!ctx.tokens.length && !ctx.explicitRegKeys.length && !ctx.traceRegKeys.length) return [];

  const tokList = ctx.tokens.map(psLiteral).join(',');
  const roots = SCAN_REG.map(psLiteral).join(',');
  const explicitList = [...ctx.explicitRegKeys, ...ctx.traceRegKeys].map(k =>
    String(k || '').replace(/^HKLM\\/i, 'HKLM:\\').replace(/^HKCU\\/i, 'HKCU:\\')
  ).map(psLiteral).join(',');
  const prodList = ctx.productNames.map(psLiteral).join(',');
  const pubList = ctx.publishers.map(psLiteral).join(',');
  const exeList = ctx.exeStems.map(psLiteral).join(',');
  const dirList = ctx.explicitDirs.map(psLiteral).join(',');

  const script = `
    $roots = @(${roots})
    $tokens = @(${tokList})
    $explicit = @(${explicitList})
    $prods = @(${prodList})
    $pubs = @(${pubList})
    $exes = @(${exeList})
    $dirs = @(${dirList})
    $mode = ${psLiteral(m)}
    $shared = @('microsoft','classes','policies','clients','wow6432node','registeredapplications','google','mozilla','adobe','intel','nvidia','amd','realtek','apple','oracle','valve','common files','windows nt','internet explorer')

    # SAFE: Explicit Uninstall / Reference / Traced keys
    foreach ($ep in $explicit) {
      if ($ep -and (Test-Path -LiteralPath $ep)) {
        $it = Get-Item -LiteralPath $ep -ErrorAction SilentlyContinue
        if ($it) {
          [pscustomobject]@{ Key = $it.Name; Name = $it.PSChildName; Kind = 'Key'; ValueName = ''; Data = ''; Source = 'UninstallOrTraced' }
        }
      }
    }

    # SAFE: Exact <Product> and <Publisher>\\<Product> keys under Software hives
    foreach ($root in $roots) {
      if (-not (Test-Path -LiteralPath $root)) { continue }
      foreach ($p in $prods) {
        if (-not $p) { continue }
        $cand1 = Join-Path $root $p
        if (Test-Path -LiteralPath $cand1) {
          $it = Get-Item -LiteralPath $cand1 -ErrorAction SilentlyContinue
          if ($it) { [pscustomobject]@{ Key = $it.Name; Name = $it.PSChildName; Kind = 'Key'; ValueName = ''; Data = ''; Source = $root } }
        }
        foreach ($pub in $pubs) {
          if (-not $pub) { continue }
          $cand2 = Join-Path (Join-Path $root $pub) $p
          if (Test-Path -LiteralPath $cand2) {
            $it = Get-Item -LiteralPath $cand2 -ErrorAction SilentlyContinue
            if ($it) { [pscustomobject]@{ Key = $it.Name; Name = $it.PSChildName; Kind = 'Key'; ValueName = ''; Data = ''; Source = "$root\\$pub" } }
          }
        }
      }
      foreach ($ex in $exes) {
        if (-not $ex) { continue }
        $cand3 = Join-Path $root $ex
        if (Test-Path -LiteralPath $cand3) {
          $it = Get-Item -LiteralPath $cand3 -ErrorAction SilentlyContinue
          if ($it) { [pscustomobject]@{ Key = $it.Name; Name = $it.PSChildName; Kind = 'Key'; ValueName = ''; Data = ''; Source = $root } }
        }
      }
    }

    # MODERATE & ADVANCED: Depth 1 + Depth 2 Software hive scan, App Paths, Run values
    if ($mode -eq 'moderate' -or $mode -eq 'advanced') {
      foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($k in Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue) {
          $n = $k.PSChildName.ToLowerInvariant()
          $isShared = $shared -contains $n
          $matchedRoot = $false
          if (-not $isShared) {
            foreach ($t in $tokens) {
              if ($n.Contains($t)) {
                [pscustomobject]@{ Key = $k.Name; Name = $k.PSChildName; Kind = 'Key'; ValueName = ''; Data = ''; Source = $root }
                $matchedRoot = $true
                break
              }
            }
          }
          if (-not $matchedRoot) {
            foreach ($sub in Get-ChildItem -LiteralPath $k.PSPath -ErrorAction SilentlyContinue) {
              $sn = $sub.PSChildName.ToLowerInvariant()
              foreach ($t in $tokens) {
                if ($sn.Contains($t)) {
                  [pscustomobject]@{ Key = $sub.Name; Name = ($k.PSChildName + '\\' + $sub.PSChildName); Kind = 'Key'; ValueName = ''; Data = ''; Source = ($root + '\\' + $k.PSChildName) }
                  break
                }
              }
            }
          }
        }
      }

      # App Paths keys matching exe name or InstallLocation
      foreach ($apRoot in @(
        'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths',
        'HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths'
      )) {
        if (-not (Test-Path -LiteralPath $apRoot)) { continue }
        foreach ($ak in Get-ChildItem -LiteralPath $apRoot -ErrorAction SilentlyContinue) {
          $an = $ak.PSChildName.ToLowerInvariant()
          $hit = $false
          foreach ($ex in $exes) {
            if ($an -eq ($ex.ToLowerInvariant() + '.exe')) { $hit = $true; break }
          }
          if (-not $hit -and $dirs.Count -gt 0) {
            $defVal = [string](Get-ItemProperty -LiteralPath $ak.PSPath -ErrorAction SilentlyContinue).'(default)'
            foreach ($d in $dirs) {
              if ($defVal -and $defVal.ToLowerInvariant().Contains($d.ToLowerInvariant())) { $hit = $true; break }
            }
          }
          if ($hit) {
            [pscustomobject]@{ Key = $ak.Name; Name = $ak.PSChildName; Kind = 'Key'; ValueName = ''; Data = ''; Source = 'AppPaths' }
          }
        }
      }

      # Run / RunOnce values matching tokens or InstallLocation
      foreach ($rk in @(
        'HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run',
        'HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\RunOnce',
        'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run',
        'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Run'
      )) {
        if (-not (Test-Path -LiteralPath $rk)) { continue }
        $pr = Get-ItemProperty -LiteralPath $rk -ErrorAction SilentlyContinue
        if (-not $pr) { continue }
        foreach ($prop in $pr.PSObject.Properties) {
          if ($prop.Name -like 'PS*') { continue }
          $vn = $prop.Name.ToLowerInvariant()
          $vd = [string]$prop.Value
          $vdl = $vd.ToLowerInvariant()
          $hit = $false
          foreach ($t in $tokens) { if ($t.Length -ge 4 -and $vn.Contains($t)) { $hit = $true; break } }
          if (-not $hit) { foreach ($d in $dirs) { if ($vdl.Contains($d.ToLowerInvariant())) { $hit = $true; break } } }
          if (-not $hit) { foreach ($ex in $exes) { if ($ex.Length -ge 4 -and $vdl.Contains($ex.ToLowerInvariant() + '.exe')) { $hit = $true; break } } }
          if ($hit) {
            [pscustomobject]@{ Key = $rk; Name = $prop.Name; Kind = 'Value'; ValueName = $prop.Name; Data = $vd; Source = 'StartupRun' }
          }
        }
      }
    }

    # ADVANCED LAYER: MuiCache, AppCompatFlags, Classes\\Applications, Services referencing InstallLocation
    if ($mode -eq 'advanced') {
      foreach ($ex in $exes) {
        if (-not $ex) { continue }
        $appKey = 'HKCU:\\Software\\Classes\\Applications\\' + $ex + '.exe'
        if (Test-Path -LiteralPath $appKey) {
          $it = Get-Item -LiteralPath $appKey -ErrorAction SilentlyContinue
          if ($it) { [pscustomobject]@{ Key = $it.Name; Name = $it.PSChildName; Kind = 'Key'; ValueName = ''; Data = ''; Source = 'ClassesApplications' } }
        }
      }

      foreach ($vk in @(
        @{ p = 'HKCU:\\Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\Shell\\MuiCache'; s = 'MuiCache' },
        @{ p = 'HKCU:\\Software\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Compatibility Assistant\\Store'; s = 'AppCompatFlags' }
      )) {
        if (-not (Test-Path -LiteralPath $vk.p)) { continue }
        $pr = Get-ItemProperty -LiteralPath $vk.p -ErrorAction SilentlyContinue
        if (-not $pr) { continue }
        foreach ($prop in $pr.PSObject.Properties) {
          if ($prop.Name -like 'PS*') { continue }
          $vnl = $prop.Name.ToLowerInvariant()
          $hit = $false
          foreach ($d in $dirs) { if ($vnl.Contains($d.ToLowerInvariant())) { $hit = $true; break } }
          if (-not $hit) {
            foreach ($ex in $exes) { if ($ex.Length -ge 4 -and $vnl.Contains('\\' + $ex.ToLowerInvariant() + '.exe')) { $hit = $true; break } }
          }
          if ($hit) {
            [pscustomobject]@{ Key = $vk.p; Name = (Split-Path -Leaf ($prop.Name -replace '\\.FriendlyAppName$','')); Kind = 'Value'; ValueName = $prop.Name; Data = [string]$prop.Value; Source = $vk.s }
          }
        }
      }

      if ($dirs.Count -gt 0) {
        $svcRoot = 'HKLM:\\SYSTEM\\CurrentControlSet\\Services'
        if (Test-Path -LiteralPath $svcRoot) {
          foreach ($sk in Get-ChildItem -LiteralPath $svcRoot -ErrorAction SilentlyContinue) {
            $img = [string](Get-ItemProperty -LiteralPath $sk.PSPath -ErrorAction SilentlyContinue).ImagePath
            if (-not $img) { continue }
            $imgL = $img.ToLowerInvariant()
            if ($imgL -like '*\\windows\\*' -or $imgL -like '*system32*') { continue }
            foreach ($d in $dirs) {
              if ($imgL.Contains($d.ToLowerInvariant())) {
                [pscustomobject]@{ Key = $sk.Name; Name = $sk.PSChildName; Kind = 'Key'; ValueName = ''; Data = $img; Source = 'Service' }
                break
              }
            }
          }
        }
      }
    }
  `;

  const rows = runPSJson(script, { timeout: 120000 });
  const seen = new Set();
  return rows.filter(r => {
    const shortKey = String(r.Key || '')
      .replace(/^HKEY_LOCAL_MACHINE/i, 'HKLM')
      .replace(/^HKEY_CURRENT_USER/i, 'HKCU')
      .replace(/^HKLM:\\/i, 'HKLM\\')
      .replace(/^HKCU:\\/i, 'HKCU\\');
    const kind = r.Kind === 'Value' ? 'Value' : 'Key';
    if (kind === 'Key' && isProtectedRegKey(shortKey)) return false;
    const dedup = kind + '|' + shortKey.toLowerCase() + '|' + String(r.ValueName || '').toLowerCase();
    if (seen.has(dedup)) return false;
    seen.add(dedup);
    r.Key = shortKey;
    r.Kind = kind;
    return true;
  }).slice(0, 250);
}

function scan(pattern, mode = 'moderate', hints = {}) {
  const normalizedMode = String(mode || 'moderate').toLowerCase();
  const result = {
    pattern,
    mode: normalizedMode,
    filesystem: scanFilesystem(pattern, normalizedMode, hints),
    registry: scanRegistry(pattern, normalizedMode, hints),
  };
  history.record('leftovers-scan', pattern, `mode=${normalizedMode} fs=${result.filesystem.length} reg=${result.registry.length}`);
  return result;
}

function cleanLeftovers({ files = [], registry = [], regValues = [], backupFirst = true } = {}) {
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const bkDir = path.join(BACKUP_DIR, `leftovers-${stamp}`);
  if (backupFirst && (files.length || registry.length || regValues.length)) {
    fs.mkdirSync(bkDir, { recursive: true });
  }

  let removedFiles = 0;
  let removedKeys = 0;
  let removedValues = 0;
  const errors = [];

  for (const rawPath of files) {
    if (!rawPath || isProtectedPath(rawPath) || !fs.existsSync(rawPath)) continue;
    try {
      if (backupFirst) {
        const dest = path.join(bkDir, path.basename(rawPath));
        fs.cpSync(rawPath, dest, { recursive: true, force: true });
      }
      fs.rmSync(rawPath, { recursive: true, force: true });
      removedFiles++;
    } catch (e) {
      errors.push({ target: rawPath, error: e.message });
    }
  }

  const protectedContainers = new Set([
    'hkcu\\software\\microsoft\\windows\\currentversion\\run',
    'hkcu\\software\\microsoft\\windows\\currentversion\\runonce',
    'hklm\\software\\microsoft\\windows\\currentversion\\run',
    'hklm\\software\\microsoft\\windows\\currentversion\\runonce',
    'hklm\\software\\wow6432node\\microsoft\\windows\\currentversion\\run',
    'hklm\\software\\microsoft\\windows\\currentversion\\app paths',
    'hkcu\\software\\microsoft\\windows\\currentversion\\app paths',
    'hkcu\\software\\classes\\local settings\\software\\microsoft\\windows\\shell\\muicache',
    'hkcu\\software\\microsoft\\windows nt\\currentversion\\appcompatflags\\compatibility assistant\\store',
    'hkcu\\software\\classes\\applications',
    'hklm\\system\\currentcontrolset\\services',
  ]);

  for (let i = 0; i < registry.length; i++) {
    const shortKey = String(registry[i] || '')
      .replace(/^HKEY_LOCAL_MACHINE/i, 'HKLM')
      .replace(/^HKEY_CURRENT_USER/i, 'HKCU')
      .replace(/^HKLM:\\/i, 'HKLM\\')
      .replace(/^HKCU:\\/i, 'HKCU\\');
    const normKey = shortKey.trim().toLowerCase().replace(/[\\/]+$/, '');
    if (!shortKey || isProtectedRegKey(shortKey) || protectedContainers.has(normKey)) continue;
    try {
      if (backupFirst) {
        const regFile = path.join(bkDir, `key_${i}.reg`);
        runPS(`& reg.exe export ${psLiteral(shortKey)} ${psLiteral(regFile)} /y`);
      }
      runPS(`& reg.exe delete ${psLiteral(shortKey)} /f`);
      removedKeys++;
    } catch (e) {
      errors.push({ target: shortKey, error: e.message });
    }
  }

  const exportedValueKeys = new Set();
  for (let i = 0; i < regValues.length; i++) {
    const rv = regValues[i];
    if (!rv || !rv.Key || !rv.ValueName) continue;
    const shortKey = String(rv.Key)
      .replace(/^HKEY_LOCAL_MACHINE/i, 'HKLM')
      .replace(/^HKEY_CURRENT_USER/i, 'HKCU')
      .replace(/^HKLM:\\/i, 'HKLM\\')
      .replace(/^HKCU:\\/i, 'HKCU\\');
    const psKey = shortKey.replace(/^HKLM\\/i, 'HKLM:\\').replace(/^HKCU\\/i, 'HKCU:\\');
    if (!shortKey.startsWith('HKCU\\') && !shortKey.startsWith('HKLM\\')) continue;
    try {
      if (backupFirst && !exportedValueKeys.has(shortKey.toLowerCase())) {
        exportedValueKeys.add(shortKey.toLowerCase());
        const regFile = path.join(bkDir, `valkey_${i}.reg`);
        runPS(`& reg.exe export ${psLiteral(shortKey)} ${psLiteral(regFile)} /y`);
      }
      runPS(`Remove-ItemProperty -LiteralPath ${psLiteral(psKey)} -Name ${psLiteral(rv.ValueName)} -Force -ErrorAction Stop`);
      removedValues++;
    } catch (e) {
      errors.push({ target: `${shortKey}\\${rv.ValueName}`, error: e.message });
    }
  }

  history.record(
    'leftovers-clean',
    `${removedFiles} paths, ${removedKeys} keys, ${removedValues} values`,
    backupFirst ? `backup=${bkDir}` : 'no-backup',
    errors.length ? 'partial' : 'ok'
  );
  return { ok: errors.length === 0, removedFiles, removedKeys, removedValues, backupDir: backupFirst ? bkDir : null, errors };
}

function findUninstallerExes(dir, depth = 0, out = []) {
  if (!dir || depth > 2 || !fs.existsSync(dir)) return out;
  let entries = [];
  try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch (_) { return out; }
  for (const e of entries) {
    const full = path.join(dir, e.name);
    if (e.isFile() && /^(unins|uninstall|setup|remove).*\.(exe|msi)$/i.test(e.name)) {
      out.push({ name: e.name, path: full });
    } else if (e.isDirectory() && depth < 2) {
      findUninstallerExes(full, depth + 1, out);
    }
  }
  return out;
}

function forcedUninstallProbe({ targetPath = '', programName = '', mode = 'moderate' } = {}) {
  const raw = String(targetPath || '').trim();
  const info = resolveShortcutTarget(raw);
  const resolved = info.targetPath || info.cleaned || '';

  let targetDir = '';
  if (resolved && fs.existsSync(resolved)) {
    try {
      const st = fs.statSync(resolved);
      targetDir = st.isDirectory() ? path.resolve(resolved) : path.dirname(path.resolve(resolved));
    } catch (_) {}
  }

  const searchName = String(programName || '').trim()
    || (targetDir && !isProtectedPath(targetDir) ? path.basename(targetDir) : '')
    || info.stem
    || '';

  const uninstallers = targetDir ? findUninstallerExes(targetDir) : [];
  const nameLower = searchName.toLowerCase();
  const dirLower = targetDir.toLowerCase();

  const tracedMatches = tracer.listTraces().filter(t =>
    (nameLower && String(t.name || '').toLowerCase().includes(nameLower)) ||
    (dirLower && String(t.InstallLocation || '').toLowerCase().includes(dirLower))
  );

  const refLogs = searchName ? (history.queryReferenceLogs(searchName, 10).items || []) : [];
  const scanRes = searchName
    ? scan(searchName, mode, { installLocation: targetDir, displayIcon: resolved })
    : { pattern: '', mode, filesystem: [], registry: [] };

  if (targetDir && fs.existsSync(targetDir) && !isProtectedPath(targetDir)) {
    const exists = scanRes.filesystem.some(f => String(f.Path || '').toLowerCase() === dirLower);
    if (!exists) {
      scanRes.filesystem.unshift({ Path: targetDir, Name: path.basename(targetDir), Kind: 'directory', Source: 'ForcedTarget' });
    }
  }

  history.record('forced-probe', searchName || targetDir || '(empty)', `mode=${mode} fs=${scanRes.filesystem.length} reg=${scanRes.registry.length} setup=${uninstallers.length}`);
  return {
    targetDir,
    searchName,
    mode,
    uninstallers,
    tracedMatches,
    refLogs,
    filesystem: scanRes.filesystem,
    registry: scanRes.registry,
  };
}

module.exports = { scan, scanFilesystem, scanRegistry, cleanLeftovers, forcedUninstallProbe, SCAN_ROOTS, SCAN_REG };
