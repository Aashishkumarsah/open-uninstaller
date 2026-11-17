'use strict';
const fs = require('fs');
const path = require('path');
const { runPS, psLiteral } = require('./util');
const { BACKUP_DIR } = require('./backup');
const { record } = require('./history');

function dirSizeBytes(dirPath) {
  let total = 0;
  try {
    for (const ent of fs.readdirSync(dirPath, { withFileTypes: true })) {
      const full = path.join(dirPath, ent.name);
      if (ent.isDirectory()) total += dirSizeBytes(full);
      else if (ent.isFile()) total += fs.statSync(full).size;
    }
  } catch (_) {}
  return total;
}

function list() {
  fs.mkdirSync(BACKUP_DIR, { recursive: true });
  const rows = [];
  for (const ent of fs.readdirSync(BACKUP_DIR, { withFileTypes: true })) {
    const p = path.join(BACKUP_DIR, ent.name);
    const st = fs.statSync(p);
    if (ent.isFile() && ent.name.endsWith('.reg')) {
      rows.push({
        file: ent.name,
        kind: 'reg',
        sizeKB: Math.round((st.size / 1024) * 10) / 10,
        created: st.mtime.toISOString(),
        path: p,
        bytes: st.size,
      });
    } else if (ent.isDirectory()) {
      const bytes = dirSizeBytes(p);
      rows.push({
        file: ent.name,
        kind: 'folder-backup',
        sizeKB: Math.round((bytes / 1024) * 10) / 10,
        created: st.mtime.toISOString(),
        path: p,
        bytes,
      });
    }
  }
  return rows.sort((a, b) => b.created.localeCompare(a.created));
}

function restore(file) {
  const target = path.join(BACKUP_DIR, path.basename(String(file || '')));
  if (!fs.existsSync(target)) return { ok: false, error: 'backup not found' };
  if (fs.statSync(target).isDirectory()) {
    // Restore any exported .reg keys inside a leftovers/regopt backup folder
    const regs = fs.readdirSync(target).filter(f => f.endsWith('.reg'));
    const imported = [];
    for (const r of regs) {
      runPS(`& reg.exe import ${psLiteral(path.join(target, r))} 2>&1`, { timeout: 300000 });
      imported.push(r);
    }
    record('restore', file, `folder=${target} regs=${imported.length}`);
    return { ok: true, folder: target, importedRegFiles: imported };
  }
  try {
    const out = runPS(`& reg.exe import ${psLiteral(target)} 2>&1`, { timeout: 300000 });
    record('restore', file, 'ok');
    return { ok: true, output: out.slice(0, 300) };
  } catch (e) {
    record('restore', file, e.message, 'error');
    return { ok: false, error: e.message };
  }
}

function deleteBackup(file) {
  const base = path.basename(String(file || '').trim());
  if (!base) return { ok: false, error: 'Backup filename required' };
  const root = path.resolve(BACKUP_DIR);
  const target = path.resolve(BACKUP_DIR, base);
  if (!target.startsWith(root + path.sep) || !fs.existsSync(target)) {
    return { ok: false, error: 'Backup not found: ' + base };
  }
  fs.rmSync(target, { recursive: true, force: true });
  record('backup-delete', base, 'deleted');
  return { ok: true, file: base };
}

function deleteOldBackups(days = 30) {
  const safeDays = Math.max(1, Number(days) || 30);
  const cutoff = Date.now() - safeDays * 86400000;
  let removed = 0;
  for (const row of list()) {
    const t = Date.parse(row.created);
    if (Number.isFinite(t) && t < cutoff) {
      const res = deleteBackup(row.file);
      if (res.ok) removed++;
    }
  }
  record('backup-clean-old', `>${safeDays}d`, `removed=${removed}`);
  return { ok: true, days: safeDays, removed };
}

module.exports = { list, restore, deleteBackup, deleteOldBackups };
