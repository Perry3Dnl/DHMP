(() => {
  const $ = id => document.getElementById(id);
  const canvas = $('arena');
  const ctx = canvas.getContext('2d');
  const WORLD_W = 40;
  const WORLD_H = 25;
  const SPEED = 5;
  const SEND_INTERVAL_MS = 50;
  const keys = new Set();
  const touch = new Set();

  let playerId = Number(localStorage.getItem('dhmpUnityPlayerId')) || null;
  let player = { x: 0, y: 0, z: 0, rotationY: 0 };
  let serverPlayers = [];
  let lastFrame = performance.now();
  let lastSend = 0;
  let sendInFlight = false;

  const request = async (url, options = {}) => {
    const response = await fetch(url, {
      cache: 'no-store',
      headers: { 'Content-Type': 'application/json', ...(options.headers || {}) },
      ...options
    });
    if (response.status === 204) return null;
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error || ('HTTP ' + response.status));
    return body;
  };

  function setConnected(connected) {
    $('connectionState').textContent = connected ? 'connected' : 'disconnected';
    $('connectionState').classList.toggle('live', connected);
    $('playerId').textContent = connected ? playerId : '—';
    $('connectButton').disabled = connected;
    $('disconnectButton').disabled = !connected;
    document.querySelectorAll('[data-dir]').forEach(button => button.disabled = !connected);
  }

  function payload() {
    const half = player.rotationY * 0.5;
    return {
      x: player.x,
      y: player.y,
      z: player.z,
      rotationX: 0,
      rotationY: Math.sin(half),
      rotationZ: 0,
      rotationW: Math.cos(half),
      flags: 0
    };
  }

  async function connect() {
    try {
      const result = await request('/api/unity/connect', {
        method: 'POST',
        body: JSON.stringify({ name: 'Web Movement Player' })
      });
      playerId = result.playerId;
      player.x = (Math.random() - 0.5) * 8;
      player.z = (Math.random() - 0.5) * 8;
      localStorage.setItem('dhmpUnityPlayerId', String(playerId));
      setConnected(true);
      await sendState(true);
      await refreshAll();
    } catch {
      clearStaleSession();
    }
  }

  async function disconnect() {
    if (!playerId) return;
    try {
      await request('/api/unity/disconnect/' + playerId, { method: 'POST' });
    } catch {
    } finally {
      playerId = null;
      localStorage.removeItem('dhmpUnityPlayerId');
      setConnected(false);
      await refreshAll();
    }
  }

  async function sendState(force = false) {
    if (!playerId || sendInFlight) return;
    const now = performance.now();
    if (!force && now - lastSend < SEND_INTERVAL_MS) return;

    sendInFlight = true;
    lastSend = now;
    try {
      await request('/api/unity/send/' + playerId, {
        method: 'POST',
        body: JSON.stringify(payload())
      });
    } catch (error) {
      if (/Unknown player/i.test(error.message)) clearStaleSession();
    } finally {
      sendInFlight = false;
    }
  }

  async function stats() {
    try {
      const result = await request('/api/unity/stats');
      $('activeConnections').textContent = result.activeConnections.toLocaleString();
      $('connectedConnections').textContent = result.connectedConnections.toLocaleString();
      $('totalConnections').textContent = result.totalConnections.toLocaleString();
      $('messagesPerSecond').textContent = result.messagesPerSecond.toLocaleString();
      $('bytesPerSecond').textContent = result.bytesPerSecond.toLocaleString();
      $('uptime').textContent = typeof result.uptime === 'string' ? result.uptime.split('.')[0] : String(result.uptime);
    } catch {
    }
  }

  async function players() {
    try {
      serverPlayers = await request('/api/unity/players');
      const mine = serverPlayers.find(p => p.playerId === playerId);
      if (mine && !isMoving()) {
        player.x = mine.x;
        player.y = mine.y;
        player.z = mine.z;
      }
      renderPlayersTable();
    } catch {
    }
  }

  async function refreshAll() {
    await Promise.all([stats(), players()]);
  }

  function isMoving() {
    return ['w','a','s','d','arrowup','arrowleft','arrowdown','arrowright'].some(k => keys.has(k)) || touch.size > 0;
  }

  function movementVector() {
    let x = 0;
    let z = 0;
    if (keys.has('a') || keys.has('arrowleft') || touch.has('left')) x -= 1;
    if (keys.has('d') || keys.has('arrowright') || touch.has('right')) x += 1;
    if (keys.has('w') || keys.has('arrowup') || touch.has('up')) z -= 1;
    if (keys.has('s') || keys.has('arrowdown') || touch.has('down')) z += 1;
    const length = Math.hypot(x, z);
    return length > 0 ? { x: x / length, z: z / length } : { x: 0, z: 0 };
  }

  function updateMovement(dt) {
    if (!playerId) return;
    const move = movementVector();
    if (move.x === 0 && move.z === 0) return;

    player.x = clamp(player.x + move.x * SPEED * dt, -WORLD_W / 2, WORLD_W / 2);
    player.z = clamp(player.z + move.z * SPEED * dt, -WORLD_H / 2, WORLD_H / 2);
    player.rotationY = Math.atan2(move.x, -move.z);
    $('posX').textContent = player.x.toFixed(2);
    $('posZ').textContent = player.z.toFixed(2);
    sendState();
  }

  function drawArena() {
    const width = canvas.width;
    const height = canvas.height;
    ctx.clearRect(0, 0, width, height);
    ctx.fillStyle = '#0b1220';
    ctx.fillRect(0, 0, width, height);

    ctx.strokeStyle = '#18243a';
    ctx.lineWidth = 1;
    for (let x = 0; x <= WORLD_W; x += 2) {
      const sx = x / WORLD_W * width;
      ctx.beginPath(); ctx.moveTo(sx, 0); ctx.lineTo(sx, height); ctx.stroke();
    }
    for (let z = 0; z <= WORLD_H; z += 2) {
      const sy = z / WORLD_H * height;
      ctx.beginPath(); ctx.moveTo(0, sy); ctx.lineTo(width, sy); ctx.stroke();
    }

    ctx.strokeStyle = '#31415e';
    ctx.lineWidth = 2;
    ctx.strokeRect(1, 1, width - 2, height - 2);

    const playersToDraw = serverPlayers.slice();
    if (playerId && !playersToDraw.some(p => p.playerId === playerId)) {
      playersToDraw.push({ playerId, x: player.x, z: player.z, sequence: 0 });
    }

    for (const p of playersToDraw) {
      const isYou = p.playerId === playerId;
      const px = isYou ? player.x : p.x;
      const pz = isYou ? player.z : p.z;
      const sx = ((px + WORLD_W / 2) / WORLD_W) * width;
      const sy = ((pz + WORLD_H / 2) / WORLD_H) * height;

      ctx.beginPath();
      ctx.arc(sx, sy, isYou ? 11 : 9, 0, Math.PI * 2);
      ctx.fillStyle = isYou ? '#71e6a1' : '#7aa2ff';
      ctx.fill();
      ctx.lineWidth = 2;
      ctx.strokeStyle = '#e6edf7';
      ctx.stroke();

      ctx.fillStyle = '#e6edf7';
      ctx.font = '14px system-ui, sans-serif';
      ctx.textAlign = 'center';
      ctx.fillText(isYou ? 'YOU #' + p.playerId : '#' + p.playerId, sx, sy - 16);
    }

    if (!playerId) {
      ctx.fillStyle = '#8fa3bd';
      ctx.font = '20px system-ui, sans-serif';
      ctx.textAlign = 'center';
      ctx.fillText('Connect a player to start moving', width / 2, height / 2);
    }
  }

  function renderPlayersTable() {
    const body = $('playersBody');
    if (!serverPlayers.length) {
      body.innerHTML = '<tr><td colspan="5">No players connected.</td></tr>';
      return;
    }
    body.innerHTML = serverPlayers.map(p => {
      const age = Math.max(0, Date.now() - new Date(p.lastMessageUtc).getTime());
      return '<tr>' +
        '<td>' + p.playerId + (p.playerId === playerId ? ' <span class="good">(you)</span>' : '') + '</td>' +
        '<td>' + p.sequence.toLocaleString() + '</td>' +
        '<td>' + p.x.toFixed(2) + '</td>' +
        '<td>' + p.z.toFixed(2) + '</td>' +
        '<td>' + age + ' ms ago</td>' +
        '</tr>';
    }).join('');
  }

  function frame(now) {
    const dt = Math.min(0.05, Math.max(0, (now - lastFrame) / 1000));
    lastFrame = now;
    updateMovement(dt);
    drawArena();
    requestAnimationFrame(frame);
  }

  function clearStaleSession() {
    playerId = null;
    localStorage.removeItem('dhmpUnityPlayerId');
    setConnected(false);
  }

  function clamp(value, min, max) {
    return Math.max(min, Math.min(max, value));
  }

  window.addEventListener('keydown', event => {
    const key = event.key.toLowerCase();
    if (['w','a','s','d','arrowup','arrowleft','arrowdown','arrowright'].includes(key)) {
      keys.add(key);
      event.preventDefault();
    }
  });

  window.addEventListener('keyup', event => keys.delete(event.key.toLowerCase()));
  window.addEventListener('blur', () => keys.clear());

  document.querySelectorAll('[data-dir]').forEach(button => {
    const dir = button.dataset.dir;
    const start = event => { if (!button.disabled) { touch.add(dir); event.preventDefault(); } };
    const stop = () => touch.delete(dir);
    button.addEventListener('pointerdown', start);
    button.addEventListener('pointerup', stop);
    button.addEventListener('pointercancel', stop);
    button.addEventListener('pointerleave', stop);
  });

  $('connectButton').addEventListener('click', connect);
  $('disconnectButton').addEventListener('click', disconnect);

  setConnected(Boolean(playerId));
  refreshAll();
  setInterval(refreshAll, 250);
  requestAnimationFrame(frame);
})();