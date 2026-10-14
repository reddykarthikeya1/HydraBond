/**
 * HydraBond engine - multi-network bandwidth pooling
 * Author: Karthikeya Reddy
 *
 *  - SOCKS5 proxy        127.0.0.1:1080
 *  - HTTP/HTTPS proxy    127.0.0.1:8080
 *  - Control/stats API   127.0.0.1:4545  (used by the HydraBond Windows app)
 *
 * Connections are spread across every enabled network adapter (least busy
 * first), with per-adapter health tracking and automatic retry on another
 * adapter when one fails.
 *
 * System proxy safety: the original Windows proxy settings are snapshotted
 * before HydraBond changes them and restored on every exit path - normal
 * exit, signals, crashes, the parent app closing or being force-killed (via a
 * detached guardian process), and as a last resort on the next start.
 *
 * CLI:  node server.js              run the engine
 *       node server.js --restore    restore system proxy settings and exit
 *       node server.js --guard PID  (internal) watchdog for PID
 */
'use strict';

const net = require('net');
const http = require('http');
const fs = require('fs');
const path = require('path');
const os = require('os');
const { execFile, spawn, spawnSync } = require('child_process');

const VERSION = '1.1.0';
const SOCKS_PORT = 1080;
const HTTP_PORT = 8080;
const API_PORT = 4545;
const BIND = '127.0.0.1';
const OUR_PROXY = `${BIND}:${HTTP_PORT}`;

const CONNECT_TIMEOUT_MS = 8000;
const IDLE_TIMEOUT_MS = 5 * 60 * 1000;
const FAIL_THRESHOLD = 4;
const COOLDOWN_MS = 10000;
const HISTORY_LEN = 60;

const DATA_DIR = path.join(process.env.APPDATA || os.tmpdir(), 'HydraBond');
const STATE_FILE = path.join(DATA_DIR, 'proxy-state.json');
const SETTINGS_FILE = path.join(DATA_DIR, 'settings.json');
const GUARD_FILE = path.join(DATA_DIR, 'guardian.json');
const LOG_FILE = path.join(DATA_DIR, 'hydrabond.log');
const INET_KEY = 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings';
const PROXY_VALUES = ['ProxyEnable', 'ProxyServer', 'ProxyOverride', 'AutoConfigURL'];

const PARENT_PID = parseInt(process.env.HYDRABOND_PARENT_PID, 10) || 0;
const argv = process.argv.slice(2);
const startedAt = Date.now();
const noop = () => {};

try { fs.mkdirSync(DATA_DIR, { recursive: true }); } catch {}

// ---------------------------------------------------------------------------
// Logging
// ---------------------------------------------------------------------------
function log(...parts) {
  const line = `[${new Date().toISOString()}] ${parts.join(' ')}`;
  try { console.log(line); } catch {}
  try { fs.appendFileSync(LOG_FILE, line + '\n'); } catch {}
}

function rotateLog() {
  try {
    if (fs.statSync(LOG_FILE).size > 1024 * 1024) fs.renameSync(LOG_FILE, LOG_FILE + '.old');
  } catch {}
}

function pidAlive(pid) {
  try { process.kill(pid, 0); return true; } catch (e) { return e.code === 'EPERM'; }
}

// ---------------------------------------------------------------------------
// Windows system proxy: snapshot / apply / restore
// ---------------------------------------------------------------------------
function reg(args) {
  return spawnSync('reg', args, { encoding: 'utf8', windowsHide: true, timeout: 5000 });
}

function regGet(name) {
  const r = reg(['query', INET_KEY, '/v', name]);
  if (r.status !== 0) return null;
  const m = new RegExp('^\\s+' + name + '\\s+(REG_\\w+)\\s*(.*)$', 'mi').exec(r.stdout || '');
  if (!m) return null;
  const value = m[1] === 'REG_DWORD' ? parseInt(m[2].trim(), 10 /* 0x.. auto-hex */) : m[2].trim();
  return { type: m[1], value: m[1] === 'REG_DWORD' && Number.isNaN(value) ? parseInt(m[2].trim(), 16) : value };
}

function regSet(name, type, value) {
  return reg(['add', INET_KEY, '/v', name, '/t', type, '/d', String(value), '/f']).status === 0;
}

function regDelete(name) {
  return reg(['delete', INET_KEY, '/v', name, '/f']).status === 0;
}

const REFRESH_PS = Buffer.from(
  "$sig='[DllImport(\"wininet.dll\")] public static extern bool InternetSetOption(IntPtr h,int o,IntPtr b,int l);';" +
  '$t=Add-Type -MemberDefinition $sig -Name WI -Namespace HB -PassThru;' +
  '[void]$t::InternetSetOption([IntPtr]::Zero,39,[IntPtr]::Zero,0);' +
  '[void]$t::InternetSetOption([IntPtr]::Zero,37,[IntPtr]::Zero,0)', 'utf16le').toString('base64');

