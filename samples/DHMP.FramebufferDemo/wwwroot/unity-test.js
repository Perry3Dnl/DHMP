(() => {
  const $ = id => document.getElementById(id);
  const canvas = $('arena');
  const ctx = canvas.getContext('2d');
  const WORLD_W = 40;
  const WORLD_H = 25;
  const SPEED = 5;
  const SEND_INTERVAL_MS = 50;
  const RECEIVE_INTERVAL_MS = 100;
  const STATS_INTERVAL_MS = 1000;
  const keys = new Set();
  const touch = new Set();

  let playerId = Number(localStorage.getItem('dhmpUnityPlayerId')) || null;
  let player = { x: 0, y: 0, z: 0, rotationY: 0 };
  let serverPlayers = [];
  const renderedPlayers = new Map();
  let lastFrame = performance.now();
  let lastSend = 0;
  let sendInFlight = false;
  let receiveInFlight = false;
  let autoMove = true;
  let autoDirection = randomDirection();
  let autoDirectionUntil = performance.now() + randomDirectionDuration();

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
    $('autoMoveButton').disabled = !connected;
    document.querySelectorAll('[data-dir]').forEach(button => button.disabled = !connected);
    updateMovementMode();
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
      autoMove = true;
      chooseNewAutoDirection();
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
    if (receiveInFlight) return;

    receiveInFlight = true;

    try {
      serverPlayers = await request('/api/unity/players');
      const present = new Set();
      const receivedAt = performance.now();

      for (const snapshot of serverPlayers) {
        present.add(snapshot.playerId);

        if (snapshot.playerId === playerId) {
          if (!isMoving() && !autoMove) {
            player.x = snapshot.x;
            player.y = snapshot.y;
            player.z = snapshot.z;
          }
          continue;
        }

        const nativeHistory = Array.isArray(snapshot.nativeHistory)
          ? snapshot.nativeHistory
              .slice()
              .sort((a, b) => a.sequence - b.sequence)
          : [];

        const frames = nativeHistory.length
          ? nativeHistory
          : [snapshot];

        const latest = frames[frames.length - 1];
        const existing = renderedPlayers.get(snapshot.playerId);

        if (!existing) {
          const first = frames[0];

          renderedPlayers.set(snapshot.playerId, {
            playerId: snapshot.playerId,
            x: first.x,
            z: first.z,
            sequence: latest.sequence,
            frames,
            playbackStartedAt: receivedAt
          });

          continue;
        }

        if (latest.sequence > existing.sequence) {
          existing.frames = frames;
          existing.playbackStartedAt = receivedAt;
          existing.sequence = latest.sequence;
        }
      }

      for (const id of renderedPlayers.keys()) {
        if (!present.has(id)) renderedPlayers.delete(id);
      }

      renderPlayersTable();
    } catch {
    } finally {
      receiveInFlight = false;
    }
  }

  async function refreshAll() {
    await Promise.all([stats(), players()]);
  }

  function smoothRemotePlayers(now) {
    for (const remote of renderedPlayers.values()) {
      const frames = remote.frames;

      if (!Array.isArray(frames) || frames.length === 0) continue;

      if (frames.length === 1) {
        remote.x = frames[0].x;
        remote.z = frames[0].z;
        continue;
      }

      const firstTimestamp = frames[0].sentAtUnixMilliseconds;
      const lastTimestamp = frames[frames.length - 1].sentAtUnixMilliseconds;
      const historyDuration = Math.max(
        1,
        lastTimestamp - firstTimestamp);

      // The browser deliberately consumes one Ring-3 window every 100 ms.
      // Stretch the authoritative N-2..N window across that fixed frontend
      // cadence instead of finishing early and freezing until the next poll.
      const playbackAlpha = Math.min(
        1,
        Math.max(
          0,
          (now - remote.playbackStartedAt) /
            RECEIVE_INTERVAL_MS));

      const playbackTimestamp =
        firstTimestamp +
        historyDuration *
        playbackAlpha;

      let left = frames[0];
      let right = frames[frames.length - 1];

      for (let index = 1; index < frames.length; index++) {
        if (playbackTimestamp <= frames[index].sentAtUnixMilliseconds) {
          left = frames[index - 1];
          right = frames[index];
          break;
        }
      }

      const segmentDuration = Math.max(
        1,
        right.sentAtUnixMilliseconds - left.sentAtUnixMilliseconds);

      const alpha = Math.min(
        1,
        Math.max(
          0,
          (playbackTimestamp - left.sentAtUnixMilliseconds) /
            segmentDuration));

      remote.x =
        left.x +
        (right.x - left.x) *
        alpha;

      remote.z =
        left.z +
        (right.z - left.z) *
        alpha;
    }
  }

  function isMoving() {
    return ['w','a','s','d','arrowup','arrowleft','arrowdown','arrowright'].some(k => keys.has(k)) || touch.size > 0;
  }

  function movementVector(now) {
    let x = 0;
    let z = 0;
    if (keys.has('a') || keys.has('arrowleft') || touch.has('left')) x -= 1;
    if (keys.has('d') || keys.has('arrowright') || touch.has('right')) x += 1;
    if (keys.has('w') || keys.has('arrowup') || touch.has('up')) z -= 1;
    if (keys.has('s') || keys.has('arrowdown') || touch.has('down')) z += 1;

    const manualLength = Math.hypot(x, z);
    if (manualLength > 0) {
      return { x: x / manualLength, z: z / manualLength, automatic: false };
    }

    if (!autoMove) return { x: 0, z: 0, automatic: false };

    if (now >= autoDirectionUntil) chooseNewAutoDirection(now);

    return { x: autoDirection.x, z: autoDirection.z, automatic: true };
  }

  function updateMovement(dt, now) {
    if (!playerId) return;
    const move = movementVector(now);
    if (move.x === 0 && move.z === 0) return;

    const halfW = WORLD_W / 2;
    const halfH = WORLD_H / 2;
    let nextX = player.x + move.x * SPEED * dt;
    let nextZ = player.z + move.z * SPEED * dt;

    if (move.automatic) {
      let bounced = false;
      if (nextX <= -halfW || nextX >= halfW) {
        autoDirection.x *= -1;
        bounced = true;
      }
      if (nextZ <= -halfH || nextZ >= halfH) {
        autoDirection.z *= -1;
        bounced = true;
      }
      if (bounced) {
        normalizeAutoDirection();
        autoDirectionUntil = now + randomDirectionDuration();
        nextX = player.x + autoDirection.x * SPEED * dt;
        nextZ = player.z + autoDirection.z * SPEED * dt;
      }
    }

    player.x = clamp(nextX, -halfW, halfW);
    player.z = clamp(nextZ, -halfH, halfH);
    player.rotationY = Math.atan2(move.automatic ? autoDirection.x : move.x, -(move.automatic ? autoDirection.z : move.z));
    $('posX').textContent = player.x.toFixed(2);
    $('posZ').textContent = player.z.toFixed(2);
    updateAutoDirectionLabel();
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

    const playersToDraw = Array.from(renderedPlayers.values());
    if (playerId) {
      playersToDraw.push({ playerId, x: player.x, z: player.z, sequence: 0 });
    }

    for (const p of playersToDraw) {
      const isYou = p.playerId === playerId;
      const px = p.x;
      const pz = p.z;
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
    updateMovement(dt, now);
    smoothRemotePlayers(now);
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

  function randomDirection() {
    const angle = Math.random() * Math.PI * 2;
    return { x: Math.cos(angle), z: Math.sin(angle) };
  }

  function randomDirectionDuration() {
    return 1500 + Math.random() * 2500;
  }

  function normalizeAutoDirection() {
    const length = Math.hypot(autoDirection.x, autoDirection.z) || 1;
    autoDirection.x /= length;
    autoDirection.z /= length;
  }

  function chooseNewAutoDirection(now = performance.now()) {
    autoDirection = randomDirection();
    autoDirectionUntil = now + randomDirectionDuration();
    updateAutoDirectionLabel();
  }

  function updateAutoDirectionLabel() {
    const degrees = ((Math.atan2(autoDirection.x, -autoDirection.z) * 180 / Math.PI) + 360) % 360;
    $('autoDirection').textContent = Math.round(degrees) + '°';
  }

  function updateMovementMode() {
    const manual = isMoving();
    $('movementMode').textContent = manual ? 'Manual override' : (autoMove ? 'Automatic' : 'Paused');
    $('autoMoveButton').textContent = 'Auto movement: ' + (autoMove ? 'ON' : 'OFF');
  }

  window.addEventListener('keydown', event => {
    const key = event.key.toLowerCase();
    if (['w','a','s','d','arrowup','arrowleft','arrowdown','arrowright'].includes(key)) {
      keys.add(key);
      updateMovementMode();
      event.preventDefault();
    }
  });

  window.addEventListener('keyup', event => {
    keys.delete(event.key.toLowerCase());
    updateMovementMode();
  });
  window.addEventListener('blur', () => {
    keys.clear();
    updateMovementMode();
  });

  document.querySelectorAll('[data-dir]').forEach(button => {
    const dir = button.dataset.dir;
    const start = event => {
      if (!button.disabled) {
        touch.add(dir);
        updateMovementMode();
        event.preventDefault();
      }
    };
    const stop = () => {
      touch.delete(dir);
      updateMovementMode();
    };
    button.addEventListener('pointerdown', start);
    button.addEventListener('pointerup', stop);
    button.addEventListener('pointercancel', stop);
    button.addEventListener('pointerleave', stop);
  });

  $('connectButton').addEventListener('click', connect);
  $('disconnectButton').addEventListener('click', disconnect);
  $('autoMoveButton').addEventListener('click', () => {
    autoMove = !autoMove;
    if (autoMove) chooseNewAutoDirection();
    updateMovementMode();
  });

  setConnected(Boolean(playerId));
  updateAutoDirectionLabel();
  refreshAll();
  setInterval(players, RECEIVE_INTERVAL_MS);
  setInterval(stats, STATS_INTERVAL_MS);
  requestAnimationFrame(frame);
})();