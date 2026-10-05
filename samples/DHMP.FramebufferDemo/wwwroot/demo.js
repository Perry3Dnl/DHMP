(() => {
  const W = 160, H = 90, INPUT = 7, OUTPUT = 16;
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

  let previous = new Uint8ClampedArray(W * H * 4);
  let running = true;
  let requestInFlight = false;
  let framesThisSecond = 0;
  let updatesThisSecond = 0;
  let lastStats = performance.now();

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

  function drawScene(t) {
    sctx.fillStyle = '#071018';
    sctx.fillRect(0, 0, W, H);

    const x = Math.floor((Math.sin(t * 0.0017) * .5 + .5) * (W - 30));
    const y = Math.floor((Math.cos(t * 0.0013) * .5 + .5) * (H - 22));
    sctx.fillStyle = '#55d6be';
    sctx.fillRect(x, y, 30, 22);

    const x2 = Math.floor((Math.cos(t * 0.0011) * .5 + .5) * (W - 16));
    sctx.fillStyle = '#f0b35a';
    sctx.fillRect(x2, 12, 16, 16);

    sctx.fillStyle = '#eef4ff';
    sctx.font = '10px monospace';
    sctx.fillText('DHMP', 6, H - 7);
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
      drawScene(now);
      void sendChangedPixels();
      framesThisSecond++;
    }

    if (now - lastStats >= 1000) {
      const seconds = (now - lastStats) / 1000;
      const fps = framesThisSecond / seconds;
      const ups = updatesThisSecond / seconds;
      fpsEl.textContent = fps.toFixed(0);
      updatesEl.textContent = Math.round(ups).toLocaleString();
      ratioEl.textContent = (ups / (W * H * Math.max(fps, 1)) * 100).toFixed(1) + '%';
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
    rctx.fillStyle = '#000';
    rctx.fillRect(0, 0, W, H);
  });

  sctx.fillStyle = '#000';
  sctx.fillRect(0, 0, W, H);
  rctx.fillStyle = '#000';
  rctx.fillRect(0, 0, W, H);
  previous.fill(255);
  requestAnimationFrame(animate);
})();