// Tell WinINet/browsers the proxy settings changed so it applies immediately.
function refreshProxySettings(sync) {
  const args = ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', REFRESH_PS];
  if (sync) spawnSync('powershell.exe', args, { windowsHide: true, timeout: 8000 });
  else execFile('powershell.exe', args, { windowsHide: true, timeout: 10000 }, noop);
}

function readState() {
  try { return JSON.parse(fs.readFileSync(STATE_FILE, 'utf8')); } catch { return null; }
}

function writeState(state) {
  fs.writeFileSync(STATE_FILE, JSON.stringify(state, null, 2));
}

function enableSystemProxy() {
  let state = readState();
  if (!state) {
    const original = {};
    for (const n of PROXY_VALUES) original[n] = regGet(n);
    // A leftover HydraBond address with the proxy switched off is not the user's setting.
    const en = original.ProxyEnable;
    if (original.ProxyServer && original.ProxyServer.value === OUR_PROXY && !(en && en.value === 1)) original.ProxyServer = null;
    state = { version: 1, savedAt: new Date().toISOString(), original };
  }
  state.ownerPid = PARENT_PID || process.pid;
  state.enginePid = process.pid;
  writeState(state); // persist BEFORE touching the registry

  const ok = regSet('ProxyServer', 'REG_SZ', OUR_PROXY) &&
    (state.original.ProxyOverride || regSet('ProxyOverride', 'REG_SZ', '<local>')) &&
    regSet('ProxyEnable', 'REG_DWORD', 1);
  refreshProxySettings(false);
  log('System proxy enabled', ok ? '' : '(registry write failed)');
  return ok;
}

// Restores the settings captured before HydraBond changed them. Idempotent and
// safe to call from anywhere (including process 'exit').
function restoreSystemProxy(reason) {
  const state = readState();
  if (!state) return false;
  let ok = true;
  for (const n of PROXY_VALUES) {
    const o = state.original && state.original[n];
    if (o) ok = regSet(n, o.type, o.value) && ok;
    else regDelete(n); // value did not exist originally
  }
  if (!ok) regSet('ProxyEnable', 'REG_DWORD', 0); // at minimum stop pointing at us
  else { try { fs.unlinkSync(STATE_FILE); } catch {} }
  refreshProxySettings(true);
  log('System proxy restored', reason ? `(${reason})` : '', ok ? '' : '- partial, will retry next start');
  return true;
}

// Crash left our proxy enabled but no snapshot to go by (older version, deleted file).
// Removes a leftover HydraBond proxy address (e.g. from an older version) once the proxy is off.
function cleanLeftover() {
  const en = regGet('ProxyEnable');
  const srv = regGet('ProxyServer');
  if (srv && srv.value === OUR_PROXY && !(en && en.value === 1)) {
    regDelete('ProxyServer');
    refreshProxySettings(false);
    log('Removed leftover HydraBond proxy address');
  }
}

function recoverOrphanedProxy() {
  if (readState()) {
    restoreSystemProxy('stale state from previous run');
    return;
  }
  const en = regGet('ProxyEnable');
  const srv = regGet('ProxyServer');
  if (en && en.value === 1 && srv && srv.value === OUR_PROXY) {
    regSet('ProxyEnable', 'REG_DWORD', 0);
    refreshProxySettings(false);
    log('Disabled orphaned HydraBond system proxy');
  } else {
    cleanLeftover();
  }
}

// ---------------------------------------------------------------------------
// Guardian: a detached watchdog that outlives a force-killed app/engine tree.
// ---------------------------------------------------------------------------
function runGuardian(watchPid) {
  try { fs.writeFileSync(GUARD_FILE, JSON.stringify({ pid: process.pid, watch: watchPid })); } catch {}
  setInterval(() => {
    if (pidAlive(watchPid)) return;
    const st = readState();
    if (st && st.ownerPid === watchPid) {
      log('[guardian] owner', watchPid, 'is gone');
      restoreSystemProxy('guardian');
    }
    try {
      const g = JSON.parse(fs.readFileSync(GUARD_FILE, 'utf8'));
      if (g.pid === process.pid) fs.unlinkSync(GUARD_FILE);
    } catch {}
    process.exit(0);
  }, 1000);
}

