(() => {
  const $ = id => document.getElementById(id);
  let playerId = Number(localStorage.getItem('dhmpUnityPlayerId')) || null;

  const log = (label, value) => {
    const time = new Date().toLocaleTimeString();
    const text = typeof value === 'string' ? value : JSON.stringify(value, null, 2);
    $('endpointLog').textContent = '[' + time + '] ' + label + '\n' + text + '\n\n' + $('endpointLog').textContent;
  };

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
    $('sendButton').disabled = !connected;
    $('moveButton').disabled = !connected;
    $('receiveButton').disabled = !connected;
  }

  function statePayload() {
    return {
      x: Number($('x').value),
      y: Number($('y').value),
      z: Number($('z').value),
      rotationX: Number($('rx').value),
      rotationY: Number($('ry').value),
      rotationZ: Number($('rz').value),
      rotationW: Number($('rw').value),
      flags: Number($('flags').value) | 0
    };
  }

  async function connect() {
    try {
      const result = await request('/api/unity/connect', {
        method: 'POST',
        body: JSON.stringify({ name: $('playerName').value })
      });
      playerId = result.playerId;
      localStorage.setItem('dhmpUnityPlayerId', String(playerId));
      setConnected(true);
      log('POST /api/unity/connect', result);
      await refreshAll();
    } catch (error) {
      log('Connect failed', error.message);
    }
  }

  async function disconnect() {
    if (!playerId) return;
    try {
      await request('/api/unity/disconnect/' + playerId, { method: 'POST' });
      log('POST /api/unity/disconnect/' + playerId, '204 No Content');
    } catch (error) {
      log('Disconnect failed', error.message);
    } finally {
      playerId = null;
      localStorage.removeItem('dhmpUnityPlayerId');
      setConnected(false);
      await refreshAll();
    }
  }

  async function send() {
    if (!playerId) return;
    try {
      const result = await request('/api/unity/send/' + playerId, {
        method: 'POST',
        body: JSON.stringify(statePayload())
      });
      log('POST /api/unity/send/' + playerId, result);
      await refreshAll();
    } catch (error) {
      log('Send failed', error.message);
      if (/Unknown player/i.test(error.message)) clearStaleSession();
    }
  }

  async function receive() {
    if (!playerId) return;
    try {
      const result = await request('/api/unity/receive/' + playerId);
      log('GET /api/unity/receive/' + playerId, result);
      renderPlayers(result.players || []);
    } catch (error) {
      log('Receive failed', error.message);
      if (/Unknown player/i.test(error.message)) clearStaleSession();
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
      $('uptime').textContent = formatDuration(result.uptime);
    } catch (error) {
      log('Stats failed', error.message);
    }
  }

  async function players() {
    try {
      renderPlayers(await request('/api/unity/players'));
    } catch (error) {
      log('Players failed', error.message);
    }
  }

  async function refreshAll() {
    await Promise.all([stats(), players()]);
  }

  function renderPlayers(players) {
    const body = $('playersBody');
    if (!players.length) {
      body.innerHTML = '<tr><td colspan="6">No players connected.</td></tr>';
      return;
    }
    body.innerHTML = players.map(p => {
      const age = Math.max(0, Date.now() - new Date(p.lastMessageUtc).getTime());
      return '<tr>' +
        '<td>' + p.playerId + (p.playerId === playerId ? ' <span class="good">(you)</span>' : '') + '</td>' +
        '<td>' + p.sequence.toLocaleString() + '</td>' +
        '<td>' + p.x.toFixed(2) + '</td>' +
        '<td>' + p.y.toFixed(2) + '</td>' +
        '<td>' + p.z.toFixed(2) + '</td>' +
        '<td>' + age + ' ms ago</td>' +
        '</tr>';
    }).join('');
  }

  function formatDuration(value) {
    if (typeof value === 'string') return value.split('.')[0];
    return String(value);
  }

  function clearStaleSession() {
    playerId = null;
    localStorage.removeItem('dhmpUnityPlayerId');
    setConnected(false);
  }

  $('connectButton').addEventListener('click', connect);
  $('disconnectButton').addEventListener('click', disconnect);
  $('sendButton').addEventListener('click', send);
  $('receiveButton').addEventListener('click', receive);
  $('refreshButton').addEventListener('click', refreshAll);
  $('moveButton').addEventListener('click', async () => {
    $('x').value = String(Number($('x').value) + 1);
    await send();
  });

  setConnected(Boolean(playerId));
  refreshAll();
  if (playerId) receive();
  setInterval(refreshAll, 1000);
})();