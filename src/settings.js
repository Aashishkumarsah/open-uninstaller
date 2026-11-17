'use strict';
const fs = require('fs');
const path = require('path');

const FILE = path.join(__dirname, '..', 'data', 'settings.json');
const DEFAULTS = {
  productName: 'Open Uninstaller',
  version: '0.6.0',
  license: 'MIT',
  referenceDbPath: '',
  theme: 'dark',
  confirmBeforeClean: true,
  backupBeforeUninstall: true,
  dryRunDefault: false,
  leftoversDepth: 'moderate',
  historyLimit: 200,
};

function get() {
  let current = {};
  try { current = JSON.parse(fs.readFileSync(FILE, 'utf8')); } catch (_) {}
  return { ...DEFAULTS, ...current, version: DEFAULTS.version };
}

function set(patch) {
  const next = { ...get(), ...patch };
  fs.mkdirSync(path.dirname(FILE), { recursive: true });
  fs.writeFileSync(FILE, JSON.stringify(next, null, 2));
  return next;
}

module.exports = { get, set, DEFAULTS };
