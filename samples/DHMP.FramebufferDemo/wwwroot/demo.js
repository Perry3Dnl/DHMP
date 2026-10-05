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
  const dirs = [
    [1, 0],
    [0, 1],
    [-1, 0],
    [0, -1]
  ];

  let previous = new Uint8ClampedArray(W * H * 4);
  let running = true;
  let requestInFlight = false;
  let framesThisSecond = 0;
  let updatesThisSecond = 0;
  let lastStats = performance.now();
  let pipes = [];

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

  function resetScene() {
    sctx.fillStyle = '#000';
    sctx.fillRect(0, 0, W, H);
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
    sctx.lineWidth = Math.max(2, pipe.width * 0.22);
    sctx.beginPath();
    sctx.moveTo(pipe.x - 2, pipe.y - 2);
    sctx.lineTo(x2 - 2, y2 - 2);
    sctx.stroke();
  }

  function growPipe(pipe) {
    const step = 7;
    const [dx, dy] = dirs[pipe.dir];
    let x2 = pipe.x + dx * step;
    let y2 = pipe.y + dy * step;

    if (x2 < 30 || x2 >= W - 30 || y2 < 30 || y2 >= H - 30) {
      pipe.dir = (pipe.dir + (Math.random() < 0.5 ? 1 : 3)) % 4;
      pipe.remaining = 30;
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
    const image = rctx.getImageData(0, 0, W, H);

    for (let off = 0; off + OUTPUT <= bytes.length; off += OUTPUT) {
      const x = view.getUint16(off, false);
      const y = view.getUint16(off + 2, false);
      const p = (y * W + x) * 4;
      image.data[p] = bytes[off + 12];
      image.data[p + 1] = bytes[off + 13];
      image.data[p + 2] = bytes[off + 14];
      image.data[p + 3] = 255;
    }

    rctx.putImageData(image, 0, 0);
  }

  async function sendChangedPixels() {
    if (requestInFlight) return;

    const current = sctx.getImageData(0, 0, W, H).data;
    let changed = 0;

    for (let p = 0; p < W * H; p++) {
      const i = p * 4;
      if (current[i] !== previous[i] ||
          current[i + 1] !== previous[i + 1] ||
          current[i + 2] !== previous[i + 2]) changed++;
    }

    if (changed === 0) return;

    const payload = new ArrayBuffer(changed * INPUT);
    const bytes = new Uint8Array(payload);
    const view = new DataView(payload);
    let off = 0;

    for (let p = 0; p < W * H; p++) {
      const i = p * 4;
      if (current[i] === previous[i] &&
          current[i + 1] === previous[i + 1] &&
          current[i + 2] === previous[i + 2]) continue;

      const x = p % W;
      const y = Math.floor(p / W);
      view.setUint16(off, x, false);
      view.setUint16(off + 2, y, false);
      bytes[off + 4] = current[i];
      bytes[off + 5] = current[i + 1];
      bytes[off + 6] = current[i + 2];
      off += INPUT;
    }

    previous.set(current);
    updatesThisSecond += changed;
    requestInFlight = true;

    try {
      const response = await fetch('/api/updates', {
        method: 'POST',
        headers: { 'Content-Type': 'application/octet-stream' },
        body: payload
      });

      if (!response.ok) throw new Error('HTTP ' + response.status);
      applyReceivedPixels(await response.arrayBuffer());
      stateEl.textContent = 'DHMP demo live';
      stateEl.classList.add('live');
    } catch {
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
    resetScene();
  });

  sctx.fillStyle = '#000';
  sctx.fillRect(0, 0, W, H);
  rctx.fillStyle = '#000';
  rctx.fillRect(0, 0, W, H);
  previous.fill(255);
  resetScene();
  requestAnimationFrame(animate);
})();
