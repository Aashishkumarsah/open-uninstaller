'use strict';
const { getInstalledPrograms, findPrograms } = require('./inventory');
const { runUninstall, splitCommand } = require('./uninstall');
const { scan } = require('./leftovers');
const { listAutoruns } = require('./autorun');
const { scanJunk } = require('./junk');
const { listWinApps } = require('./winapps');
const { backupRegistry } = require('./backup');
const history = require('./history');

const HELP = `
Open Uninstaller (ou) — free, original Windows cleanup CLI
Usage: ou <command> [options]

Commands:
  list                          List installed programs
  search <pattern>              Search installed programs (regex or * ? wildcards)
  details <pattern>             Show full metadata for a matching program
  uninstall <pattern> [--all]   Launch a program's uninstaller (use --all for multiple matches)
  leftovers <pattern>           Read-only scan for leftover folders/registry keys
  autoruns                      List startup entries
  winapps                       List Store/UWP packages (AppModel repository)
  junk                          Scan common junk locations and report sizes
  backup                        Export registry keys to .reg files (data/backups)
  history [limit]               Show operation history (default 25)
  ui [--port N]                 Launch the graphical web UI (opens your browser)
  help                          Show this help

Safety: this tool never deletes files or registry keys. Cleanup commands only
report candidates; uninstall launches the vendor's own uninstaller.
`;

function fmtSize(bytes) {
  bytes = Number(bytes) || 0;
  if (bytes < 1024) return bytes + ' B';
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
  if (bytes < 1024 * 1024 * 1024) return (bytes / 1024 / 1024).toFixed(1) + ' MB';
  return (bytes / 1024 / 1024 / 1024).toFixed(2) + ' GB';
}

function cmdList(args) {
  const progs = getInstalledPrograms();
  history.record('list', 'installed', `count=${progs.length}`);
  console.log(`Installed programs: ${progs.length}\n`);
  for (const p of progs) {
    const size = p.EstimatedSize ? ` | ${fmtSize(Number(p.EstimatedSize) * 1024)}` : '';
    console.log(`  ${p.DisplayName}${p.DisplayVersion ? ' ' + p.DisplayVersion : ''}${size}`);
    console.log(`      publisher: ${p.Publisher || '(unknown)'}`);
    console.log(`      key: ${p.RegistryKey}  hive: ${p.HivePath.split('\\').slice(0, 2).join('\\')}`);
  }
}

function cmdSearch(args) {
  const pattern = args[0];
  if (!pattern) return console.error('search requires a pattern');
  const found = findPrograms(pattern);
  history.record('search', pattern, `count=${found.length}`);
  console.log(`Matches for /${pattern}/i: ${found.length}\n`);
  for (const p of found) {
    console.log(`  ${p.DisplayName} ${p.DisplayVersion || ''} — ${p.Publisher || '?'}`);
  }
}

function cmdDetails(args) {
  const pattern = args[0];
  if (!pattern) return console.error('details requires a pattern');
  const found = findPrograms(pattern);
  if (!found.length) return console.log('No match.');
  const p = found[0];
  history.record('details', p.DisplayName);
  for (const [k, v] of Object.entries(p)) {
    if (v !== undefined && v !== null && v !== '') console.log(`${k}: ${v}`);
  }
  if (p.UninstallString) {
    console.log('\nParsed uninstall command:');
    console.log('  ' + JSON.stringify(splitCommand(p.UninstallString)));
  }
}

function cmdUninstall(args) {
  const pattern = args[0];
  if (!pattern) return console.error('uninstall requires a pattern');
  const all = args.includes('--all');
  const r = runUninstall(pattern, { all });
  console.log(JSON.stringify(r, null, 2));
}

