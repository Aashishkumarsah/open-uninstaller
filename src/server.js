'use strict';
const http = require('http');
const fs = require('fs');
const path = require('path');
const { URL } = require('url');

const { getInstalledPrograms, findPrograms, exportPrograms, removeProgramEntry } = require('./inventory');
const { runUninstall } = require('./uninstall');
const leftovers = require('./leftovers');
const autorun = require('./autorun');
const winapps = require('./winapps');
const junk = require('./junk');
const history = require('./history');
const { backupRegistry, BACKUP_DIR } = require('./backup');
const tracks = require('./tracks');
const extensions = require('./extensions');
const hunter = require('./hunter');
const wintools = require('./wintools');
const evidence = require('./evidence');
const registry = require('./registry');
const restore = require('./restore');
const settings = require('./settings');
const tracer = require('./tracer');

const UI_DIR = path.join(__dirname, '..', 'ui');

function json(res, data, code = 200) {
  res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8', 'Access-Control-Allow-Origin': '*' });
  res.end(JSON.stringify(data));
}

function backupUninstallKey(b) {
  const hive = String((b && b.hivePath) || '').replace(/^HKLM:\\/i, 'HKLM\\').replace(/^HKCU:\\/i, 'HKCU\\');
  const keyName = String((b && b.registryKey) || '').trim();
  if (!hive || !keyName) return backupRegistry();
  const fullKey = `${hive}\\${keyName}`;
  fs.mkdirSync(BACKUP_DIR, { recursive: true });
  const safe = String((b && b.name) || keyName).replace(/[^a-zA-Z0-9._-]+/g, '_').slice(0, 40);
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const file = path.join(BACKUP_DIR, `prog-${safe}-${stamp}.reg`);
  const res = registry.exportKey(fullKey, file);
  if (res.ok) history.record('backup-key', fullKey, file);
  return Object.assign({ file, key: fullKey }, res);
}

const routes = {
  '/api/status': () => ({ ok: true, product: settings.get().productName, version: settings.get().version, license: settings.get().license }),
  '/api/settings': () => settings.get(),
  '/api/programs': () => getInstalledPrograms(),
  '/api/traces': () => ({ active: tracer.getActiveTrace(), logs: tracer.listTraces() }),
  '/api/trace-items': (url) => tracer.getTraceItems(url.searchParams.get('id')),
  '/api/autoruns': () => autorun.listAutoruns(),
  '/api/winapps': () => winapps.listWinApps(),
  '/api/junk': () => junk.scanJunk(),
  '/api/junk-detailed': () => junk.scanJunkDetailed({}),
  '/api/history': () => history.list(200),
  '/api/backups': () => restore.list(),
  '/api/tracks': () => tracks.scan(),
  '/api/extensions': () => extensions.scan(),
  '/api/processes': () => hunter.processes(),
  '/api/registry/invalid': () => registry.scanInvalid(),
  '/api/wintools': () => wintools.TOOLS,
  '/api/drives': () => evidence.listDrives(),
  '/api/ref-logs': () => history.queryReferenceLogs('', 50),
};

