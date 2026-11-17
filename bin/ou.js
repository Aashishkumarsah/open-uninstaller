#!/usr/bin/env node
'use strict';
const path = require('path');
const fs = require('fs');
const { main } = require('../src/cli');

// No arguments -> launch the native Windows desktop app if present.
if (process.argv.length === 2) {
  const exe = path.join(__dirname, '..', 'OpenUninstaller.exe');
  if (fs.existsSync(exe)) {
    const { spawn } = require('child_process');
    const child = spawn(exe, [], { detached: true, stdio: 'ignore', windowsHide: false });
    child.on('error', e => console.error('Failed to launch desktop app:', e.message));
    child.unref();
    console.log('Launched Open Uninstaller (native Windows app).');
    process.exit(0);
  }
}
main(process.argv.slice(2));