function cmdLeftovers(args) {
  const pattern = args[0];
  if (!pattern) return console.error('leftovers requires a pattern');
  history.record('leftovers', pattern);
  console.log(`Scanning for leftovers matching /${pattern}/i (read-only)...`);
  const result = scan(pattern);
  console.log(`\nFilesystem folders (${result.filesystem.length}):`);
  for (const f of result.filesystem) console.log(`  ${f.Path}`);
  console.log(`\nRegistry keys (${result.registry.length}):`);
  for (const k of result.registry) console.log(`  ${k.Key}`);
}

function cmdAutoruns() {
  const rows = listAutoruns();
  history.record('autoruns', 'all', `count=${rows.length}`);
  console.log(`Startup entries: ${rows.length}\n`);
  for (const r of rows) {
    console.log(`  [${r.Status}] ${r.Name}`);
    console.log(`      ${r.Command}`);
  }
}

function cmdWinApps() {
  const rows = listWinApps();
  history.record('winapps', 'all', `count=${rows.length}`);
  console.log(`Store/UWP packages: ${rows.length}\n`);
  for (const r of rows) {
    console.log(`  ${r.Name} ${r.Version || ''} — ${r.Publisher || '?'}`);
    console.log(`      family: ${r.PackageFamilyName || '?'}`);
  }
}

function cmdBackup() {
  const rows = backupRegistry();
  console.log('Registry backup:\n');
  for (const r of rows) {
    console.log(`  ${r.key}`);
    console.log(`      ${r.ok ? 'OK' : 'FAILED'} — ${r.file}${r.bytes ? ' (' + fmtSize(r.bytes) + ')' : ''}`);
  }
}

function cmdJunk() {
  const rows = scanJunk();
  const total = rows.reduce((a, r) => a + (Number(r.Bytes) || 0), 0);
  history.record('junk', 'scan', `totalBytes=${total}`);
  console.log('Junk scan (read-only):\n');
  for (const r of rows) {
    console.log(`  ${r.Path}`);
    console.log(`      ${r.Files} files, ${fmtSize(r.Bytes)} (older than 1 day)`);
  }
  console.log(`\nTotal: ${fmtSize(total)}`);
}

function cmdHistory(args) {
  const limit = parseInt(args[0], 10) || 25;
  const rows = history.list(limit);
  for (const r of rows) {
    console.log(`#${r.id} ${r.ts} [${r.status}] ${r.action} ${r.target}${r.detail ? ' — ' + r.detail : ''}`);
  }
}

async function cmdUi(args) {
  const portArg = args.indexOf('--port');
  const port = portArg >= 0 ? parseInt(args[portArg + 1], 10) || 8742 : 8742;
  const { start } = require('./server');
  const { spawn } = require('child_process');
  const actual = await start(port);
  const url = `http://127.0.0.1:${actual}`;
  console.log(`Open Uninstaller UI running at ${url}`);
  console.log('Press Ctrl+C to stop.');
  try { spawn('cmd', ['/c', 'start', '', url], { detached: true, stdio: 'ignore' }).unref(); } catch (_) {}
}

function main(argv) {
  const [cmd, ...args] = argv;
  if (cmd === 'ui') return cmdUi(args).catch(e => { console.error(e.message); process.exitCode = 1; });
  switch (cmd) {
    case 'list': return cmdList(args);
    case 'search': return cmdSearch(args);
    case 'details': return cmdDetails(args);
    case 'uninstall': return cmdUninstall(args);
    case 'leftovers': return cmdLeftovers(args);
    case 'autoruns': return cmdAutoruns();
    case 'winapps': return cmdWinApps();
    case 'junk': return cmdJunk();
    case 'backup': return cmdBackup();
    case 'history': return cmdHistory(args);
    case 'gui':
    case 'ui': return cmdUi(args);
    case 'help': console.log(HELP); return;
    case undefined: return cmdUi(args); // no args -> launch the full GUI
    default: console.error(`Unknown command: ${cmd}\n${HELP}`); process.exitCode = 1;
  }
}

module.exports = { main };
