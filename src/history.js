'use strict';
const path = require('path');
const fs = require('fs');
const { DatabaseSync } = require('node:sqlite');

const DATA_DIR = path.join(__dirname, '..', 'data');
fs.mkdirSync(DATA_DIR, { recursive: true });
const DB_PATH = path.join(DATA_DIR, 'history.sqlite');
const settings = require('./settings');

// Optional, user-supplied read-only "reference" SQLite database (never bundled with this project).
// Configure with the OU_REFERENCE_DB environment variable or `referenceDbPath` in data/settings.json.
// Expected table: ILogs(Number, Name, Publisher, Version, RKey, RDN, WVer64Bits). Disabled by default.
function referenceDbPath() {
  const p = process.env.OU_REFERENCE_DB || settings.get().referenceDbPath || '';
  return p && fs.existsSync(p) ? p : '';
}

let db;
function getDb() {
  if (!db) {
    db = new DatabaseSync(DB_PATH);
    db.exec(`
      PRAGMA journal_mode = WAL;
      PRAGMA busy_timeout = 5000;
      CREATE TABLE IF NOT EXISTS operations (
        id      INTEGER PRIMARY KEY AUTOINCREMENT,
        ts      TEXT NOT NULL,
        action  TEXT NOT NULL,
        target  TEXT NOT NULL,
        detail  TEXT,
        status  TEXT NOT NULL DEFAULT 'ok'
      );
      CREATE INDEX IF NOT EXISTS idx_operations_ts ON operations(ts);
    `);
  }
  return db;
}

function record(action, target, detail = '', status = 'ok') {
  try {
    getDb().prepare(
      'INSERT INTO operations(ts, action, target, detail, status) VALUES(?,?,?,?,?)'
    ).run(new Date().toISOString(), String(action), String(target), String(detail), String(status));
  } catch (_) {}
}

function list(limit = 100) {
  return getDb().prepare(
    'SELECT id, ts, action, target, detail, status FROM operations ORDER BY id DESC LIMIT ?'
  ).all(Math.max(1, Math.min(500, Number(limit) || 100)));
}

function clear() {
  getDb().prepare('DELETE FROM operations').run();
  return { ok: true };
}

function queryReferenceLogs(pattern = '', limit = 50) {
  const refPath = referenceDbPath();
  if (!refPath) return { available: false, total: 0, items: [] };
  const ref = new DatabaseSync(refPath, { readOnly: true });
  try {
    const q = String(pattern || '').trim();
    const safeLimit = Math.max(1, Math.min(200, Number(limit) || 50));
    if (!q) {
      const info = ref.prepare('SELECT NProgs, Ver, Date, NLogs FROM Info LIMIT 1').get();
      const items = ref.prepare('SELECT Number, Name, Publisher, Version, RKey, RDN, WVer64Bits FROM ILogs ORDER BY Name ASC LIMIT ?').all(safeLimit);
      return { available: true, info, total: Number(info?.NLogs || items.length), items };
    }
    const like = `%${q}%`;
    const total = ref.prepare('SELECT COUNT(*) as c FROM ILogs WHERE Name LIKE ? OR Publisher LIKE ? OR RDN LIKE ?').get(like, like, like).c;
    const items = ref.prepare('SELECT Number, Name, Publisher, Version, RKey, RDN, WVer64Bits FROM ILogs WHERE Name LIKE ? OR Publisher LIKE ? OR RDN LIKE ? ORDER BY Name ASC LIMIT ?').all(like, like, like, safeLimit);
    return { available: true, total, items };
  } finally {
    ref.close();
  }
}

module.exports = { record, list, clear, queryReferenceLogs, DB_PATH };