function ensureGuardian() {
  const watch = PARENT_PID || process.pid;
  try {
    const g = JSON.parse(fs.readFileSync(GUARD_FILE, 'utf8'));
    if (g.watch === watch && pidAlive(g.pid)) return;
  } catch {}
  // Launch through a short-lived cmd so the guardian is NOT a child of this
  // process - otherwise "kill process tree" would take it down too.
  const cmd = `start "" /B "${process.execPath}" "${__filename}" --guard ${watch}`;
  try {
    // (not 'detached': that would make Windows open a visible console for the guardian)
    spawn('cmd.exe', ['/d', '/c', cmd], {
      stdio: 'ignore', windowsHide: true, windowsVerbatimArguments: true
    }).on('error', e => log('Guardian spawn failed:', e.message)).unref();
  } catch (e) { log('Guardian spawn failed:', e.message); }
}

// ---------------------------------------------------------------------------
// Settings
// ---------------------------------------------------------------------------
let settings = { disabled: [] };
try {
  const s = JSON.parse(fs.readFileSync(SETTINGS_FILE, 'utf8'));
  if (Array.isArray(s.disabled)) settings.disabled = s.disabled.filter(x => typeof x === 'string');
} catch {}

function saveSettings() {
  try { fs.writeFileSync(SETTINGS_FILE, JSON.stringify(settings, null, 2)); } catch {}
}

// ---------------------------------------------------------------------------
// Adapters, health and load balancing
// ---------------------------------------------------------------------------
const adapters = new Map();   // ip -> adapter
let activeAdapters = [];      // adapters currently present, in display order
let pickSeq = 0;

const totals = { down: 0, up: 0, peakMbps: 0 };
const totalHistory = [];
let totalSpeedNow = 0;

const recentConnections = [];
let connectionId = 0;

function newAdapter(ip) {
  return {
    ip, name: '', type: '', icon: 'lan', present: true, enabled: true,
    failCount: 0, cooldownUntil: 0, lastError: '', lastPick: 0,
    bytesDown: 0, bytesUp: 0, downSec: 0, upSec: 0,
    downMbps: 0, upMbps: 0, speedMbps: 0, peakMbps: 0,
    activeSockets: 0, totalConns: 0, history: []
  };
}

function scanInterfaces() {
  const found = [];
  for (const [name, addrs] of Object.entries(os.networkInterfaces())) {
    const lower = name.toLowerCase();
    if (/virtual|vbox|vmware|wsl|vethernet|hyper-v|loopback|bluetooth|tailscale|zerotier|npcap|pseudo/.test(lower)) continue;

    for (const addr of addrs) {
      if (addr.family !== 'IPv4' || addr.internal || addr.address.startsWith('169.254.')) continue;
      let type = 'Network Adapter';
      let icon = 'lan';
      if (/wi-?fi|wlan|wireless/.test(lower)) {
        type = 'Wi-Fi'; icon = 'wifi';
      } else if (/rndis|android|iphone|apple mobile|tether/.test(lower) ||
                 addr.address.startsWith('192.168.42.') || addr.address.startsWith('172.20.10.')) {
        type = 'Phone Tether'; icon = 'phone';
      } else if (lower.includes('ethernet') || lower.includes('local area')) {
        // Phone USB tethering commonly hands out 10.x on an "Ethernet N" adapter.
        if (addr.address.startsWith('10.')) { type = 'Phone Tether'; icon = 'phone'; }
        else { type = 'Ethernet'; icon = 'ethernet'; }
      }
      found.push({ name, ip: addr.address, type, icon });
    }
  }
  // Primary (Wi-Fi / plain Ethernet) first; stable otherwise.
  const primary = n => (n.toLowerCase().includes('wi-fi') || n === 'Ethernet') ? 1 : 0;
  found.sort((a, b) => primary(b.name) - primary(a.name));
  return found;
}

function updateInterfaces() {
  const found = scanInterfaces();
  const prevKey = activeAdapters.map(a => `${a.name}:${a.ip}`).join('|');
  const seen = new Set();
  const list = [];
  for (const info of found) {
    let a = adapters.get(info.ip);
    if (!a) {
      a = newAdapter(info.ip);
      a.enabled = !settings.disabled.includes(info.name);
      adapters.set(info.ip, a);
    }
    a.name = info.name; a.type = info.type; a.icon = info.icon; a.present = true;
    seen.add(info.ip);
    list.push(a);
  }
  for (const a of adapters.values()) if (!seen.has(a.ip)) { a.present = false; a.activeSockets = 0; }
  activeAdapters = list;
  if (list.map(a => `${a.name}:${a.ip}`).join('|') !== prevKey) {
    log(`Adapters updated: ${list.map(a => `${a.name} (${a.ip})`).join(', ') || 'none'}`);
    broadcastStats();
  }
}

