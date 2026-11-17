'use strict';
const API = 'http://127.0.0.1:8742';
const $ = s => document.querySelector(s);
let state = {};

const NAV = [
  ['dashboard','▦','Dashboard','Overview, storage health, and quick actions'],
  ['programs','▤','Uninstaller','Installed programs, forced probe, export & uninstall'],
  ['hunter','◎','Hunter Mode','Find programs from running processes or shortcuts'],
  ['leftovers','⌕','Leftover Scanner','Find & safely remove leftover files and registry keys'],
  ['traces','◉','Traced Programs','Install monitor with before/after SQLite snapshots'],
  ['autoruns','↻','Autorun Manager','Startup entries: enable, disable, clean invalid, or add'],
  ['winapps','▣','Windows Apps','Store / UWP packages, reset cache, or remove'],
  ['tracks','✕','Tracks Cleaner','Browser, Office and Windows history'],
  ['extensions','⊞','Browser Extensions','Chrome, Edge, Brave, Firefox & BHO add-ons'],
  ['junk','♻','Junk Files','Individual temporary, log & dump files cleanup'],
  ['backup','▧','Backup & Restore','Registry export, folder backups, and restore'],
  ['registry','⚙','Registry Optimizer','Stale uninstall, startup & App Path entries'],
  ['wintools','🛠','Windows Tools','Launch 18 Windows system utilities'],
  ['evidence','🗑','Evidence Remover','3-pass secure batch shredder & drive free-space wipe'],
  ['history','◷','Logs & History','Operation log and optional reference database'],
  ['settings','⚙','Settings','Theme, leftover mode, and safety preferences'],
];

async function api(path, opts) {
  const r = await fetch(API + path, opts);
  return r.json();
}
async function get(path) { const r = await api(path); if (!r.ok) throw new Error(r.error); return r.data; }
async function action(name, payload = {}) {
  const r = await api('/api/action', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({action:name,...payload}) });
  if (!r.ok) throw new Error(r.error); toast(name.replace(/-/g,' ')+' completed'); return r.data;
}
function toast(msg){ const t=$('#toast'); t.textContent=msg; t.classList.add('show'); clearTimeout(t._t); t._t=setTimeout(()=>t.classList.remove('show'),2600); }
function esc(s){ return String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }
function size(b){ b=Number(b)||0; if(b<1024)return b+' B'; if(b<1048576)return (b/1024).toFixed(1)+' KB'; if(b<1073741824)return (b/1048576).toFixed(1)+' MB'; return (b/1073741824).toFixed(2)+' GB'; }
function table(cols, rows){
  if(!rows||!rows.length) return '<div class="empty">No results.</div>';
  return '<table><thead><tr>'+cols.map(c=>`<th>${esc(c[0])}</th>`).join('')+'</tr></thead><tbody>'+
    rows.map((r,i)=>'<tr>'+cols.map(c=>`<td>${c[1](r,i)}</td>`).join('')+'</tr>').join('')+'</tbody></table>';
}

function renderNav(){
  $('#nav').innerHTML = NAV.map(([id,ico,label])=>`<button data-page="${id}"><span class="ico">${ico}</span><span>${label}</span></button>`).join('');
  $('#nav').querySelectorAll('button').forEach(b=>b.onclick=()=>{ location.hash = b.dataset.page; load(b.dataset.page); });
}

async function load(page){
  if(!PAGES[page]) page='dashboard';
  NAV.forEach(n=>{ if(n[0]===page){ $('#page-title').textContent=n[2]; $('#page-sub').textContent=n[3]; }});
  document.querySelectorAll('#nav button').forEach(b=>b.classList.toggle('active',b.dataset.page===page));
  const c=$('#content'); c.innerHTML='<div class="loading">Loading…</div>';
  try { await PAGES[page](c); } catch(e){ c.innerHTML=`<div class="card"><h3>Error</h3><pre>${esc(e.message)}</pre></div>`; }
}

