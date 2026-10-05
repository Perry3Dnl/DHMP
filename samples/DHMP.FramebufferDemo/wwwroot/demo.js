(() => {
  const INPUT = 7, OUTPUT = 16;
  const RESOLUTIONS = [
    { label: '160p', width: 284, height: 160 },
    { label: '240p', width: 426, height: 240 },
    { label: '360p', width: 640, height: 360 },
    { label: '480p', width: 854, height: 480 },
    { label: '720p', width: 1280, height: 720 },
    { label: '1080p', width: 1920, height: 1080 },
    { label: '1440p', width: 2560, height: 1440 },
    { label: '4K', width: 3840, height: 2160 },
    { label: '8K', width: 7680, height: 4320 }
  ];

  const source = document.querySelector('#source');
  const receiver = document.querySelector('#receiver');
  const stateEl = document.querySelector('#state');
  const fpsEl = document.querySelector('#fps');
  const updatesEl = document.querySelector('#updates');
  const ratioEl = document.querySelector('#ratio');
  const bytesEl = document.querySelector('#bytes');
  const toggle = document.querySelector('#toggle');
  const reset = document.querySelector('#reset');
  const resolution = document.querySelector('#resolution');
  const resolutionValue = document.querySelector('#resolutionValue');
  const sourceLabel = document.querySelector('#sourceLabel');

  const serverRxEl = document.querySelector('#serverRx');
  const serverTxEl = document.querySelector('#serverTx');
  const serverMbpsEl = document.querySelector('#serverMbps');
  const serverTotalEl = document.querySelector('#serverTotal');
  const recordsChart = document.querySelector('#recordsChart');
  const throughputChart = document.querySelector('#throughputChart');
  const totalChart = document.querySelector('#totalChart');

  let W = 1280;
  let H = 720;
  let sctx;
  let rctx;
  let dirtyRegions = [];
  let lastSent = new Map();
  let running = true;
  let requestInFlight = false;
  let framesThisSecond = 0;
  let updatesThisSecond = 0;
  let lastStats = performance.now();
  let pipes = [];
  let epoch = 0;

  let previousServerStats = null;
  const serverHistory = [];

  const palette = ['#56d6ff', '#f4c95d', '#ff6b8a', '#7ce38b', '#b78cff', '#ff955c'];
  const dirs = [[1, 0], [0, 1], [-1, 0], [0, -1]];

  function configureCanvases(width, height) {
    W = width;
    H = height;
    source.width = W;
    source.height = H;
    receiver.width = W;
    receiver.height = H;
    sctx = source.getContext('2d', { alpha: false, willReadFrequently: true });
    rctx = receiver.getContext('2d', { alpha: false, willReadFrequently: true });
    sctx.fillStyle = '#000';
    sctx.fillRect(0, 0, W, H);
    rctx.fillStyle = '#000';
    rctx.fillRect(0, 0, W, H);
  }

  function applyResolution(index) {
    const selected = RESOLUTIONS[index];
    epoch++;
    dirtyRegions = [];
    lastSent = new Map();
    configureCanvases(selected.width, selected.height);
    resolutionValue.textContent = selected.width + '×' + selected.height + ' · ' + selected.label;
    sourceLabel.textContent = selected.label + ' persistent pipes scene';
    pipes = Array.from({ length: 6 }, (_, pipeIndex) => makePipe(pipeIndex));
  }

  function markDirty(x0, y0, x1, y1) {
    x0 = Math.max(0, Math.floor(x0));
    y0 = Math.max(0, Math.floor(y0));
    x1 = Math.min(W, Math.ceil(x1));
    y1 = Math.min(H, Math.ceil(y1));
    if (x1 > x0 && y1 > y0) dirtyRegions.push([x0, y0, x1, y1]);
  }

  function makePipe(index) {
    const margin = Math.max(24, Math.round(Math.min(W, H) * 0.08));
    const availableW = Math.max(1, W - margin * 2);
    const availableH = Math.max(1, H - margin * 2);
    const scale = Math.max(0.45, Math.min(3, H / 720));

    return {
      x: margin + Math.floor(Math.random() * availableW),
      y: margin + Math.floor(Math.random() * availableH),
      dir: Math.floor(Math.random() * 4),
      remaining: 30 + Math.floor(Math.random() * 90),
      color: palette[index % palette.length],
      width: Math.max(5, Math.round((18 + (index % 3) * 3) * scale)),
      step: Math.max(2, Math.round(7 * scale))
    };
  }

  function resetScene() {
    epoch++;
    dirtyRegions = [];
    lastSent = new Map();
    sctx.fillStyle = '#000';
    sctx.fillRect(0, 0, W, H);
    rctx.fillStyle = '#000';
    rctx.fillRect(0, 0, W, H);
    pipes = Array.from({ length: 6 }, (_, index) => makePipe(index));
  }

  function chooseTurn(pipe) {
    const turn = Math.random() < 0.5 ? 1 : -1;
    pipe.dir = (pipe.dir + turn + 4) % 4;
    pipe.remaining = 35 + Math.floor(Math.random() * 110);
    drawJoint(pipe.x, pipe.y, pipe.width, pipe.color);
  }

  function drawJoint(x, y, width, color) {
    const radius = width * 0.68;
    const gradient = sctx.createRadialGradient(
      x - radius * 0.35,
      y - radius * 0.35,
      radius * 0.15,
      x,
      y,
      radius
    );
    gradient.addColorStop(0, '#ffffff');
    gradient.addColorStop(0.18, color);
    gradient.addColorStop(1, '#111722');
    sctx.fillStyle = gradient;
    sctx.beginPath();
    sctx.arc(x, y, radius, 0, Math.PI * 2);
    sctx.fill();
    markDirty(x - radius - 2, y - radius - 2, x + radius + 2, y + radius + 2);
  }

  function drawSegment(pipe, x2, y2) {
    const pad = pipe.width + 10;

    sctx.lineCap = 'round';
    sctx.strokeStyle = '#0a0e15';
    sctx.lineWidth = pipe.width + 8;
    sctx.beginPath();
    sctx.moveTo(pipe.x, pipe.y);
    sctx.lineTo(x2, y2);
    sctx.stroke();

    sctx.strokeStyle = pipe.color;
    sctx.lineWidth = pipe.width;
    sctx.beginPath();
    sctx.moveTo(pipe.x, pipe.y);
    sctx.lineTo(x2, y2);
    sctx.stroke();

    sctx.strokeStyle = 'rgba(255,255,255,.55)';
    sctx.lineWidth = Math.max(1, pipe.width * 0.22);
    sctx.beginPath();
    sctx.moveTo(pipe.x - 2, pipe.y - 2);
    sctx.lineTo(x2 - 2, y2 - 2);
    sctx.stroke();

    markDirty(
      Math.min(pipe.x, x2) - pad,
      Math.min(pipe.y, y2) - pad,
      Math.max(pipe.x, x2) + pad,
      Math.max(pipe.y, y2) + pad
    );
  }

  function growPipe(pipe) {
    const [dx, dy] = dirs[pipe.dir];
    const x2 = pipe.x + dx * pipe.step;
    const y2 = pipe.y + dy * pipe.step;
    const edge = Math.max(10, pipe.width * 1.5);

    if (x2 < edge || x2 >= W - edge || y2 < edge || y2 >= H - edge) {
      pipe.dir = (pipe.dir + (Math.random() < 0.5 ? 1 : 3)) % 4;
      pipe.remaining = 30;
      drawJoint(pipe.x, pipe.y, pipe.width, pipe.color);
      return;
    }

    drawSegment(pipe, x2, y2);
    pipe.x = x2;
    pipe.y = y2;
    pipe.remaining--;

    if (pipe.remaining <= 0) chooseTurn(pipe);
  }

  function drawScene() {
    for (const pipe of pipes) growPipe(pipe);
  }

  function packRgb(r, g, b) {
    return (r << 16) | (g << 8) | b;
  }

  function collectChangedPixels(regions) {
    const changed = [];
    const touched = new Set();

    for (const [x0, y0, x1, y1] of regions) {
      const width = x1 - x0;
      const height = y1 - y0;
      const image = sctx.getImageData(x0, y0, width, height).data;

      for (let localY = 0; localY < height; localY++) {
        for (let localX = 0; localX < width; localX++) {
          const x = x0 + localX;
          const y = y0 + localY;
          const pixel = y * W + x;
          if (touched.has(pixel)) continue;
          touched.add(pixel);

          const local = (localY * width + localX) * 4;
          const r = image[local];
          const g = image[local + 1];
          const b = image[local + 2];
          const packed = packRgb(r, g, b);
          const previous = lastSent.get(pixel) || 0;

          if (packed !== previous) {
            changed.push([pixel, x, y, r, g, b, packed]);
          }
        }
      }
    }

    return changed;
  }

  function applyReceivedPixels(buffer, requestEpoch) {
    if (requestEpoch !== epoch) return;

    const bytes = new Uint8Array(buffer);
    const view = new DataView(buffer);

    for (let off = 0; off + OUTPUT <= bytes.length; off += OUTPUT) {
      const x = view.getUint16(off, false);
      const y = view.getUint16(off + 2, false);
      if (x >= W || y >= H) continue;
      const r = bytes[off + 12];
      const g = bytes[off + 13];
      const b = bytes[off + 14];
      rctx.fillStyle = 'rgb(' + r + ',' + g + ',' + b + ')';
      rctx.fillRect(x, y, 1, 1);
    }
  }

  async function sendChangedPixels() {
    if (requestInFlight || dirtyRegions.length === 0) return;

    const requestEpoch = epoch;
    const regions = dirtyRegions;
    dirtyRegions = [];
    const changedPixels = collectChangedPixels(regions);
    if (changedPixels.length === 0) return;

    const payload = new ArrayBuffer(changedPixels.length * INPUT);
    const bytes = new Uint8Array(payload);
    const view = new DataView(payload);

    for (let index = 0; index < changedPixels.length; index++) {
      const [, x, y, r, g, b] = changedPixels[index];
      const off = index * INPUT;
      view.setUint16(off, x, false);
      view.setUint16(off + 2, y, false);
      bytes[off + 4] = r;
      bytes[off + 5] = g;
      bytes[off + 6] = b;
    }

    updatesThisSecond += changedPixels.length;
    requestInFlight = true;

    try {
      const response = await fetch('/api/updates', {
        method: 'POST',
        headers: { 'Content-Type': 'application/octet-stream' },
        body: payload
      });

      if (!response.ok) throw new Error('HTTP ' + response.status);
      const responseBuffer = await response.arrayBuffer();

      if (requestEpoch === epoch) {
        for (const [pixel, , , , , , packed] of changedPixels) {
          if (packed === 0) lastSent.delete(pixel);
          else lastSent.set(pixel, packed);
        }
        applyReceivedPixels(responseBuffer, requestEpoch);
      }

      stateEl.textContent = 'DHMP demo live';
      stateEl.classList.add('live');
    } catch {
      if (requestEpoch === epoch) {
        for (const [, x, y] of changedPixels) markDirty(x, y, x + 1, y + 1);
      }
      stateEl.textContent = 'demo request failed';
      stateEl.classList.remove('live');
    } finally {
      requestInFlight = false;
    }
  }

  function drawLineChart(canvas, series, labels) {
    const ctx = canvas.getContext('2d');
    const cssWidth = canvas.clientWidth || 560;
    const cssHeight = canvas.clientHeight || 220;
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    canvas.width = Math.floor(cssWidth * dpr);
    canvas.height = Math.floor(cssHeight * dpr);
    ctx.scale(dpr, dpr);

    const width = cssWidth;
    const height = cssHeight;
    const left = 48, right = 12, top = 16, bottom = 26;
    const plotW = width - left - right;
    const plotH = height - top - bottom;

    ctx.clearRect(0, 0, width, height);
    ctx.fillStyle = '#090c11';
    ctx.fillRect(0, 0, width, height);

    let max = 1;
    for (const values of series) {
      for (const value of values) max = Math.max(max, value);
    }
    const roundedMax = max < 10 ? 10 : max;

    ctx.strokeStyle = '#202833';
    ctx.lineWidth = 1;
    ctx.fillStyle = '#758091';
    ctx.font = '11px system-ui';

    for (let i = 0; i <= 4; i++) {
      const y = top + plotH * i / 4;
      ctx.beginPath();
      ctx.moveTo(left, y);
      ctx.lineTo(width - right, y);
      ctx.stroke();
      const value = roundedMax * (1 - i / 4);
      ctx.fillText(formatCompact(value), 4, y + 4);
    }

    const colors = ['#69b7ff', '#7ce38b', '#f4c95d'];
    series.forEach((values, seriesIndex) => {
      if (values.length < 2) return;
      ctx.strokeStyle = colors[seriesIndex % colors.length];
      ctx.lineWidth = 2;
      ctx.beginPath();
      values.forEach((value, index) => {
        const x = left + plotW * index / Math.max(1, values.length - 1);
        const y = top + plotH * (1 - value / roundedMax);
        if (index === 0) ctx.moveTo(x, y);
        else ctx.lineTo(x, y);
      });
      ctx.stroke();
    });

    labels.forEach((label, index) => {
      ctx.fillStyle = colors[index % colors.length];
      ctx.fillRect(left + index * 112, height - 15, 10, 3);
      ctx.fillStyle = '#8d98a7';
      ctx.fillText(label, left + 16 + index * 112, height - 10);
    });
  }

  function formatCompact(value) {
    if (value >= 1e9) return (value / 1e9).toFixed(1) + 'B';
    if (value >= 1e6) return (value / 1e6).toFixed(1) + 'M';
    if (value >= 1e3) return (value / 1e3).toFixed(1) + 'K';
    if (value >= 10) return value.toFixed(0);
    return value.toFixed(1);
  }

  function renderServerCharts() {
    drawLineChart(
      recordsChart,
      [
        serverHistory.map(point => point.rx),
        serverHistory.map(point => point.tx)
      ],
      ['received/s', 'published/s']
    );
    drawLineChart(
      throughputChart,
      [serverHistory.map(point => point.mbps)],
      ['record MB/s']
    );
    drawLineChart(
      totalChart,
      [serverHistory.map(point => point.total)],
      ['total records']
    );
  }

  async function pollServerStats() {
    try {
      const response = await fetch('/api/stats', { cache: 'no-store' });
      if (!response.ok) throw new Error('HTTP ' + response.status);
      const current = await response.json();

      if (previousServerStats) {
        const elapsed = Math.max(
          0.001,
          (current.uptimeMilliseconds - previousServerStats.uptimeMilliseconds) / 1000
        );
        const rx = Math.max(0, current.receivedRecords - previousServerStats.receivedRecords) / elapsed;
        const tx = Math.max(0, current.publishedRecords - previousServerStats.publishedRecords) / elapsed;
        const byteDelta = Math.max(
          0,
          current.receivedRecordBytes - previousServerStats.receivedRecordBytes
        );
        const mbps = byteDelta / elapsed / 1_000_000;

        serverHistory.push({
          rx,
          tx,
          mbps,
          total: current.receivedRecords
        });
        if (serverHistory.length > 60) serverHistory.shift();

        serverRxEl.textContent = Math.round(rx).toLocaleString();
        serverTxEl.textContent = Math.round(tx).toLocaleString();
        serverMbpsEl.textContent = mbps.toFixed(2);
        serverTotalEl.textContent = Number(current.receivedRecords).toLocaleString();
        renderServerCharts();
      }

      previousServerStats = current;
    } catch {
      serverRxEl.textContent = '—';
      serverTxEl.textContent = '—';
      serverMbpsEl.textContent = '—';
    }
  }

  function animate(now) {
    if (running) {
      drawScene();
      void sendChangedPixels();
      framesThisSecond++;
    }

    if (now - lastStats >= 1000) {
      const seconds = (now - lastStats) / 1000;
      const fps = framesThisSecond / seconds;
      const ups = updatesThisSecond / seconds;
      fpsEl.textContent = fps.toFixed(0);
      updatesEl.textContent = Math.round(ups).toLocaleString();
      ratioEl.textContent = (ups / (W * H * Math.max(fps, 1)) * 100).toFixed(3) + '%';
      bytesEl.textContent = (ups * OUTPUT / 1000).toFixed(1);
      framesThisSecond = 0;
      updatesThisSecond = 0;
      lastStats = now;
    }

    requestAnimationFrame(animate);
  }

  toggle.addEventListener('click', () => {
    running = !running;
    toggle.textContent = running ? 'Pause animation' : 'Resume animation';
  });

  reset.addEventListener('click', resetScene);

  resolution.addEventListener('input', () => {
    applyResolution(Number(resolution.value));
  });

  window.addEventListener('resize', renderServerCharts);

  applyResolution(Number(resolution.value));
  void pollServerStats();
  setInterval(pollServerStats, 1000);
  requestAnimationFrame(animate);
})();
