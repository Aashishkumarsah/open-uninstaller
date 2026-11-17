'use strict';
const path = require('path');
const fs = require('fs');
const { runPS, psLiteral } = require('./util');
const { record } = require('./history');

const BACKUP_DIR = path.join(__dirname, '..', 'data', 'backups');

function backupRegistry(label = 'manual') {
  fs.mkdirSync(BACKUP_DIR, { recursive: true });
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const keys = [
    ['HKCU', 'HKCU\\Software'],
    ['HKLM', 'HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall'],
  ];
  const results = [];
  for (const [name, key] of keys) {
    const file = path.join(BACKUP_DIR, `${stamp}-${name}.reg`);
    try {
      const out = runPS(`& reg.exe export ${psLiteral(key)} ${psLiteral(file)} /y 2>&1`, { timeout: 300000 });
      const exists = fs.existsSync(file);
      results.push({ key, file, ok: exists, bytes: exists ? fs.statSync(file).size : 0, output: out.slice(0, 200) });
      record('backup', key, file, exists ? 'ok' : 'failed');
    } catch (e) {
      results.push({ key, file, ok: false, error: e.message.slice(0, 200) });
      record('backup', key, file, 'error');
    }
  }
  return results;
}

module.exports = { backupRegistry, BACKUP_DIR };