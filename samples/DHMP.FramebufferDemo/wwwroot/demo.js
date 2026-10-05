(() => {
  const PREVIEW_W = 1280;
  const PREVIEW_H = 720;
  const RECEIVER_PREVIEW_FPS = 10;

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
  const sctx = source.getContext('2d', { alpha: false });
  const rctx = receiver.getContext('2d', { alpha: false });

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

  const palette = ['#56d6ff', '#f4c95d', '#ff6b8a', '#7ce38b', '#b78cff', '#ff955c'];
  const dirs = [[1, 0], [0, 1], [-1, 0], [0, -1]];

  let running = true;
  let framesThisSecond = 0;
  let lastPreviewStats = performance.now();
  let lastReceiverPreview = 0;
  let selectedResolution = RESOLUTIONS[4];
  let workloadTimer = 0;
  let pipes = [];

  let previousServerStats = null;
  const serverHistory = [];

  function makePipe(index) {
    return {
      x: 80 + Math.floor(Math.random() * (PREVIEW_W - 160)),
      y: 80 + Math.floor(Math.random() * (PREVIEW_H - 160)),
      dir: Math.floor(Math.random() * 4),
      remaining: 30 + Math.floor(Math.random() * 90),
      color: palette[index % palette.length],
      width: 18 + (index % 3) * 3,
      step: 7
    };
  }

  function resetPreview() {
    sctx.fillStyle = '#000';
    sctx.fillRect(0, 0, PREVIEW_W, PREVIEW_H);
    rctx.fillStyle = '#000';
    rctx.fillRect(0, 0, PREVIEW_W, PREVIEW_H);
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
  }

  function drawSegment(pipe, x2, y2) {
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
  }

  function growPipe(pipe) {
    const [dx, dy] = dirs[pipe.dir];
    const x2 = pipe.x + dx * pipe.step;
    const y2 = pipe.y + dy * pipe.step;

    if (x2 < 30 ||
        x2 >= PREVIEW_W - 30 ||
        y2 < 30 ||
        y2 >= PREVIEW_H - 30) {
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

  function drawPreview() {
    for (const pipe of pipes) growPipe(pipe);
  }

  async function setServerWorkload(selected) {
    stateEl.textContent = 'setting server workload…';
    stateEl.classList.remove('live');

    try {
      const response = await fetch('/api/workload', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(selected)
      });

      if (!response.ok) throw new Error('HTTP ' + response.status);

      selectedResolution = selected;
      resolutionValue.textContent =
        selected.width + '×' + selected.height + ' · ' + selected.label;
      sourceLabel.textContent =
        selected.label + ' server workload · representative 720p preview';

      previousServerStats = null;
      serverHistory.length = 0;
      renderServerCharts();

      stateEl.textContent = 'server workload live';
      stateEl.classList.add('live');
    } catch {
      stateEl.textContent = 'workload update failed';
      stateEl.classList.remove('live');
    }
  }

  function drawLineChart(canvas, series, labels) {
    const ctx = canvas.getContext('2d');
    const cssWidth = canvas.clientWidth || 560;
    const cssHeight = canvas.clientHeight || 220;
    const dpr = Math.min(window.devicePixelRatio || 1, 2);

    canvas.width = Math.floor(cssWidth * dpr);
    canvas.height = Math.floor(cssHeight * dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

    const width = cssWidth;
    const height = cssHeight;
    const left = 48;
    const right = 12;
    const top = 16;
    const bottom = 26;
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
          (current.uptimeMilliseconds -
            previousServerStats.uptimeMilliseconds) / 1000
        );

        const rx =
          Math.max(
            0,
            current.receivedRecords -
              previousServerStats.receivedRecords
          ) / elapsed;

        const tx =
          Math.max(
            0,
            current.publishedRecords -
              previousServerStats.publishedRecords
          ) / elapsed;

        const byteDelta = Math.max(
          0,
          current.receivedRecordBytes -
            previousServerStats.receivedRecordBytes
        );

        const mbps = byteDelta / elapsed / 1_000_000;
        const target = Math.max(1, current.targetRecordsPerSecond);
        const targetPercent = rx / target * 100;

        serverHistory.push({
          rx,
          tx,
          mbps,
          total: current.receivedRecords
        });

        if (serverHistory.length > 60) serverHistory.shift();

        updatesEl.textContent = Math.round(rx).toLocaleString();
        ratioEl.textContent = targetPercent.toFixed(1) + '%';
        bytesEl.textContent = mbps.toFixed(2);

        serverRxEl.textContent = Math.round(rx).toLocaleString();
        serverTxEl.textContent = Math.round(tx).toLocaleString();
        serverMbpsEl.textContent = mbps.toFixed(2);
        serverTotalEl.textContent =
          Number(current.receivedRecords).toLocaleString();

        resolutionValue.textContent =
          current.width + '×' + current.height + ' · ' + current.label;

        renderServerCharts();
      }

      previousServerStats = current;
      stateEl.textContent = 'server workload live';
      stateEl.classList.add('live');
    } catch {
      stateEl.textContent = 'server telemetry unavailable';
      stateEl.classList.remove('live');
      serverRxEl.textContent = '—';
      serverTxEl.textContent = '—';
      serverMbpsEl.textContent = '—';
    }
  }

  function animate(now) {
    if (running) {
      drawPreview();
      framesThisSecond++;

      if (now - lastReceiverPreview >= 1000 / RECEIVER_PREVIEW_FPS) {
        rctx.drawImage(source, 0, 0);
        lastReceiverPreview = now;
      }
    }

    if (now - lastPreviewStats >= 1000) {
      const seconds = (now - lastPreviewStats) / 1000;
      fpsEl.textContent =
        (framesThisSecond / seconds).toFixed(0);
      framesThisSecond = 0;
      lastPreviewStats = now;
    }

    requestAnimationFrame(animate);
  }

  toggle.addEventListener('click', () => {
    running = !running;
    toggle.textContent =
      running ? 'Pause animation' : 'Resume animation';
  });

  reset.addEventListener('click', resetPreview);

  resolution.addEventListener('input', () => {
    const selected =
      RESOLUTIONS[Number(resolution.value)];

    resolutionValue.textContent =
      selected.width + '×' +
      selected.height + ' · ' +
      selected.label;

    clearTimeout(workloadTimer);

    workloadTimer = setTimeout(
      () => void setServerWorkload(selected),
      180
    );
  });

  window.addEventListener('resize', renderServerCharts);

  source.width = PREVIEW_W;
  source.height = PREVIEW_H;
  receiver.width = PREVIEW_W;
  receiver.height = PREVIEW_H;

  resetPreview();
  void setServerWorkload(selectedResolution);
  void pollServerStats();
  setInterval(pollServerStats, 1000);
  requestAnimationFrame(animate);
})();
