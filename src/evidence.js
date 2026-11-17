'use strict';
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { isProtectedPath, runPSJson } = require('./util');
const { record } = require('./history');

// Secure file/folder deletion: 3-pass overwrite (0x00, 0xFF, CSPRNG), random rename, then unlink.
function secureDelete(targetPath, passes = 3) {
  if (!targetPath || isProtectedPath(targetPath)) {
    return { ok: false, error: 'Refusing empty or protected system path' };
  }
  try {
    const stat = fs.statSync(targetPath);
    let shreddedFiles = 0;
    let shreddedBytes = 0;

    function shredFile(fp) {
      const st = fs.statSync(fp);
      const fd = fs.openSync(fp, 'r+');
      try {
        const chunkSize = Math.min(1024 * 1024, Math.max(1, st.size));
        for (let i = 0; i < passes; i++) {
          let off = 0;
          while (off < st.size) {
            const len = Math.min(chunkSize, st.size - off);
            const buf = i === 0 ? Buffer.alloc(len, 0x00) : i === 1 ? Buffer.alloc(len, 0xff) : crypto.randomBytes(len);
            fs.writeSync(fd, buf, 0, len, off);
            off += len;
          }
          fs.fsyncSync(fd);
        }
      } finally {
        fs.closeSync(fd);
      }
      const renamed = path.join(path.dirname(fp), '.shred_' + crypto.randomBytes(6).toString('hex'));
      fs.renameSync(fp, renamed);
      fs.unlinkSync(renamed);
      shreddedFiles++;
      shreddedBytes += st.size;
    }

    if (stat.isDirectory()) {
      const walk = (dir) => {
        for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
          const full = path.join(dir, e.name);
          if (e.isDirectory()) walk(full);
          else if (e.isFile()) shredFile(full);
        }
      };
      walk(targetPath);
      fs.rmSync(targetPath, { recursive: true, force: true });
    } else if (stat.isFile()) {
      shredFile(targetPath);
    } else {
      throw new Error('Unsupported file type');
    }

    record('secure-delete', targetPath, `files=${shreddedFiles} bytes=${shreddedBytes} passes=${passes}`);
    return { ok: true, path: targetPath, shreddedFiles, shreddedBytes, passes };
  } catch (e) {
    record('secure-delete', targetPath, e.message, 'error');
    return { ok: false, error: e.message };
  }
}

// Evidence Remover: wipe unallocated space in a chosen folder/drive with bounded random bytes.
function wipeFreeSpace(folderPath, megabytes = 16) {
  const root = path.resolve(folderPath || process.env.TEMP || 'C:\\');
  if (!fs.existsSync(root)) return { ok: false, error: 'Target folder/drive does not exist' };
  const wipeDir = path.join(root, '.ou_wipe_' + crypto.randomBytes(4).toString('hex'));
  fs.mkdirSync(wipeDir, { recursive: true });
  const wipeFile = path.join(wipeDir, 'wipe.tmp');
  const mb = Math.max(1, Math.min(1024, Number(megabytes) || 16));
  let writtenMb = 0;
  try {
    const fd = fs.openSync(wipeFile, 'w');
    try {
      const buf = crypto.randomBytes(1024 * 1024);
      for (let i = 0; i < mb; i++) {
        fs.writeSync(fd, buf, 0, buf.length);
        writtenMb++;
      }
      fs.fsyncSync(fd);
    } finally {
      fs.closeSync(fd);
    }
    record('wipe-freespace', root, `${writtenMb} MB`);
    return { ok: true, target: root, wipedMB: writtenMb };
  } catch (e) {
    record('wipe-freespace', root, e.message, 'error');
    return { ok: false, error: e.message, wipedMB: writtenMb };
  } finally {
    try { fs.rmSync(wipeDir, { recursive: true, force: true }); } catch (_) {}
  }
}

function listDrives() {
  const script = `
    [System.IO.DriveInfo]::GetDrives() | Where-Object { $_.IsReady } | ForEach-Object {
      $total = [double]$_.TotalSize
      $free = [double]$_.TotalFreeSpace
      $pct = if ($total -gt 0) { [math]::Round(($free / $total) * 100.0, 1) } else { 0 }
      [pscustomobject]@{
        Drive = $_.Name.TrimEnd('\\')
        Label = if ($_.VolumeLabel) { $_.VolumeLabel } else { $_.DriveType.ToString() }
        DriveType = $_.DriveType.ToString()
        FileSystem = $_.DriveFormat
        TotalGB = [math]::Round($total / 1GB, 2)
        FreeGB = [math]::Round($free / 1GB, 2)
        FreePercent = $pct
        Path = $_.RootDirectory.FullName
      }
    }
  `;
  return runPSJson(script, { timeout: 15000 });
}

function secureDeleteBatch(paths = [], passes = 3) {
  const safePasses = Math.max(1, Math.min(7, Number(passes) || 3));
  const list = Array.isArray(paths) ? paths.map(p => String(p || '').trim()).filter(Boolean) : [];
  const results = list.map(p => secureDelete(p, safePasses));
  let deletedPaths = 0;
  let failedPaths = 0;
  let shreddedFiles = 0;
  let shreddedBytes = 0;
  for (const r of results) {
    if (r.ok) {
      deletedPaths++;
      shreddedFiles += Number(r.shreddedFiles || 0);
      shreddedBytes += Number(r.shreddedBytes || 0);
    } else {
      failedPaths++;
    }
  }
  return {
    ok: failedPaths === 0 && deletedPaths > 0,
    totalPaths: list.length,
    deletedPaths,
    failedPaths,
    shreddedFiles,
    shreddedBytes,
    passes: safePasses,
    results,
  };
}

module.exports = { secureDelete, secureDeleteBatch, wipeFreeSpace, listDrives };