function isAdapterError(err) {
  return !!err && ['ETIMEDOUT', 'ENETUNREACH', 'EHOSTUNREACH', 'ENETDOWN', 'EADDRNOTAVAIL', 'EINVAL'].includes(err.code);
}

function markFail(a, err) {
  if (!a) return;
  a.lastError = err.code || err.message;
  if (++a.failCount >= FAIL_THRESHOLD) {
    a.cooldownUntil = Date.now() + COOLDOWN_MS;
    a.failCount = 0;
    log(`Adapter "${a.name}" failing (${a.lastError}) - resting for ${COOLDOWN_MS / 1000}s`);
  }
}

function markOk(a) {
  if (!a) return;
  a.failCount = 0;
  a.cooldownUntil = 0;
  a.lastError = '';
}

// Least busy enabled adapter wins; ties go to the one with less current
// traffic, then round-robin. Adapters in cooldown are skipped unless nothing
// else is left.
function pickAdapter(exclude) {
  const now = Date.now();
  const enabled = activeAdapters.filter(a => a.enabled && !(exclude && exclude.has(a)));
  if (!enabled.length) return null;
  let pool = enabled.filter(a => now >= a.cooldownUntil);
  if (!pool.length) pool = enabled;
  pool.sort((x, y) => (x.activeSockets - y.activeSockets) || (x.speedMbps - y.speedMbps) || (x.lastPick - y.lastPick));
  pool[0].lastPick = ++pickSeq;
  return pool[0];
}

function logConnection(proto, host, port, iface) {
  recentConnections.unshift({
    id: ++connectionId, proto, host, port,
    adapter: iface ? iface.name : 'Default route',
    ip: iface ? iface.ip : '',
    time: new Date().toLocaleTimeString()
  });
  if (recentConnections.length > 40) recentConnections.pop();
}

function count(a, dir, n) {
  if (dir === 'down') { totals.down += n; if (a) { a.bytesDown += n; a.downSec += n; } }
  else { totals.up += n; if (a) { a.bytesUp += n; a.upSec += n; } }
}

// Connect to host:port through the best adapter, retrying on others if the
// adapter (not the destination) is at fault.
function connectOutbound(host, port, cb) {
  const tried = new Set();
  const maxAttempts = Math.max(1, Math.min(3, activeAdapters.length));
  let attempts = 0;

  (function next() {
    attempts++;
    const iface = net.isIPv6(host) ? null : pickAdapter(tried);
    if (iface) tried.add(iface);
    const opts = { host, port };
    if (iface) { opts.family = 4; opts.localAddress = iface.ip; }

    const sock = net.connect(opts);
    let done = false;
    const fail = err => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      sock.destroy();
      const adapterFault = iface && isAdapterError(err);
      if (adapterFault) markFail(iface, err);
      if (adapterFault && attempts < maxAttempts) return next();
      cb(err);
    };
    const timer = setTimeout(() => {
      const e = new Error('connect timeout'); e.code = 'ETIMEDOUT'; fail(e);
    }, CONNECT_TIMEOUT_MS);

    sock.once('error', fail);
    sock.once('connect', () => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      sock.removeListener('error', fail);
      sock.on('error', noop);
      markOk(iface);
      cb(null, sock, iface);
    });
  })();
}

// Wire client <-> target together, counting bytes against the adapter.
function relay(client, target, iface, head) {
  if (iface) { iface.activeSockets++; iface.totalConns++; }
  client.setNoDelay(true);
  target.setNoDelay(true);
  target.setKeepAlive(true, 15000);
  client.setTimeout(IDLE_TIMEOUT_MS, () => { client.destroy(); target.destroy(); });
  target.setTimeout(IDLE_TIMEOUT_MS, () => { client.destroy(); target.destroy(); });
  client.on('error', noop);
  target.on('error', () => client.destroy());

  target.on('data', c => count(iface, 'down', c.length));
  client.on('data', c => count(iface, 'up', c.length));

  client.once('close', () => target.destroy());
  target.once('close', () => {
    if (iface && iface.activeSockets > 0) iface.activeSockets--;
    client.end();
    setTimeout(() => client.destroy(), 3000).unref();
  });

  if (head && head.length) { target.write(head); count(iface, 'up', head.length); }
  client.pipe(target);
  target.pipe(client);
}

// ---------------------------------------------------------------------------
// SOCKS5 proxy
// ---------------------------------------------------------------------------
function socksReplyCode(err) {
  switch (err && err.code) {
    case 'ECONNREFUSED': return 0x05;
    case 'ENETUNREACH': case 'ENETDOWN': return 0x03;
    case 'ENOTFOUND': case 'EHOSTUNREACH': case 'ETIMEDOUT': return 0x04;
    default: return 0x01;
  }
}
const socksReply = code => Buffer.from([0x05, code, 0x00, 0x01, 0, 0, 0, 0, 0, 0]);