const actions = {
  search: b => findPrograms(b.pattern || '', { includeSystem: !!b.includeSystem }),
  details: b => findPrograms(b.pattern || '')[0] || null,
  uninstall: b => runUninstall(b.pattern, { all: !!b.all, quiet: !!b.quiet, forceMsi: !!b.forceMsi, exact: !!b.exact }),
  'uninstall-record': b => {
    history.record('uninstall', b.name || 'Program', b.detail || (`exit=${b.exitCode == null ? '?' : b.exitCode}`), b.status || 'ok');
    return { ok: true };
  },
  'export-programs': b => exportPrograms(b.format || 'csv', { includeSystem: !!b.includeSystem }),
  'remove-entry': b => removeProgramEntry(b || {}),
  'forced-probe': b => leftovers.forcedUninstallProbe(b || {}),
  leftovers: b => leftovers.scan(b.pattern || '', b.mode || 'moderate', b),
  'leftovers-clean': b => leftovers.cleanLeftovers(b),
  'trace-start': b => tracer.startTrace(b.name || '', b.installerPath || ''),
  'trace-stop': b => tracer.stopTrace(b.name || ''),
  'trace-items': b => tracer.getTraceItems(b.id),
  'trace-delete': b => tracer.deleteTrace(b.id),
  'trace-export': b => tracer.exportTrace(b.id, b.format || 'json'),
  'trace-import': b => tracer.importTrace(b.file),
  'autorun-toggle': b => autorun.setAutorunEnabled(b),
  'autorun-add': b => autorun.addAutorun(b),
  'autorun-remove': b => autorun.removeAutorun(b),
  'autorun-clean-invalid': () => autorun.removeInvalidAutoruns(),
  'winapp-remove': b => winapps.removeWinApp(b.packageFullName),
  'winapp-reset': b => winapps.resetWinApp(b.packageFullName, b.packageFamilyName),
  backup: () => backupRegistry(),
  'backup-key': b => backupUninstallKey(b),
  restore: b => restore.restore(b.file),
  'backup-delete': b => restore.deleteBackup(b.file),
  'backup-clean-old': b => restore.deleteOldBackups(b.days || 30),
  'tracks-clean': b => tracks.clean(b.ids || []),
  'extension-toggle': b => extensions.setEnabled(b.path, !!b.enabled),
  'junk-scan-detailed': b => junk.scanJunkDetailed(b || {}),
  'junk-clean': b => junk.cleanJunk(b.files ? { files: b.files, paths: b.paths || [] } : (b.paths || [])),
  'registry-clean': b => registry.cleanInvalid(b.items || []),
  hunt: b => hunter.hunt(b.pattern || ''),
  'hunt-path': b => hunter.findFromPath(b.path || ''),
  'kill-process': b => hunter.killProcess(b.pid),
  wintool: b => wintools.launch(b.id),
  'secure-delete': b => evidence.secureDelete(b.path, b.passes || 3),
  'secure-delete-batch': b => evidence.secureDeleteBatch(b.paths || (b.path ? [b.path] : []), b.passes || 3),
  'wipe-freespace': b => evidence.wipeFreeSpace(b.path, b.megabytes || 16),
  'history-clear': () => history.clear(),
  'ref-logs': b => history.queryReferenceLogs(b.pattern || '', b.limit || 50),
  settings: b => settings.set(b.patch || {}),
};

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');
  if (req.method === 'OPTIONS') {
    res.writeHead(204, { 'Access-Control-Allow-Origin': '*', 'Access-Control-Allow-Methods': 'GET,POST', 'Access-Control-Allow-Headers': 'Content-Type' });
    return res.end();
  }

  if (req.method === 'GET' && routes[url.pathname]) {
    try { return json(res, { ok: true, data: routes[url.pathname](url) }); }
    catch (e) { return json(res, { ok: false, error: e.message }, 500); }
  }

  if (req.method === 'POST' && url.pathname === '/api/action') {
    let body = '';
    req.on('data', c => body += c);
    req.on('end', async () => {
      try {
        const b = JSON.parse(body || '{}');
        const fn = actions[b.action];
        if (!fn) return json(res, { ok: false, error: 'unknown action' }, 400);
        const result = await fn(b);
        return json(res, { ok: true, data: result });
      } catch (e) { return json(res, { ok: false, error: e.message }, 500); }
    });
    return;
  }

  let rel = url.pathname === '/' ? '/index.html' : url.pathname;
  const file = path.join(UI_DIR, path.normalize(rel).replace(/^(\.\.[\\/])+/, ''));
  if (!file.startsWith(UI_DIR)) { res.writeHead(403); return res.end('forbidden'); }
  fs.readFile(file, (err, data) => {
    if (err) { res.writeHead(404); return res.end('not found'); }
    const ext = path.extname(file);
    const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8' };
    res.writeHead(200, { 'Content-Type': types[ext] || 'application/octet-stream' });
    res.end(data);
  });
});

function start(port = 8742) {
  return new Promise(resolve => server.listen(port, '127.0.0.1', () => resolve(port)));
}

module.exports = { start, server };
