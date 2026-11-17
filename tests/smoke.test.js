'use strict';
// Pure-function smoke tests. They never touch the registry, the filesystem outside data/, or any process.
const test = require('node:test');
const assert = require('node:assert/strict');

const { formatInstallDate, classifyProgram, isRecentInstall } = require('../src/inventory');
const { splitCommand, parseArgs } = require('../src/uninstall');
const { isProtectedPath, isProtectedRegKey, psLiteral } = require('../src/util');

test('formatInstallDate converts YYYYMMDD and leaves other values alone', () => {
  assert.equal(formatInstallDate('20260115'), '2026-01-15');
  assert.equal(formatInstallDate('unknown'), 'unknown');
  assert.equal(formatInstallDate(''), '');
});

test('isRecentInstall accepts today and rejects old or invalid dates', () => {
  assert.equal(isRecentInstall(new Date().toISOString().slice(0, 10)), true);
  assert.equal(isRecentInstall('2001-01-01'), false);
  assert.equal(isRecentInstall('not-a-date'), false);
});

test('classifyProgram assigns sensible categories', () => {
  assert.equal(classifyProgram({ DisplayName: 'Microsoft Visual C++ 2022 Redistributable' }), 'System & Runtimes');
  assert.equal(classifyProgram({ DisplayName: 'Mozilla Firefox' }), 'Internet');
  assert.equal(classifyProgram({ DisplayName: 'VLC media player' }), 'Multimedia');
  assert.equal(classifyProgram({ DisplayName: 'Some Unknown Thing' }), 'General');
});

test('splitCommand handles quoted and unquoted uninstall strings', () => {
  const a = splitCommand('"C:\\Program Files\\App\\unins000.exe" /SILENT');
  assert.equal(a.exe, 'C:\\Program Files\\App\\unins000.exe');
  assert.deepEqual(a.args, ['/SILENT']);
  const b = splitCommand('msiexec.exe /x {GUID}');
  assert.equal(b.exe, 'msiexec.exe');
  assert.deepEqual(b.args, ['/x', '{GUID}']);
  assert.deepEqual(parseArgs('"a b" c'), ['a b', 'c']);
});

test('isProtectedPath refuses system roots but allows ordinary app folders', () => {
  assert.equal(isProtectedPath('C:\\Windows\\System32'), true);
  assert.equal(isProtectedPath('C:\\Program Files'), true);
  assert.equal(isProtectedPath(''), true);
  assert.equal(isProtectedPath('C:\\Program Files\\SomeVendor\\SomeApp'), false);
});

test('isProtectedRegKey refuses hive roots and the Uninstall container', () => {
  assert.equal(isProtectedRegKey('HKLM\\SOFTWARE'), true);
  assert.equal(isProtectedRegKey('HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall'), true);
  assert.equal(isProtectedRegKey(''), true);
  assert.equal(isProtectedRegKey('HKCU\\Software\\SomeVendor\\SomeApp'), false);
});

test('psLiteral escapes single quotes', () => {
  assert.equal(psLiteral("it's"), "'it''s'");
  assert.equal(psLiteral(null), "''");
});