const socksServer = net.createServer(client => {
  client.on('error', noop);
  client.setNoDelay(true);
  let buf = Buffer.alloc(0);
  let stage = 0;
  const hsTimer = setTimeout(() => client.destroy(), 10000);
  client.once('close', () => clearTimeout(hsTimer));

  const onData = chunk => {
    buf = Buffer.concat([buf, chunk]);
    if (buf.length > 600) return client.destroy();

    if (stage === 0) {
      if (buf.length < 2) return;
      if (buf[0] !== 0x05) return client.destroy();
      const n = buf[1];
      if (buf.length < 2 + n) return;
      const noAuth = buf.subarray(2, 2 + n).includes(0x00);
      buf = buf.subarray(2 + n);
      if (!noAuth) return client.end(Buffer.from([0x05, 0xff]));
      client.write(Buffer.from([0x05, 0x00]));
      stage = 1;
    }

    if (stage === 1) {
      if (buf.length < 4) return;
      if (buf[0] !== 0x05) return client.destroy();
      const cmd = buf[1];
      const atyp = buf[3];
      let host, off;
      if (atyp === 0x01) {
        if (buf.length < 10) return;
        host = `${buf[4]}.${buf[5]}.${buf[6]}.${buf[7]}`; off = 8;
      } else if (atyp === 0x03) {
        if (buf.length < 5) return;
        const len = buf[4];
        if (buf.length < 5 + len + 2) return;
        host = buf.toString('utf8', 5, 5 + len); off = 5 + len;
      } else if (atyp === 0x04) {
        if (buf.length < 22) return;
        const parts = [];
        for (let i = 4; i < 20; i += 2) parts.push(buf.readUInt16BE(i).toString(16));
        host = parts.join(':'); off = 20;
      } else {
        return client.end(socksReply(0x08));
      }
      const port = buf.readUInt16BE(off);
      const rest = buf.subarray(off + 2);

      stage = 2;
      clearTimeout(hsTimer);
      client.pause();
      client.removeListener('data', onData);
      if (cmd !== 0x01) return client.end(socksReply(0x07));

      connectOutbound(host, port, (err, sock, iface) => {
        if (err) {
          logConnection('SOCKS5', host, port, null);
          if (!client.destroyed) client.end(socksReply(socksReplyCode(err)));
          return;
        }
        if (client.destroyed) return sock.destroy();
        logConnection('SOCKS5', host, port, iface);
        client.write(socksReply(0x00));
        relay(client, sock, iface, rest);
      });
    }
  };
  client.on('data', onData);
});

// ---------------------------------------------------------------------------
// HTTP / HTTPS CONNECT proxy
// ---------------------------------------------------------------------------
const httpProxyServer = http.createServer((req, res) => {
  let u;
  try { u = new URL(req.url); } catch { res.writeHead(400); return res.end('Bad Request'); }
  if (u.protocol !== 'http:') { res.writeHead(400); return res.end('Bad Request'); }

  const host = u.hostname.replace(/^\[|\]$/g, '');
  const port = parseInt(u.port, 10) || 80;
  const iface = net.isIPv6(host) ? null : pickAdapter();
  logConnection('HTTP', host, port, iface);

  const headers = Object.assign({}, req.headers);
  delete headers['proxy-connection'];
  delete headers['proxy-authorization'];
  headers.connection = 'close';

  const opts = {
    host, port, path: u.pathname + u.search, method: req.method,
    headers, agent: false, timeout: 20000
  };
  if (iface) { opts.family = 4; opts.localAddress = iface.ip; iface.activeSockets++; iface.totalConns++; }

  let released = false;
  const release = () => {
    if (!released && iface) { released = true; if (iface.activeSockets > 0) iface.activeSockets--; }
  };

  const preq = http.request(opts, pres => {
    markOk(iface);
    res.writeHead(pres.statusCode, pres.headers);
    pres.on('data', c => count(iface, 'down', c.length));
    pres.pipe(res);
  });
  preq.on('timeout', () => { const e = new Error('timeout'); e.code = 'ETIMEDOUT'; preq.destroy(e); });
  preq.on('error', err => {
    if (iface && isAdapterError(err)) markFail(iface, err);
    if (!res.headersSent) { res.writeHead(502); res.end('Bad Gateway: ' + (err.code || err.message)); }
    else res.destroy();
  });
  res.on('close', () => { release(); preq.destroy(); });
  req.on('data', c => count(iface, 'up', c.length));
  req.on('error', noop);
  req.pipe(preq);
});

