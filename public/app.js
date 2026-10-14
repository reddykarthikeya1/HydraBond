// HydraBond Frontend Dashboard Controller
// Engineered by Karthikeya Reddy

const poolingStatusPill = document.getElementById('pooling-status-pill');
const poolingStatusText = document.getElementById('pooling-status-text');
const systemProxyToggle = document.getElementById('system-proxy-toggle');
const adapterCountBadge = document.getElementById('adapter-count-badge');
const devicesList = document.getElementById('devices-list');
const totalSpeedVal = document.getElementById('total-speed-val');
const totalMbsVal = document.getElementById('total-mbs-val');
const meterFill = document.getElementById('meter-fill');
const multiplierMetric = document.getElementById('multiplier-metric');
const sessionDataVal = document.getElementById('session-data-val');
const feedList = document.getElementById('feed-list');
const activeSocketsTag = document.getElementById('active-sockets-tag');

const btnLaunchHydra = document.getElementById('btn-launch-hydra');
const btnRunBenchmark = document.getElementById('btn-run-benchmark');
const btnOpenSpeedtest = document.getElementById('btn-open-speedtest');

let maxObservedSpeed = 100; // dynamic scale

// Update Dashboard UI with real-time stats
function renderStats(data) {
  // 1. Status Pill
  if (data.poolingActive) {
    poolingStatusPill.className = 'status-pill active';
    poolingStatusText.textContent = `● POOLING ACTIVE (${data.activeAdapterCount} ADAPTERS)`;
  } else {
    poolingStatusPill.className = 'status-pill waiting';
    poolingStatusText.textContent = `WAITING FOR 2ND ADAPTER (CONNECT PHONE)`;
  }

  // 2. System Proxy Toggle State
  systemProxyToggle.checked = !!data.systemProxyEnabled;

  // 3. Adapter Count
  adapterCountBadge.textContent = `${data.activeAdapterCount} Active`;

  // 4. Render Adapter Cards
  if (!data.devices || data.devices.length === 0) {
    devicesList.innerHTML = '<div class="feed-empty">No active internet adapters detected.</div>';
  } else {
    devicesList.innerHTML = data.devices.map(dev => `
      <div class="device-card ${dev.isPrimary ? 'primary' : 'secondary'}">
        <div class="device-top">
          <div class="device-info">
            <div class="device-icon-box">
              ${getDeviceSvg(dev.icon)}
            </div>
            <div>
              <div class="device-name-text">${escapeHtml(dev.name)}</div>
              <div class="device-ip-text">${escapeHtml(dev.ip)}</div>
            </div>
          </div>
          <span class="device-role-pill">${dev.isPrimary ? 'Primary' : 'Bonded'}</span>
        </div>
        <div class="device-stats">
          <div>
            <span class="device-speed-num">${dev.speedMbps.toFixed(1)}</span>
            <span class="device-speed-unit">Mbps</span>
          </div>
          <span class="device-data-label">${formatBytes(dev.totalBytes)}</span>
        </div>
      </div>
    `).join('');
  }

  // 5. Total Pool Speed Readout
  const totalMbps = data.totalSpeedMbps || 0;
  totalSpeedVal.textContent = totalMbps.toFixed(1);
  totalMbsVal.textContent = `${data.totalMBs.toFixed(1)} MB/s`;

  // Adjust Radial Meter
  if (totalMbps > maxObservedSpeed) maxObservedSpeed = Math.ceil(totalMbps * 1.2);
  const ratio = Math.min(1, totalMbps / maxObservedSpeed);
  // circumference is 2 * PI * 70 = ~440
  const offset = 440 - (ratio * 440);
  meterFill.style.strokeDashoffset = offset;

  // 6. Multiplier
  if (data.devices.length >= 2) {
    const mult = (1 + (data.devices.length - 1) * 0.95).toFixed(1);
    multiplierMetric.textContent = `~${mult}x`;
  } else {
    multiplierMetric.textContent = `1.0x`;
  }

  // 7. Session Data
  sessionDataVal.textContent = formatBytes(data.totalBytesTransferred || 0);

  // 8. Recent Connection Activity Feed
  if (data.recentConnections && data.recentConnections.length > 0) {
    feedList.innerHTML = data.recentConnections.map(c => `
      <div class="feed-item">
        <span class="feed-time">${c.time}</span>
        <span class="feed-proto">[${c.proto}]</span>
        <span class="feed-target">${escapeHtml(c.host)}:${c.port}</span>
        <span class="feed-via">via ${escapeHtml(c.adapter)}</span>
      </div>
    `).join('');

    const totalActiveSockets = data.devices.reduce((acc, d) => acc + (d.activeSockets || 0), 0);
    activeSocketsTag.textContent = `${totalActiveSockets} Active Socket(s)`;
  }
}

// Device Icon SVGs
function getDeviceSvg(type) {
  if (type === 'wifi') {
    return `<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M5 12.55a11 11 0 0 1 14.08 0M1.42 9a16 16 0 0 1 21.16 0M8.53 16.11a6 6 0 0 1 6.95 0M12 20h.01"/></svg>`;
  }
  if (type === 'smartphone') {
    return `<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="5" y="2" width="14" height="20" rx="2" ry="2"/><line x1="12" y1="18" x2="12.01" y2="18"/></svg>`;
  }
  return `<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="2" y="2" width="20" height="8" rx="2"/><rect x="2" y="14" width="20" height="8" rx="2"/><line x1="6" y1="6" x2="6.01" y2="6"/><line x1="6" y1="18" x2="6.01" y2="18"/></svg>`;
}

function formatBytes(bytes) {
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

function escapeHtml(str) {
  if (!str) return '';
  return String(str).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

// Connect to Server-Sent Events (SSE)
function initEventStream() {
  const evtSource = new EventSource('/api/events');
  evtSource.onmessage = e => {
    try {
      const data = JSON.parse(e.data);
      renderStats(data);
    } catch (err) {
      console.error('SSE parse error:', err);
    }
  };

  evtSource.onerror = () => {
    evtSource.close();
    // Fallback to polling
    setInterval(() => {
      fetch('/api/stats')
        .then(r => r.json())
        .then(renderStats)
        .catch(() => {});
    }, 1500);
  };
}
initEventStream();

// Event Handlers
systemProxyToggle.addEventListener('change', () => {
  fetch('/api/proxy/toggle', { method: 'POST' })
    .then(r => r.json())
    .then(res => {
      systemProxyToggle.checked = res.systemProxyEnabled;
    });
});

btnLaunchHydra.addEventListener('click', () => {
  btnLaunchHydra.disabled = true;
  fetch('/api/launch/hydra', { method: 'POST' })
    .finally(() => {
      setTimeout(() => btnLaunchHydra.disabled = false, 2000);
    });
});

btnRunBenchmark.addEventListener('click', () => {
  window.open('https://fast.com', '_blank');
});

btnOpenSpeedtest.addEventListener('click', () => {
  window.open('https://speedtest.net', '_blank');
});

const btnExitApp = document.getElementById('btn-exit-app');
if (btnExitApp) {
  btnExitApp.addEventListener('click', () => {
    if (confirm('Exit HydraBond and restore normal single-network settings?')) {
      document.body.innerHTML = '<div style="display:flex;height:100vh;align-items:center;justify-content:center;font-family:sans-serif;color:#94a3b8;background:#08090e;flex-direction:column;gap:12px;"><h2>HydraBond Stopped</h2><p>Normal network settings restored.</p></div>';
      fetch('/api/exit', { method: 'POST' }).finally(() => {
        setTimeout(() => window.close(), 600);
      });
    }
  });
}
