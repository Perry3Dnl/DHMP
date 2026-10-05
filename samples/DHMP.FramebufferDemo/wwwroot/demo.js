(() => {
  const W = 1280, H = 720, INPUT = 7, OUTPUT = 16;
  const source = document.querySelector('#source');
  const receiver = document.querySelector('#receiver');
  const sctx = source.getContext('2d', { alpha: false, willReadFrequently: true });
  const rctx = receiver.getContext('2d', { alpha: false, willReadFrequently: true });
  const stateEl = document.querySelector('#state');
  const fpsEl = document.querySelector('#fps');
  const updatesEl = document.querySelector('#updates');
  const ratioEl = document.querySelector('#ratio');
  const bytesEl = document.querySelector('#bytes');
  const toggle = document.querySelector('#toggle');
  const reset = document.querySelector('#reset');

  const palette = ['#56d6ff', '#f4c95d', '#ff6b8a', '#7ce38b', '#b78cff', '#ff955c'];
  const dirs = [[1, 0], [0, 1], [-1, 0], [0, -1]];

  let previous = new Uint8ClampedArray(W * H * 4);
  let touchedMask = new Uint8Array(W * H);
  let dirtyRegions = [];
  let running = true;
  let requestInFlight = false;
  let framesThisSecond = 0;
  let updatesThisSecond = 0;
  let lastStats = performance.now();
  let pipes = [];

  function markDirty(x0, y0, x1, y1) {
    x0 = Math.max(0, Math.floor(x0));
    y0 = Math.max(0, Math.floor(y0));
    x1 = Math.min(W, Math.ceil(x1));
    y1 = Math.min(H, Math.ceil(y1));
    if (x1 > x0 && y1 > y0) dirtyRegions.push([x0, y0, x1, y1]);
  }

  function makePipe(index) {
    const margin = 80;
    return {
      x: margin + Math.floor(Math.random() * (W - margin * 2)),
      y: margin + Math.floor(Math.random() * (H - margin * 2)),
      dir: Math.floor(Math.random() * 4),
      remaining: 30 + Math.floor(Math.random() * 90),
      color: palette[index % palette.length],
      width: 18 + (index % 3) * 3
    };
  }

  function resetScene(sendClear = true) {
    sctx.fillStyle = '#000';
    sctx.fillRect(0, 0, W, H);
    if (sendClear) markDirty(0, 0, W, H);
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
    sctx.lineWidth = Math.max(2, pipe.width * 0.22);
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
    const step = 7;
    const [dx, dy] = dirs[pipe.dir];
    const x2 = pipe.x + dx * step;
    const y2 = pipe.y + dy * step;

    if (x2 < 30 || x2 >= W - 30 || y2 < 30 || y2 >= H - 30) {
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

  function applyReceivedPixels(buffer) {
    const bytes = new Uint8Array(buffer);
    const view = new DataView(buffer);

    for (let off = 0; off + OUTPUT <= bytes.length; off += OUTPUT) {
      const x = view.getUint16(off, false);
      const y = view.getUint16(off + 2, false);
      const r = bytes[off + 12];
      const g = bytes[off + 13];
      const b = bytes[off + 14];
      rctx.fillStyle = 'rgb(' + r + ',' + g + ',' + b + ')';
      rctx.fillRect(x, y, 1, 1);
    }
  }

  function collectChangedPixels(regions) {
    const changed = [];
    const touched = [];

    for (const [x0, y0, x1, y1] of regions) {
      const width = x1 - x0;
      const height = y1 - y0;
      const image = sctx.getImageData(x0, y0, width, height).data;

      for (let localY = 0; localY < height; localY++) {
        for (let localX = 0; localX < width; localX++) {
          const x = x0 + localX;
          const y = y0 + localY;
          const pixel = y * W + x;

          if (touchedMask[pixel]) continue;
          touchedMask[pixel] = 1;
          touched.push(pixel);

          const local = (localY * width + localX) * 4;
          const global = pixel * 4;
          const r = image[local];
          const g = image[local + 1];
          const b = image[local + 2];

          if (r !== previous[global] ||
              g !== previous[global + 1] ||
              b !== previous[global + 2]) {
            changed.push([pixel, x, y, r, g, b]);
          }
        }
      }
    }

    for (const pixel of touched) touchedMask[pixel] = 0;
    return changed;
  }

  async function sendChangedPixels() {
    if (requestInFlight || dirtyRegions.length === 0) return;

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

      for (const [pixel, , , r, g, b] of changedPixels) {
        const global = pixel * 4;
        previous[global] = r;
        previous[global + 1] = g;
        previous[global + 2] = b;
      }

      applyReceivedPixels(await response.arrayBuffer());
      stateEl.textContent = 'DHMP demo live';
      stateEl.classList.add('live');
    } catch {
      for (const [, x, y] of changedPixels) markDirty(x, y, x + 1, y + 1);
      stateEl.textContent = 'demo request failed';
      stateEl.classList.remove('live');
    } finally {
      requestInFlight = false;
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

  reset.addEventListener('click', () => {
    resetScene(true);
  });

  sctx.fillStyle = '#000';
  sctx.fillRect(0, 0, W, H);
  rctx.fillStyle = '#000';
  rctx.fillRect(0, 0, W, H);
  resetScene(false);
  requestAnimationFrame(animate);
})();