httpProxyServer.on('clientError', (err, sock) => {
  if (sock.writable) sock.end('HTTP/1.1 400 Bad Request\r\n\r\n');
  else sock.destroy();
});

httpProxyServer.on('connect', (req, clientSocket, head) => {
  clientSocket.on('error', noop);
  const m = /^(?:\[([^\]]+)\]|([^:]+)):(\d+)$/.exec(req.url || '');
  if (!m) return clientSocket.end('HTTP/1.1 400 Bad Request\r\n\r\n');
  const host = m[1] || m[2];
  const port = parseInt(m[3], 10);

  connectOutbound(host, port, (err, sock, iface) => {
    if (err) {
      logConnection('HTTPS', host, port, null);
      if (!clientSocket.destroyed) clientSocket.end('HTTP/1.1 502 Bad Gateway\r\n\r\n');
      return;
    }
    if (clientSocket.destroyed) return sock.destroy();
    logConnection('HTTPS', host, port, iface);
    clientSocket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
    relay(clientSocket, sock, iface, head);
  });
});

// ---------------------------------------------------------------------------
// Stats
// ---------------------------------------------------------------------------
let systemProxyEnabled = false;
let socksReady = false;
let httpReady = false;
const portErrors = [];
const round2 = n => Math.round(n * 100) / 100;

setInterval(() => {
  let sum = 0;
  for (const a of adapters.values()) {
    a.downMbps = round2(a.downSec * 8 / 1e6);
    a.upMbps = round2(a.upSec * 8 / 1e6);
    a.speedMbps = round2(a.downMbps + a.upMbps);
    a.downSec = 0; a.upSec = 0;
    if (a.speedMbps > a.peakMbps) a.peakMbps = a.speedMbps;
    a.history.push(a.speedMbps);
    if (a.history.length > HISTORY_LEN) a.history.shift();
    if (a.present) sum += a.speedMbps;
  }
  totalSpeedNow = round2(sum);
  if (totalSpeedNow > totals.peakMbps) totals.peakMbps = totalSpeedNow;
  totalHistory.push(totalSpeedNow);
  if (totalHistory.length > HISTORY_LEN) totalHistory.shift();
  broadcastStats();
}, 1000);

function checkSystemProxy() {
  execFile('reg', ['query', INET_KEY], { windowsHide: true, timeout: 5000 }, (err, stdout) => {
    if (err || !stdout) return;
    const en = /ProxyEnable\s+REG_DWORD\s+(0x[0-9a-f]+)/i.exec(stdout);
    const srv = /ProxyServer\s+REG_SZ\s+(\S+)/i.exec(stdout);
    systemProxyEnabled = !!(en && parseInt(en[1], 16) === 1 && srv && srv[1] === OUR_PROXY);
  });
}

function statusOf(a, now) {
  if (!a.enabled) return 'disabled';
  if (now < a.cooldownUntil) return 'resting';
  return (a.activeSockets > 0 || a.speedMbps > 0) ? 'active' : 'idle';
}

function getStatsPayload() {
  const now = Date.now();
  const total = totalSpeedNow;
  const devices = activeAdapters.map((a, idx) => ({
    name: a.name, ip: a.ip, type: a.type, icon: a.icon,
    isPrimary: idx === 0,
    enabled: a.enabled,
    status: statusOf(a, now),
    lastError: a.lastError,
    speedMbps: a.speedMbps, downMbps: a.downMbps, upMbps: a.upMbps, peakMbps: a.peakMbps,
    share: total > 0 ? round2(a.speedMbps / total) : 0,
    totalBytes: a.bytesDown + a.bytesUp, bytesDown: a.bytesDown, bytesUp: a.bytesUp,
    activeSockets: a.activeSockets, totalConnections: a.totalConns,
    history: a.history.slice(-30)
  }));
  const pooled = devices.filter(d => d.enabled).length;
  return {
    appName: 'HydraBond',
    author: 'Karthikeya Reddy',
    version: VERSION,
    uptimeSec: Math.floor((now - startedAt) / 1000),
    poolingActive: pooled >= 2,
    pooledCount: pooled,
    systemProxyEnabled,
    activeAdapterCount: devices.length,
    totalSpeedMbps: total,
    downMbps: round2(devices.reduce((s, d) => s + d.downMbps, 0)),
    upMbps: round2(devices.reduce((s, d) => s + d.upMbps, 0)),
    totalMBs: round2(total / 8),
    peakMbps: totals.peakMbps,
    totalBytesTransferred: totals.down + totals.up,
    totalBytesDown: totals.down,
    totalBytesUp: totals.up,
    activeSockets: devices.reduce((s, d) => s + d.activeSockets, 0),
    history: totalHistory.slice(-30),
    engine: { socksReady, httpReady, portErrors, pid: process.pid },
    devices,
    recentConnections: recentConnections.slice(0, 25)
  };
}