const PAGES = {
  async dashboard(c){
    const [s,st,progs,drv]=await Promise.all([get('/api/status'),get('/api/history'),get('/api/programs'),get('/api/drives')]);
    c.innerHTML=`<div class="grid">
      <div class="stat"><div class="n">${progs.length}</div><div class="l">installed desktop programs</div></div>
      <div class="stat"><div class="n">${st.length}</div><div class="l">logged local operations</div></div>
      <div class="stat"><div class="n">${drv.length}</div><div class="l">local drives detected</div></div>
      <div class="stat"><div class="n">v${esc(s.version)}</div><div class="l">${esc(s.license)}</div></div>
    </div>
    <div class="card"><h3>Local Storage</h3>${drv.map(d=>{
      const used=Math.max(0,Math.min(100,100-(Number(d.FreePercent)||0)));
      return `<div style="margin-bottom:10px"><b>${esc(d.Drive)}</b> (${esc(d.Label)}, ${esc(d.FileSystem)}) — ${esc(d.FreeGB)} GB free of ${esc(d.TotalGB)} GB<div class="bar"><i style="width:${used}%"></i></div></div>`;
    }).join('')}</div>
    <div class="card"><h3>Quick actions</h3><div class="row">
      <button class="btn" id="qd-prog">Programs</button>
      <button class="btn blue" id="qd-trace">Traced Programs</button>
      <button class="btn blue" id="qd-junk">Junk Cleaner</button>
      <button class="btn ghost" id="qd-backup">Backup Registry</button>
      <button class="btn ghost" id="qd-tools">Windows Tools</button>
    </div><p class="mut">All analysis and cleanup run locally via PowerShell and SQLite.</p></div>`;
    $('#qd-prog').onclick=()=>load('programs');
    $('#qd-trace').onclick=()=>load('traces');
    $('#qd-junk').onclick=()=>load('junk');
    $('#qd-backup').onclick=()=>load('backup');
    $('#qd-tools').onclick=()=>load('wintools');
  },

  async programs(c){
    const rows=await get('/api/programs'); state.programs=rows;
    const render=(list)=>{
      const cat=$('#cat').value;
      const shown = cat==='All' ? list : list.filter(r=>(cat==='New' ? r.RecentInstall : (cat==='64-bit'||cat==='32-bit'||cat==='User') ? r.Architecture===cat : r.Category===cat));
      $('#res').innerHTML=table([
        ['Program',r=>`<b>${esc(r.DisplayName)}</b>`],
        ['Version',r=>esc(r.DisplayVersion||'')],
        ['Publisher',r=>esc(r.Publisher||'')],
        ['Category',r=>`<span class="pill">${esc(r.Category||'General')}</span>`],
        ['Arch',r=>esc(r.Architecture||'')],
        ['Size',r=>size(Number(r.EstimatedSize)*1024)],
        ['',r=>`<button class="btn danger" data-u="${esc(r.DisplayName)}">Uninstall</button> <button class="btn ghost" data-l="${esc(r.DisplayName)}">Leftovers</button> <button class="btn ghost" data-rm-entry="${esc(r.RegistryKey)}" data-hive="${esc(r.HivePath)}" data-name="${esc(r.DisplayName)}">Remove Entry</button>`]
      ],shown);
      c.querySelectorAll('[data-u]').forEach(b=>b.onclick=async()=>{
        if(!confirm('Launch the vendor uninstaller for "'+b.dataset.u+'"?'))return;
        await action('uninstall',{pattern:b.dataset.u,exact:true});
      });
      c.querySelectorAll('[data-l]').forEach(b=>b.onclick=()=>{ state.pendingLeftover=b.dataset.l; load('leftovers'); });
      c.querySelectorAll('[data-rm-entry]').forEach(b=>b.onclick=async()=>{
        if(!confirm('Back up and remove ONLY the Uninstall registry entry for "'+b.dataset.name+'"?'))return;
        await action('remove-entry',{registryKey:b.dataset.rmEntry,hivePath:b.dataset.hive,name:b.dataset.name});
        load('programs');
      });
    };
    c.innerHTML=`<div class="card"><div class="row">
      <input id="q" placeholder="Filter (name, regex or * wildcard)" style="flex:1">
      <select id="cat"><option value="All">All Categories</option><option value="New">New (30d)</option><option value="Internet">Internet</option><option value="Games">Games</option><option value="Multimedia">Multimedia</option><option value="Utilities">Utilities</option><option value="System & Runtimes">System & Runtimes</option><option value="General">General</option><option value="64-bit">64-bit</option><option value="32-bit">32-bit</option><option value="User">User</option></select>
      <button class="btn" id="go">Search</button>
      <button class="btn blue" id="ecsv">Export CSV</button>
      <button class="btn ghost" id="ehtml">Export HTML</button>
    </div><div id="res"></div></div>`;
    render(rows);
    $('#go').onclick=async()=>{ state.programs=await action('search',{pattern:$('#q').value}); render(state.programs); };
    $('#cat').onchange=()=>render(state.programs);
    $('#ecsv').onclick=async()=>{ const r=await action('export-programs',{format:'csv'}); toast('Saved: '+r.file); };
    $('#ehtml').onclick=async()=>{ const r=await action('export-programs',{format:'html'}); toast('Saved: '+r.file); };
  },

  async hunter(c){
    const procs=await get('/api/processes');
    c.innerHTML=`<div class="card"><h3>Hunt a program or shortcut (.lnk / .exe / name)</h3><div class="row"><input id="p" placeholder="process, program name, or shortcut path" style="flex:1"><button class="btn" id="h">Hunt</button></div><div id="r"></div></div>`;
    const draw=(procsList,progsList)=>`<p><b>${procsList.length}</b> processes, <b>${progsList.length}</b> programs</p>`+
      table([['PID',x=>esc(String(x.ProcessId))],['Process',x=>esc(x.Name)],['Window',x=>esc(x.WindowTitle||'')],['Path',x=>`<span class="mono wrap">${esc(x.ExecutablePath||x.Path||'')}</span>`],['',x=>`<button class="btn danger" data-k="${x.ProcessId}">Kill</button>`]],procsList)+
      (progsList.length?`<h3>Related installed programs</h3>`+table([['Program',x=>esc(x.DisplayName)],['Version',x=>esc(x.DisplayVersion||'')],['Location',x=>`<span class="mono wrap">${esc(x.InstallLocation||'')}</span>`]],progsList):'');
    const bindKills=()=>$('#r').querySelectorAll('[data-k]').forEach(b=>b.onclick=async()=>{ if(!confirm('Terminate PID '+b.dataset.k+'?'))return; await action('kill-process',{pid:Number(b.dataset.k)}); load('hunter'); });
    $('#r').innerHTML=draw(procs,[]); bindKills();
    $('#h').onclick=async()=>{ const r=await action('hunt',{pattern:$('#p').value}); $('#r').innerHTML=draw(r.processes,r.programs); bindKills(); };
  },

  async leftovers(c){
    const pre=state.pendingLeftover||''; state.pendingLeftover='';
    c.innerHTML=`<div class="card"><h3>Leftover & Forced Uninstall Scanner</h3><div class="row"><input id="p" value="${esc(pre)}" placeholder="program name, folder, .exe, or .lnk path" style="flex:1"><select id="m"><option value="safe">Safe</option><option value="moderate" selected>Moderate</option><option value="advanced">Advanced</option></select><button class="btn" id="s">Scan</button><button class="btn blue" id="fp">Forced Probe</button></div><div id="r" class="mut">Select mode and click Scan or Forced Probe.</div></div>`;
    const renderResults=(r)=>{
      $('#r').innerHTML=`<h3>Folders & Files (${r.filesystem.length})</h3>`+
        r.filesystem.map(x=>`<label style="display:block"><input type="checkbox" class="lf" value="${esc(x.Path)}" checked> <span class="mono">${esc(x.Path)}</span></label>`).join('')+
        `<h3>Registry keys (${r.registry.length})</h3>`+
        r.registry.map(x=>`<label style="display:block"><input type="checkbox" class="lr" value="${esc(x.Key)}" checked> <span class="mono">${esc(x.Key)}</span></label>`).join('')+
        ((r.filesystem.length||r.registry.length)?`<div class="row" style="margin-top:12px"><button class="btn danger" id="lc">Backup & Delete Selected</button></div>`:'<p>No leftovers found.</p>');
      const lc=$('#lc');
      if(lc) lc.onclick=async()=>{
        const files=[...$('#r').querySelectorAll('.lf:checked')].map(x=>x.value);
        const registry=[...$('#r').querySelectorAll('.lr:checked')].map(x=>x.value);
        if(!confirm(`Backup and delete ${files.length} path(s) and ${registry.length} registry key(s)?`))return;
        const res=await action('leftovers-clean',{files,registry,backupFirst:true});
        lc.insertAdjacentHTML('afterend','<pre>'+esc(JSON.stringify(res,null,2))+'</pre>');
      };
    };
    const runScan=async()=>renderResults(await action('leftovers',{pattern:$('#p').value,mode:$('#m').value}));
    $('#s').onclick=runScan;
    $('#fp').onclick=async()=>renderResults(await action('forced-probe',{targetPath:$('#p').value,programName:$('#p').value,mode:$('#m').value}));
    if(pre) runScan();
  },

  async traces(c){
    const d=await get('/api/traces');
    const act=d.active;
    c.innerHTML=`<div class="card"><h3>Installation Monitor (Before / After Snapshot Diff)</h3>
      <div class="row">
        <input id="tn" placeholder="Program name to trace" style="flex:1">
        <button class="btn" id="ts" ${act?'disabled':''}>Install & Monitor</button>
        <button class="btn danger" id="tp" ${act?'':'disabled'}>Stop & Save Trace</button>
      </div>
      ${act?`<p class="pill warn">Active trace: ${esc(act.name)} (started ${esc(act.startedAt)})</p>`:'<p class="mut">Idle — click Install & Monitor before running a setup.</p>'}
    </div>
    <div class="card"><h3>Saved Traces (${d.logs.length})</h3>${table([
      ['ID',r=>r.id],
      ['Program',r=>`<b>${esc(r.name)}</b>`],
      ['Items',r=>String(r.ItemsCount||0)],
      ['Stopped',r=>esc(r.StoppedAt||'')],
      ['',r=>`<button class="btn ghost" data-ti="${r.id}">Items</button> <button class="btn blue" data-te="${r.id}">Export HTML</button> <button class="btn danger" data-td="${r.id}">Delete</button>`]
    ],d.logs)}<div id="tout"></div></div>`;
    $('#ts').onclick=async()=>{ await action('trace-start',{name:$('#tn').value||'Tracked Install'}); load('traces'); };
    $('#tp').onclick=async()=>{ await action('trace-stop',{name:$('#tn').value}); load('traces'); };
    c.querySelectorAll('[data-ti]').forEach(b=>b.onclick=async()=>{
      const res=await action('trace-items',{id:Number(b.dataset.ti)});
      $('#tout').innerHTML='<h3>Recorded Items ('+res.items.length+')</h3>'+table([['Kind',x=>esc(x.Kind)],['Path',x=>`<span class="mono wrap">${esc(x.Path)}</span>`],['Detail',x=>esc(x.Detail||'')]],res.items);
    });
    c.querySelectorAll('[data-te]').forEach(b=>b.onclick=async()=>{ const r=await action('trace-export',{id:Number(b.dataset.te),format:'html'}); toast('Exported: '+r.file); });
    c.querySelectorAll('[data-td]').forEach(b=>b.onclick=async()=>{ if(!confirm('Delete trace log?'))return; await action('trace-delete',{id:Number(b.dataset.td)}); load('traces'); });
  },

  async autoruns(c){
    const rows=await get('/api/autoruns');
    c.innerHTML=`<div class="card"><div class="row"><h3 style="margin:0;flex:1">Startup entries (${rows.length})</h3><button class="btn danger" id="acinv">Remove All Invalid</button></div>`+
      table([['Name',r=>esc(r.Name)],['Status',r=>`<span class="pill ${r.Status==='INVALID'?'err':r.Status==='RUNNING'?'ok':''}">${esc(r.Status)}</span>`],['Command',r=>`<span class="mono wrap">${esc(r.Command)}</span>`],['Location',r=>esc(r.HivePath)],['',r=>`<button class="btn ghost" data-t="${esc(r.Name)}" data-h="${esc(r.HivePath)}" data-on="${r.Enabled?'false':'true'}">${r.Enabled?'Disable':'Enable'}</button> <button class="btn danger" data-rm="${esc(r.Name)}" data-h="${esc(r.HivePath)}" data-c="${esc(r.Command)}">Remove</button>`]],rows)+`</div>`;
    $('#acinv').onclick=async()=>{ if(!confirm('Remove all INVALID startup entries?'))return; await action('autorun-clean-invalid'); load('autoruns'); };
    c.querySelectorAll('[data-t]').forEach(b=>b.onclick=async()=>{ await action('autorun-toggle',{name:b.dataset.t,hivePath:b.dataset.h,enabled:b.dataset.on==='true'}); load('autoruns'); });
    c.querySelectorAll('[data-rm]').forEach(b=>b.onclick=async()=>{ if(!confirm('Remove startup entry '+b.dataset.rm+'?'))return; await action('autorun-remove',{name:b.dataset.rm,hivePath:b.dataset.h,command:b.dataset.c}); load('autoruns'); });
  },

  async winapps(c){
    const rows=await get('/api/winapps');
    c.innerHTML=`<div class="card"><h3>Store / UWP packages (${rows.length})</h3>`+table([['Name',r=>esc(r.DisplayName||r.Name)],['Version',r=>esc(r.Version)],['Family',r=>`<span class="mono wrap">${esc(r.PackageFamilyName)}</span>`],['',r=>(r.NonRemovable?'<span class="mut">Protected</span>':`<button class="btn danger" data-wa="${esc(r.PackageFullName)}">Uninstall</button> `)+`<button class="btn ghost" data-wr="${esc(r.PackageFullName)}" data-wf="${esc(r.PackageFamilyName)}">Reset</button>`]],rows)+`</div>`;
    c.querySelectorAll('[data-wa]').forEach(b=>b.onclick=async()=>{ if(!confirm('Uninstall '+b.dataset.wa+'?'))return; await action('winapp-remove',{packageFullName:b.dataset.wa}); load('winapps'); });
    c.querySelectorAll('[data-wr]').forEach(b=>b.onclick=async()=>{ if(!confirm('Reset cache/state for '+b.dataset.wf+'?'))return; await action('winapp-reset',{packageFullName:b.dataset.wr,packageFamilyName:b.dataset.wf}); });
  },

  async tracks(c){
    const rows=await get('/api/tracks');
    c.innerHTML=`<div class="card"><h3>Tracks found</h3>`+table([['Category',r=>esc(r.category)],['Target',r=>esc(r.label)],['Items',r=>String(r.items||0)],['Size',r=>size(r.bytes)],['',r=>`<label><input type="checkbox" class="tc" value="${esc(r.id)}"> clean</label>`]],rows)+
      `<div class="row" style="margin-top:14px"><button class="btn danger" id="cl">Clean selected</button><span class="mut">Closes browsers first; cleans temp/history/MRU.</span></div></div>`;
    $('#cl').onclick=async()=>{ const ids=[...c.querySelectorAll('.tc:checked')].map(x=>x.value); if(!ids.length)return toast('select at least one');
      if(!confirm('Clean '+ids.length+' track category/categories?'))return; const r=await action('tracks-clean',{ids});
      $('#cl').insertAdjacentHTML('afterend','<pre>'+esc(JSON.stringify(r,null,2))+'</pre>'); };
  },

  async extensions(c){
    const rows=await get('/api/extensions');
    c.innerHTML=`<div class="card"><h3>Browser extensions (${rows.length})</h3>`+table([['Browser',r=>esc(r.browser)],['Name',r=>esc(r.name)],['Version',r=>esc(r.version)],['Status',r=>`<span class="pill">${esc(r.status)}</span>`],['',r=>`<button class="btn ghost" data-e="${esc(r.path)}" data-on="${r.status==='disabled'?'true':'false'}">${r.status==='disabled'?'Enable':'Disable'}</button>`]],rows)+`</div>`;
    c.querySelectorAll('[data-e]').forEach(b=>b.onclick=async()=>{ await action('extension-toggle',{path:b.dataset.e,enabled:b.dataset.on==='true'}); load('extensions'); });
  },

  async junk(c){
    const jd=await get('/api/junk-detailed');
    const files=jd.files||[]; const sum=jd.summary||[];
    const total=sum.reduce((a,r)=>a+(Number(r.Bytes)||0),0);
    c.innerHTML=`<div class="card"><h3>Junk scan — ${size(total)} across ${sum.length} roots (${files.length} files shown)</h3>
      <div class="row"><input id="jdir" placeholder="Optional custom folder to scan" style="flex:1"><label><input type="checkbox" id="jrec"> Include &lt; 24h</label><button class="btn blue" id="jrescan">Rescan</button><button class="btn danger" id="jclean">Clean Checked Files</button></div>
      <div id="jtbl"></div><div id="jo"></div></div>`;
    const draw=(list)=>{
      $('#jtbl').innerHTML=table([['',r=>`<input type="checkbox" class="jc" value="${esc(r.Path)}" checked>`],['File',r=>esc(r.Name)],['Size',r=>`${esc(r.SizeKB)} KB`],['Modified',r=>esc(r.LastModified)],['Path',r=>`<span class="mono wrap">${esc(r.Path)}</span>`]],list);
    };
    draw(files);
    $('#jrescan').onclick=async()=>{ const r=await action('junk-scan-detailed',{customDir:$('#jdir').value,includeRecent:$('#jrec').checked}); draw(r.files||[]); };
    $('#jclean').onclick=async()=>{ const picked=[...c.querySelectorAll('.jc:checked')].map(x=>x.value); if(!picked.length)return; if(!confirm('Permanently delete '+picked.length+' checked junk file(s)?'))return; const r=await action('junk-clean',{files:picked}); $('#jo').innerHTML='<pre>'+esc(JSON.stringify(r,null,2))+'</pre>'; };
  },

  async backup(c){
    const rows=await get('/api/backups');
    c.innerHTML=`<div class="card"><h3>Create & Clean Backups</h3><div class="row"><button class="btn" id="b">Export registry keys</button><button class="btn ghost" id="bold">Purge &gt;30 Days</button><span class="mut">Writes .reg files & folder backups to data/backups</span></div></div>
    <div class="card"><h3>Existing backups (${rows.length})</h3>`+table([['File / Folder',r=>esc(r.file)],['Type',r=>`<span class="pill">${esc(r.kind||'reg')}</span>`],['Size',r=>size(r.bytes)],['Created',r=>esc(r.created)],['',r=>`<button class="btn blue" data-r="${esc(r.file)}">Restore</button> <button class="btn danger" data-bd="${esc(r.file)}">Delete</button>`]],rows)+`<div id="out"></div></div>`;
    $('#b').onclick=async()=>{ const r=await action('backup'); $('#out').innerHTML='<pre>'+esc(JSON.stringify(r,null,2))+'</pre>'; load('backup'); };
    $('#bold').onclick=async()=>{ if(!confirm('Delete backups older than 30 days?'))return; await action('backup-clean-old',{days:30}); load('backup'); };
    c.querySelectorAll('[data-r]').forEach(b=>b.onclick=async()=>{ if(!confirm('Import '+b.dataset.r+' into the registry?'))return; const r=await action('restore',{file:b.dataset.r}); $('#out').innerHTML='<pre>'+esc(JSON.stringify(r,null,2))+'</pre>'; });
    c.querySelectorAll('[data-bd]').forEach(b=>b.onclick=async()=>{ if(!confirm('Delete backup '+b.dataset.bd+'?'))return; await action('backup-delete',{file:b.dataset.bd}); load('backup'); });
  },

  async registry(c){
    const rows=await get('/api/registry/invalid');
    c.innerHTML=`<div class="card"><h3>Stale registry entries (${rows.length})</h3><p class="mut">Detected missing InstallLocation, Startup targets, and App Paths.</p>`+
      table([['',(_,i)=>`<input type="checkbox" class="rc" data-idx="${i}" checked>`],['Issue',r=>esc(r.Issue)],['Name',r=>esc(r.Name)],['Key',r=>`<span class="mono">${esc(r.Key)}</span>`],['Missing path',r=>`<span class="mono wrap">${esc(r.MissingPath)}</span>`]],rows)+
      (rows.length?`<div class="row" style="margin-top:12px"><button class="btn danger" id="rclean">Backup & Remove Checked</button></div>`:'')+`</div>`;
    const btn=$('#rclean');
    if(btn) btn.onclick=async()=>{ const sel=[...c.querySelectorAll('.rc:checked')].map(x=>rows[Number(x.dataset.idx)]); if(!sel.length)return; if(!confirm('Backup and remove '+sel.length+' stale entries?'))return; await action('registry-clean',{items:sel}); load('registry'); };
  },

  async wintools(c){
    const rows=await get('/api/wintools');
    c.innerHTML=`<div class="grid">`+rows.map(t=>`<div class="card"><h3>${esc(t.label)}</h3><p class="mut mono">${esc(t.command)}</p><button class="btn blue" data-w="${esc(t.id)}">Launch</button></div>`).join('')+`</div>`;
    c.querySelectorAll('[data-w]').forEach(b=>b.onclick=async()=>{ await action('wintool',{id:b.dataset.w}); });
  },

  async evidence(c){
    const drv=await get('/api/drives');
    c.innerHTML=`<div class="card"><h3>Secure file/folder batch deletion</h3><div class="row"><input id="f" placeholder="one or more file/folder paths separated by newline or ;" style="flex:1"><select id="ep"><option value="1">1-Pass</option><option value="3" selected>3-Pass</option><option value="7">7-Pass</option></select><button class="btn danger" id="d">Shred Listed</button></div>
    <p class="mut">Overwrite passes + random rename + unlink. On SSD/NTFS with wear leveling, overwrite cannot guarantee physical erasure.</p><div id="o"></div></div>
    <div class="card"><h3>Local Drives & Wipe Free Space</h3>${table([['Drive',r=>esc(r.Drive)],['Label',r=>esc(r.Label)],['FS',r=>esc(r.FileSystem)],['Total',r=>r.TotalGB+' GB'],['Free',r=>r.FreeGB+' GB ('+r.FreePercent+'%)'],['',r=>`<button class="btn ghost" data-dp="${esc(r.Path)}">Select</button>`]],drv)}
    <div class="row" style="margin-top:12px"><input id="wf" placeholder="target folder or drive (e.g. D:\\)" style="flex:1"><input id="wmb" type="number" value="16" style="width:90px"><button class="btn blue" id="wb">Wipe MB</button></div><div id="wo"></div></div>`;
    c.querySelectorAll('[data-dp]').forEach(b=>b.onclick=()=>{ $('#wf').value=b.dataset.dp; });
    $('#d').onclick=async()=>{
      const paths=$('#f').value.split(/[\r\n;]+/).map(x=>x.trim()).filter(Boolean);
      if(!paths.length)return;
      if(!confirm('Permanently overwrite and delete '+paths.length+' target(s)?'))return;
      const r=await action('secure-delete-batch',{paths,passes:Number($('#ep').value)||3});
      $('#o').innerHTML='<pre>'+esc(JSON.stringify(r,null,2))+'</pre>';
    };
    $('#wb').onclick=async()=>{ const r=await action('wipe-freespace',{path:$('#wf').value,megabytes:Number($('#wmb').value)||16}); $('#wo').innerHTML='<pre>'+esc(JSON.stringify(r,null,2))+'</pre>'; };
  },

  async history(c){
    const rows=await get('/api/history');
    c.innerHTML=`<div class="card"><div class="row"><h3 style="margin:0;flex:1">Logs & Reference Database</h3>
      <input id="rq" placeholder="Search optional reference DB (e.g. Chrome)" style="width:260px">
      <button class="btn blue" id="rgo">Search Reference DB</button>
      <button class="btn ghost" id="loc">Show Local Log</button>
      <button class="btn danger" id="hc">Clear Local Log</button></div><div id="ht"></div></div>`;
    const drawLocal=(list)=>{ $('#ht').innerHTML=table([['#',r=>r.id],['Time',r=>esc(r.ts)],['Status',r=>`<span class="pill ${r.status==='ok'||r.status==='launched'?'ok':'err'}">${esc(r.status)}</span>`],['Action',r=>esc(r.action)],['Target',r=>esc(r.target)],['Detail',r=>esc(r.detail||'')]],list); };
    drawLocal(rows);
    $('#loc').onclick=async()=>drawLocal(await get('/api/history'));
    $('#rgo').onclick=async()=>{
      const res=await action('ref-logs',{pattern:$('#rq').value,limit:100});
      $('#ht').innerHTML='<p class="mut">Reference DB matches (total '+res.total+'):</p>'+table([['#',r=>r.Number],['Name',r=>esc(r.Name)],['Publisher',r=>esc(r.Publisher)],['Version',r=>esc(r.Version)],['',r=>`<button class="btn ghost" data-rl="${esc(r.Name)}">Leftovers</button>`]],res.items||[]);
      c.querySelectorAll('[data-rl]').forEach(b=>b.onclick=()=>{ state.pendingLeftover=b.dataset.rl; load('leftovers'); });
    };
    $('#hc').onclick=async()=>{ if(!confirm('Clear local operation history?'))return; await action('history-clear'); load('history'); };
  },

  async settings(c){
    const s=await get('/api/settings');
    const ld = s.leftoversDepth===1?'safe':s.leftoversDepth===2?'moderate':s.leftoversDepth===3?'advanced':(s.leftoversDepth||'moderate');
    c.innerHTML=`<div class="card"><h3>Settings</h3>
      <div class="row"><label style="width:220px">Product Name</label><input id="sp" value="${esc(s.productName||'Open Uninstaller')}" style="flex:1"></div>
      <div class="row"><label style="width:220px">Default Leftover Mode</label><select id="sm"><option value="safe"${ld==='safe'?' selected':''}>safe</option><option value="moderate"${ld==='moderate'?' selected':''}>moderate</option><option value="advanced"${ld==='advanced'?' selected':''}>advanced</option></select></div>
      <div class="row"><label style="width:220px">Theme</label><select id="sth"><option value="dark"${s.theme==='dark'?' selected':''}>dark</option><option value="midnight"${s.theme==='midnight'?' selected':''}>midnight</option><option value="light"${s.theme==='light'?' selected':''}>light</option></select></div>
      <div class="row"><label style="width:220px">History Limit</label><input id="sh" type="number" value="${esc(s.historyLimit||200)}" style="width:120px"></div>
      <div class="row"><label><input type="checkbox" id="sc" ${s.confirmBeforeClean!==false?'checked':''}> Confirm before cleaning/deleting</label></div>
      <div class="row"><label><input type="checkbox" id="sb" ${s.backupBeforeUninstall!==false?'checked':''}> Backup Uninstall key before uninstall</label></div>
      <div class="row"><label><input type="checkbox" id="sd" ${s.dryRunDefault?'checked':''}> Dry-run default</label></div>
      <div class="row" style="margin-top:12px"><button class="btn" id="sv">Save Settings</button></div>
      <pre id="sprev">${esc(JSON.stringify(s,null,2))}</pre>
    </div>`;
    $('#sv').onclick=async()=>{
      const updated=await action('settings',{patch:{
        productName:$('#sp').value,
        leftoversDepth:$('#sm').value,
        theme:$('#sth').value,
        historyLimit:Number($('#sh').value)||200,
        confirmBeforeClean:$('#sc').checked,
        backupBeforeUninstall:$('#sb').checked,
        dryRunDefault:$('#sd').checked,
      }});
      $('#sprev').textContent=JSON.stringify(updated,null,2);
    };
  },
};

async function boot(){
  renderNav();
  window.addEventListener('hashchange',()=>load(location.hash.slice(1)||'dashboard'));
  $('#refresh').onclick=()=>load(location.hash.slice(1)||'dashboard');
  try{ const s=await get('/api/status'); $('#status-dot').classList.add('ok'); $('#status-text').textContent=s.product+' v'+s.version+' · online'; }
  catch(e){ $('#status-text').textContent='offline'; }
  load(location.hash.slice(1)||'dashboard');
}
boot();
