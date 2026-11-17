'use strict';
const { spawn } = require('child_process');
const { record } = require('./history');

const TOOLS = [
  { id: 'rstrui', label: 'System Restore', command: 'rstrui.exe', args: [] },
  { id: 'cleanmgr', label: 'Disk Cleanup', command: 'cleanmgr.exe', args: [] },
  { id: 'appwiz', label: 'Programs and Features', command: 'control.exe', args: ['/name', 'Microsoft.ProgramsAndFeatures'] },
  { id: 'services', label: 'Services', command: 'services.msc', args: [] },
  { id: 'eventvwr', label: 'Event Viewer', command: 'eventvwr.msc', args: [] },
  { id: 'msconfig', label: 'System Configuration', command: 'msconfig.exe', args: [] },
  { id: 'taskmgr', label: 'Task Manager', command: 'taskmgr.exe', args: [] },
  { id: 'diskmgmt', label: 'Disk Management', command: 'diskmgmt.msc', args: [] },
  { id: 'device', label: 'Device Manager', command: 'devmgmt.msc', args: [] },
  { id: 'resmon', label: 'Resource Monitor', command: 'resmon.exe', args: [] },
  { id: 'msinfo', label: 'System Information', command: 'msinfo32.exe', args: [] },
  { id: 'sysdm', label: 'System Properties', command: 'sysdm.cpl', args: [] },
  { id: 'taskschd', label: 'Task Scheduler', command: 'taskschd.msc', args: [] },
  { id: 'defrag', label: 'Optimize Drives', command: 'dfrgui.exe', args: [] },
  { id: 'osk', label: 'On-Screen Keyboard', command: 'osk.exe', args: [] },
  { id: 'dxdiag', label: 'DirectX Diagnostic Tool', command: 'dxdiag.exe', args: [] },
  { id: 'netstat', label: 'TCP/IP Netstat Connections', command: 'cmd.exe', args: ['/c', 'start', 'cmd', '/k', 'netstat -abno'] },
  { id: 'dism', label: 'Repair Windows Image (DISM)', command: 'cmd.exe', args: ['/c', 'start', 'cmd', '/k', 'DISM /Online /Cleanup-Image /RestoreHealth'] },
];

function launch(id) {
  const tool = TOOLS.find(t => t.id === id);
  if (!tool) return { ok: false, error: 'unknown tool' };
  const needsShellStart = /\.(msc|cpl)$/i.test(tool.command);
  const exe = needsShellStart ? 'cmd.exe' : tool.command;
  const args = needsShellStart ? ['/c', 'start', '', tool.command, ...tool.args] : tool.args;
  try {
    const child = spawn(exe, args, { detached: true, stdio: 'ignore', windowsHide: false, shell: false });
    child.on('error', e => record('wintool', id, e.message, 'error'));
    child.unref();
    record('wintool', id, `launched ${tool.command}`);
    return { ok: true, tool };
  } catch (e) {
    record('wintool', id, e.message, 'error');
    return { ok: false, error: e.message };
  }
}

module.exports = { TOOLS, launch };