// ---------------------------------------------------------------------------
// Control API (127.0.0.1:4545)
// ---------------------------------------------------------------------------
const sseClients = new Set();

function broadcastStats() {
  if (!sseClients.size) return;
  const data = `data: ${JSON.stringify(getStatsPayload())}\n\n`;
  for (const c of sseClients) { try { c.write(data); } catch {} }
}

function sendJson(res, code, obj) {
  res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' });
  res.end(JSON.stringify(obj));
}

function readBody(req, cb) {
  const chunks = [];
  let size = 0;
  req.on('data', c => { size += c.length; if (size > 8192) req.destroy(); else chunks.push(c); });
  req.on('end', () => {
    let o = {};
    try { const s = Buffer.concat(chunks).toString('utf8'); if (s) o = JSON.parse(s); } catch {}
    cb(o && typeof o === 'object' ? o : {});
  });
  req.on('error', noop);
}

function setSystemProxy(enable) {
  if (enable) {
    if (!httpReady) return { ok: false, error: `Boost can't start: another program is using port ${HTTP_PORT}. Close it and restart HydraBond.` };
    const ok = enableSystemProxy();
    systemProxyEnabled = ok;
    return { ok, error: ok ? '' : 'Couldn\'t change your Windows internet settings' };
  }
  restoreSystemProxy('user');
  systemProxyEnabled = false;
  return { ok: true, error: '' };
}

const ALLOWED_HOSTS = [`127.0.0.1:${API_PORT}`, `localhost:${API_PORT}`];
const ALLOWED_ORIGINS = ALLOWED_HOSTS.map(h => 'http://' + h);

const mimeTypes = {
  '.html': 'text/html; charset=utf-8', '.css': 'text/css', '.js': 'application/javascript',
  '.png': 'image/png', '.svg': 'image/svg+xml', '.ico': 'image/x-icon'
};
const PUBLIC_DIR = path.join(__dirname, 'public');

const webServer = http.createServer((req, res) => {
  // Block DNS-rebinding and cross-site requests from web pages.
  if (!ALLOWED_HOSTS.includes((req.headers.host || '').toLowerCase())) { res.writeHead(403); return res.end(); }
  const isRead = req.method === 'GET' || req.method === 'HEAD';
  if (!isRead && req.headers.origin && !ALLOWED_ORIGINS.includes(req.headers.origin)) { res.writeHead(403); return res.end(); }

  const url = (req.url || '/').split('?')[0];

  if (url === '/api/stats' && req.method === 'GET') return sendJson(res, 200, getStatsPayload());

  if (url === '/api/events' && req.method === 'GET') {
    res.writeHead(200, { 'Content-Type': 'text/event-stream', 'Connection': 'keep-alive', 'Cache-Control': 'no-cache' });
    res.write(`data: ${JSON.stringify(getStatsPayload())}\n\n`);
    sseClients.add(res);
    req.on('close', () => sseClients.delete(res));
    return;
  }

  if ((url === '/api/proxy' || url === '/api/proxy/toggle') && req.method === 'POST') {
    return readBody(req, body => {
      const want = url === '/api/proxy/toggle' || typeof body.enabled !== 'boolean' ? !systemProxyEnabled : body.enabled;
      const r = setSystemProxy(want);
      broadcastStats();
      sendJson(res, r.ok ? 200 : 500, { success: r.ok, error: r.error, systemProxyEnabled });
    });
  }

  if (url === '/api/adapter' && req.method === 'POST') {
    return readBody(req, body => {
      const a = adapters.get(String(body.ip || ''));
      if (!a) return sendJson(res, 404, { success: false, error: 'Unknown adapter' });
      a.enabled = typeof body.enabled === 'boolean' ? body.enabled : !a.enabled;
      settings.disabled = settings.disabled.filter(n => n !== a.name);
      if (!a.enabled) settings.disabled.push(a.name);
      saveSettings();
      log(`Adapter "${a.name}" ${a.enabled ? 'enabled' : 'disabled'}`);
      broadcastStats();
      sendJson(res, 200, { success: true, enabled: a.enabled });
    });
  }

  if (url === '/api/launch/hydra' && req.method === 'POST') {
    const exe = path.join(process.env.LOCALAPPDATA || '', 'Programs', 'Hydra', 'Hydra.exe');
    if (!fs.existsSync(exe)) return sendJson(res, 404, { success: false, error: 'Hydra.exe not found' });
    try {
      spawn(exe, [`--proxy-server=socks5://${BIND}:${SOCKS_PORT}`, '--proxy-bypass-list=<-loopback>'],
        { detached: true, stdio: 'ignore' }).on('error', e => log('Hydra launch failed:', e.message)).unref();
      return sendJson(res, 200, { success: true });
    } catch (e) { return sendJson(res, 500, { success: false, error: e.message }); }
  }

  if (url === '/api/exit' && req.method === 'POST') {
    sendJson(res, 200, { success: true });
    return setTimeout(() => shutdown('api exit request'), 50);
  }

  // Legacy browser dashboard (optional) - static files with traversal protection.
  if (!isRead) { res.writeHead(404); return res.end(); }
  let rel;
  try { rel = decodeURIComponent(url === '/' ? '/index.html' : url); } catch { res.writeHead(400); return res.end(); }
  const filePath = path.normalize(path.join(PUBLIC_DIR, rel));
  if (!filePath.startsWith(PUBLIC_DIR + path.sep)) { res.writeHead(403); return res.end(); }
  fs.readFile(filePath, (err, content) => {
    if (err) { res.writeHead(404); return res.end('Not Found'); }
    res.writeHead(200, { 'Content-Type': mimeTypes[path.extname(filePath)] || 'application/octet-stream', 'Cache-Control': 'no-cache' });
    res.end(content);
  });
});

// ---------------------------------------------------------------------------
// Lifecycle
// ---------------------------------------------------------------------------
let ownsLifecycle = false; // only the instance that owns the API port may touch the proxy
let shuttingDown = false;

function shutdown(reason, code) {
  if (shuttingDown) return;
  shuttingDown = true;
  log('Shutting down:', reason);
  try { restoreSystemProxy(reason); } catch (e) { log('Restore failed:', e.message); }
  process.exit(code || 0);
}

process.on('exit', () => {
  if (ownsLifecycle) { try { restoreSystemProxy('process exit'); } catch {} }
});
for (const sig of ['SIGINT', 'SIGTERM', 'SIGBREAK', 'SIGHUP']) process.on(sig, () => shutdown(sig));

let errBurst = [];
process.on('uncaughtException', err => {
  log('uncaughtException:', (err && err.stack) || err);
  const now = Date.now();
  errBurst = errBurst.filter(t => now - t < 60000);
  errBurst.push(now);
  if (errBurst.length > 25) shutdown('too many unexpected errors', 1);
});
process.on('unhandledRejection', r => log('unhandledRejection:', (r && r.stack) || r));

function listenProxy(server, port, label, onReady) {
  server.on('error', err => {
    const msg = err.code === 'EADDRINUSE'
      ? `${label} port ${port} is in use by another program`
      : `${label} error: ${err.message}`;
    log(msg);
    if (!portErrors.includes(msg)) portErrors.push(msg);
  });
  server.listen(port, BIND, () => onReady());
}

function main() {
  rotateLog();
  let started = false;

  webServer.on('error', err => {
    if (started) return log('API server error:', err.message);
    if (err.code === 'EADDRINUSE') {
      console.error('HydraBond is already running.');
      process.exit(3); // does not touch the proxy: the other instance owns it
    }
    log('API server failed to start:', err.message);
    process.exit(1);
  });

  webServer.listen(API_PORT, BIND, () => {
    started = true;
    ownsLifecycle = true;
    log(`HydraBond engine ${VERSION} started (pid ${process.pid}, parent ${PARENT_PID || 'none'})`);

    recoverOrphanedProxy();
    listenProxy(socksServer, SOCKS_PORT, 'SOCKS5', () => { socksReady = true; log(`SOCKS5 on ${BIND}:${SOCKS_PORT}`); });
    listenProxy(httpProxyServer, HTTP_PORT, 'HTTP proxy', () => { httpReady = true; log(`HTTP proxy on ${BIND}:${HTTP_PORT}`); });

    updateInterfaces();
    setInterval(updateInterfaces, 1500);
    checkSystemProxy();
    setInterval(checkSystemProxy, 5000);
    ensureGuardian();
    setInterval(ensureGuardian, 30000).unref();

    if (PARENT_PID) {
      setInterval(() => { if (!pidAlive(PARENT_PID)) shutdown('parent app exited'); }, 1000);
    }
  });
}

if (argv.includes('--restore')) {
  const did = restoreSystemProxy('cli');
  if (!did) cleanLeftover();
  console.log(did ? 'System proxy restored.' : 'Nothing to restore.');
  process.exit(0);
} else if (argv[0] === '--guard') {
  runGuardian(parseInt(argv[1], 10));
} else {
  main();
}
