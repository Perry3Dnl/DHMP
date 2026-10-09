(() => {
  const $ = id => document.getElementById(id);
  const escapeHtml = value => String(value).replace(/[&<>"']/g, c => ({'&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;'}[c]));
  let latestReport = null;
  let timer = 0;

  function fullInteger(value) {
    return Math.round(Number(value || 0)).toLocaleString('en-US', {
      maximumFractionDigits: 0
    });
  }

  function fullDecimal(value, digits = 2) {
    return Number(value || 0).toLocaleString('en-US', {
      minimumFractionDigits: digits,
      maximumFractionDigits: digits
    });
  }


  const chartPalette = ['#69b7ff', '#71e6a1', '#f5c66f', '#d79cff', '#ff8c8c'];

  function prepareCanvas(canvas) {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const width = Math.max(320, canvas.clientWidth || 640);
    const height = Math.max(240, canvas.clientHeight || 320);
    canvas.width = Math.round(width * dpr);
    canvas.height = Math.round(height * dpr);
    const ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    return { ctx, width, height };
  }

  function drawAxes(ctx, width, height, maxValue, yLabelFormatter) {
    const right = 16, top = 20, bottom = 58;

    ctx.clearRect(0, 0, width, height);
    ctx.fillStyle = '#0b1220';
    ctx.fillRect(0, 0, width, height);
    ctx.font = '11px system-ui, sans-serif';
    ctx.textBaseline = 'middle';

    const axisLabels = [];
    for (let i = 0; i <= 4; i++) {
      axisLabels.push(yLabelFormatter(maxValue * (1 - i / 4)));
    }

    const widestLabel = Math.max(
      ...axisLabels.map(label => ctx.measureText(label).width));

    const left = Math.max(78, Math.ceil(widestLabel) + 18);
    const plotWidth = Math.max(80, width - left - right);
    const plotHeight = height - top - bottom;

    for (let i = 0; i <= 4; i++) {
      const y = top + plotHeight * i / 4;
      const value = maxValue * (1 - i / 4);
      ctx.strokeStyle = '#202b3a';
      ctx.beginPath();
      ctx.moveTo(left, y);
      ctx.lineTo(width - right, y);
      ctx.stroke();
      ctx.fillStyle = '#8fa3bd';
      ctx.textAlign = 'right';
      ctx.fillText(yLabelFormatter(value), left - 8, y);
    }

    return { left, right, top, bottom, plotWidth, plotHeight };
  }

  function drawGroupedBarChart(canvasId, categories, series, valueFormatter = fullInteger) {
    const canvas = $(canvasId);
    if (!canvas || !categories.length || !series.length) return;

    const { ctx, width, height } = prepareCanvas(canvas);
    const dataMax = Math.max(1, ...series.flatMap(s => s.values.map(Number)));
    const maxValue = dataMax * 1.15;
    const a = drawAxes(ctx, width, height, maxValue, valueFormatter);
    const groupWidth = a.plotWidth / categories.length;
    const innerWidth = Math.min(groupWidth * 0.82, 100);
    const barWidth = innerWidth / series.length;

    categories.forEach((category, categoryIndex) => {
      const groupX = a.left + groupWidth * categoryIndex + groupWidth / 2;
      series.forEach((s, seriesIndex) => {
        const value = Number(s.values[categoryIndex] || 0);
        const h = value / maxValue * a.plotHeight;
        const x = groupX - innerWidth / 2 + seriesIndex * barWidth + 1;
        const y = a.top + a.plotHeight - h;
        ctx.fillStyle = chartPalette[seriesIndex % chartPalette.length];
        ctx.fillRect(x, y, Math.max(1, barWidth - 2), h);

        if (series.length === 1) {
          ctx.fillStyle = '#e6edf7';
          ctx.font = '700 11px system-ui, sans-serif';
          ctx.textAlign = 'center';
          ctx.textBaseline = 'bottom';
          ctx.fillText(
            valueFormatter(value),
            x + Math.max(1, barWidth - 2) / 2,
            Math.max(a.top + 12, y - 5));
        }
      });

      ctx.save();
      ctx.translate(groupX, height - a.bottom + 10);
      ctx.rotate(-0.42);
      ctx.fillStyle = '#8fa3bd';
      ctx.textAlign = 'right';
      ctx.textBaseline = 'middle';
      ctx.fillText(category, 0, 0);
      ctx.restore();
    });

    let legendX = a.left;
    const legendY = height - 10;
    series.forEach((s, index) => {
      ctx.fillStyle = chartPalette[index % chartPalette.length];
      ctx.fillRect(legendX, legendY - 4, 10, 3);
      ctx.fillStyle = '#a8b7ca';
      ctx.textAlign = 'left';
      ctx.fillText(s.label, legendX + 15, legendY);
      legendX += ctx.measureText(s.label).width + 42;
    });
  }


  function drawComparisonBarChart(canvasId, rows, valueSelector, valueFormatter = fullInteger) {
    const canvas = $(canvasId);
    if (!canvas || !rows.length) return;

    const { ctx, width, height } = prepareCanvas(canvas);
    const values = rows.map(row => Number(valueSelector(row) || 0));
    const dataMax = Math.max(1, ...values);
    const maxValue = dataMax * 1.15;
    const a = drawAxes(ctx, width, height, maxValue, valueFormatter);
    const groupWidth = a.plotWidth / rows.length;
    const barWidth = Math.min(groupWidth * 0.62, 100);

    rows.forEach((row, index) => {
      const value = values[index];
      const h = value / maxValue * a.plotHeight;
      const centerX = a.left + groupWidth * index + groupWidth / 2;
      const x = centerX - barWidth / 2;
      const y = a.top + a.plotHeight - h;
      const isDhmp = String(row.protocol || '').toUpperCase().includes('DHMP');

      ctx.fillStyle = isDhmp ? '#71e6a1' : '#69b7ff';
      ctx.fillRect(x, y, barWidth, h);

      ctx.fillStyle = isDhmp ? '#71e6a1' : '#e6edf7';
      ctx.font = isDhmp
        ? '800 12px system-ui, sans-serif'
        : '700 11px system-ui, sans-serif';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'bottom';
      ctx.fillText(
        valueFormatter(value),
        centerX,
        Math.max(a.top + 12, y - 6));

      ctx.save();
      ctx.translate(centerX, height - a.bottom + 10);
      ctx.rotate(-0.42);
      ctx.fillStyle = isDhmp ? '#71e6a1' : '#8fa3bd';
      ctx.font = isDhmp
        ? '800 11px system-ui, sans-serif'
        : '11px system-ui, sans-serif';
      ctx.textAlign = 'right';
      ctx.textBaseline = 'middle';
      ctx.fillText(row.protocol, 0, 0);
      ctx.restore();
    });

    ctx.fillStyle = '#69b7ff';
    ctx.fillRect(a.left, height - 13, 10, 3);
    ctx.fillStyle = '#a8b7ca';
    ctx.textAlign = 'left';
    ctx.font = '11px system-ui, sans-serif';
    ctx.fillText('Other measured paths', a.left + 15, height - 11);

    const dhmpLegendX = a.left + 145;
    ctx.fillStyle = '#71e6a1';
    ctx.fillRect(dhmpLegendX, height - 13, 10, 3);
    ctx.fillStyle = '#a8b7ca';
    ctx.fillText('DHMP', dhmpLegendX + 15, height - 11);
  }

  function drawZeroAwareAllocationChart(canvasId, rows) {
    const canvas = $(canvasId);
    if (!canvas || !rows.length) return;

    const values = rows.map(row => Number(row.fullPathBytesPerCall || 0));
    const allZero = values.every(value => Math.abs(value) < 1e-12);

    if (!allZero) {
      drawGroupedBarChart(
        canvasId,
        rows.map(row => row.receiveMode + (row.nativeSmoothing ? ' + Native Smoothing' : '')),
        [{
          label: 'Full path B/call',
          values
        }],
        value => fullDecimal(value, 3));
      return;
    }

    const { ctx, width, height } = prepareCanvas(canvas);
    const left = 72;
    const right = 24;
    const top = 54;
    const bottom = 72;
    const plotWidth = width - left - right;
    const baselineY = height - bottom;

    ctx.clearRect(0, 0, width, height);
    ctx.fillStyle = '#0b1220';
    ctx.fillRect(0, 0, width, height);

    ctx.fillStyle = '#71e6a1';
    ctx.font = '700 18px system-ui, sans-serif';
    ctx.textAlign = 'center';
    ctx.fillText('ZERO STEADY-STATE ALLOCATION', width / 2, 24);

    ctx.fillStyle = '#8fa3bd';
    ctx.font = '12px system-ui, sans-serif';
    ctx.fillText('All measured full-path receive modes allocated 0.000 B/call', width / 2, 44);

    ctx.strokeStyle = '#263244';
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(left, baselineY);
    ctx.lineTo(width - right, baselineY);
    ctx.stroke();

    rows.forEach((row, index) => {
      const x = rows.length === 1
        ? left + plotWidth / 2
        : left + plotWidth * index / (rows.length - 1);

      ctx.fillStyle = '#71e6a1';
      ctx.beginPath();
      ctx.arc(x, baselineY, 6, 0, Math.PI * 2);
      ctx.fill();

      ctx.fillStyle = '#e6edf7';
      ctx.font = '700 12px ui-monospace, SFMono-Regular, Menlo, Consolas, monospace';
      ctx.textAlign = 'center';
      ctx.fillText('0.000 B/call', x, baselineY - 22);

      ctx.save();
      ctx.translate(x, baselineY + 18);
      ctx.rotate(-0.32);
      ctx.fillStyle = '#8fa3bd';
      ctx.font = '11px system-ui, sans-serif';
      ctx.textAlign = 'right';
      ctx.fillText(
        row.receiveMode + (row.nativeSmoothing ? ' + Native Smoothing' : ''),
        0,
        0);
      ctx.restore();
    });
  }

  function drawLineChart(canvasId, categories, values, label, valueFormatter = fullInteger) {
    const canvas = $(canvasId);
    if (!canvas || !categories.length) return;

    const { ctx, width, height } = prepareCanvas(canvas);
    const maxValue = Math.max(1, ...values.map(Number));
    const a = drawAxes(ctx, width, height, maxValue, valueFormatter);

    ctx.strokeStyle = chartPalette[0];
    ctx.lineWidth = 2.5;
    ctx.beginPath();

    values.forEach((value, index) => {
      const x = categories.length === 1
        ? a.left + a.plotWidth / 2
        : a.left + a.plotWidth * index / (categories.length - 1);
      const y = a.top + a.plotHeight - Number(value) / maxValue * a.plotHeight;
      if (index === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    });
    ctx.stroke();

    values.forEach((value, index) => {
      const x = categories.length === 1
        ? a.left + a.plotWidth / 2
        : a.left + a.plotWidth * index / (categories.length - 1);
      const y = a.top + a.plotHeight - Number(value) / maxValue * a.plotHeight;
      ctx.fillStyle = chartPalette[0];
      ctx.beginPath();
      ctx.arc(x, y, 3.5, 0, Math.PI * 2);
      ctx.fill();
      ctx.fillStyle = '#8fa3bd';
      ctx.textAlign = 'center';
      ctx.fillText(categories[index], x, height - a.bottom + 18);
    });

    ctx.fillStyle = '#a8b7ca';
    ctx.textAlign = 'left';
    ctx.fillText(label, a.left, height - 10);
  }


  function protocolEnvelope(row) {
    const protocol = String(row.protocol || '');
    const payload = Number(row.packetBytes || 0);

    // Count the protocol item as transmitted by each benchmark path instead
    // of stripping transport/framing bytes away from competitors.
    if (protocol.includes('DHMP + AF_XDP')) {
      return {
        bytes: payload + 14 + 40,
        note: 'Ethernet 14 B + IPv6 40 B + DHMP data. DHMP V1 adds 0 data-plane header bytes.'
      };
    }

    if (protocol.includes('DHMP Raw IPv6')) {
      return {
        bytes: payload + 14 + 40,
        note: 'Ethernet 14 B + IPv6 40 B + DHMP data. DHMP V1 adds 0 data-plane header bytes.'
      };
    }

    if (protocol.startsWith('UDP/')) {
      return {
        bytes: payload + 40 + 8,
        note: 'IPv6 40 B + UDP 8 B + data.'
      };
    }

    if (protocol.startsWith('TCP/')) {
      return {
        bytes: payload + 40 + 20,
        note: 'Minimum IPv6 40 B + TCP 20 B + data; TCP options, ACK traffic and segmentation can add more.'
      };
    }

    if (protocol.startsWith('HTTP/1.1')) {
      const requestLine = 'POST /api/report/http-sink HTTP/1.1\r\n';
      const host = 'Host: 127.0.0.1:8080\r\n';
      const contentLength = 'Content-Length: ' + payload + '\r\n';
      const httpMinimum = requestLine.length + host.length + contentLength.length + 2;

      return {
        bytes: payload + 40 + 20 + httpMinimum,
        note: 'Minimum HTTP request framing + TCP/IPv6 + body; response bytes, TCP options, ACKs and segmentation can add more.'
      };
    }

    return {
      bytes: payload,
      note: 'No additional protocol-byte accounting available.'
    };
  }

  function wholeProtocolGigabytesPerSecond(row) {
    const envelope = protocolEnvelope(row);
    return Number(row.packetRate || 0) * envelope.bytes / 1_000_000_000;
  }

  function renderProtocolComparison(report) {
    const rows = Array.isArray(report.protocolComparisons)
      ? report.protocolComparisons
      : [];

    if (!rows.length) return;

    const ioRows = rows.filter(
      row => !String(row.protocol || '').toLowerCase().includes('processing ceiling'));

    const labels = ioRows.map(row => row.protocol);

    drawComparisonBarChart(
      'protocolPacketRateChart',
      ioRows,
      row => Number(row.packetRate || 0),
      fullInteger);

    drawComparisonBarChart(
      'protocolThroughputChart',
      ioRows,
      row => wholeProtocolGigabytesPerSecond(row),
      value => fullDecimal(value, 3));

    const body = $('protocolComparisonBody');
    if (body) {
      body.innerHTML = rows.map(row => {
        const isCeiling =
          String(row.protocol || '').toLowerCase().includes('processing ceiling');
        const classLabel = isCeiling
          ? '<span class="scope-badge ceiling">Software ceiling</span>'
          : '<span class="scope-badge">Measured I/O</span>';

        return '<tr>' +
          '<td>' + row.protocol + '</td>' +
          '<td style="text-align:left;white-space:normal;min-width:220px">' + row.scope + '</td>' +
          '<td>' + classLabel + '</td>' +
          '<td>' + fullInteger(protocolEnvelope(row).bytes) + ' B</td>' +
          '<td>' + fullInteger(row.packetRate) + '</td>' +
          '<td>' + fullDecimal(wholeProtocolGigabytesPerSecond(row), 3) + '</td>' +
          '<td>' + (row.kernelBypass ? 'Yes — AF_XDP' : 'No') + '</td>' +
          '<td style="text-align:left;white-space:normal;min-width:300px">' +
          row.detail + '<br><span class="muted">' + protocolEnvelope(row).note + '</span></td>' +
          '</tr>';
      }).join('');
    }
  }


  function renderDhmpValueStory(report) {
    const scaling = Array.isArray(report.workerScaling)
      ? report.workerScaling
      : [];

    if (!scaling.length) return;

    drawLineChart(
      'softwareThroughputChart',
      scaling.map(row => String(row.workers)),
      scaling.map(row => Number(row.packetRate || 0)),
      'Workers → canonical one-record packets/s',
      fullInteger);
  }

  function renderCharts(report) {
    const packetSizes = [...new Set(report.pathMatrix.map(r => Number(r.packetBytes)))];
    const modeLabels = ['Sequential', 'UnsafeSequential', 'Latest', 'UnsafeLatest', 'Latest + Native Smoothing'];

    function rowsForMode(label) {
      return report.pathMatrix.filter(row => modeName(row) === label);
    }

    drawGroupedBarChart(
      'packetRateModeChart',
      packetSizes.map(v => fullInteger(v) + ' B'),
      modeLabels.map(label => ({
        label,
        values: packetSizes.map(size => {
          const row = rowsForMode(label).find(r => Number(r.packetBytes) === size);
          return row ? Number(row.packetRate) : 0;
        })
      })),
      fullInteger);

    drawGroupedBarChart(
      'coreTimingModeChart',
      packetSizes.map(v => fullInteger(v) + ' B'),
      modeLabels.map(label => ({
        label,
        values: packetSizes.map(size => {
          const row = rowsForMode(label).find(r => Number(r.packetBytes) === size);
          return row ? Number(row.processorNanoseconds.median) : 0;
        })
      })),
      value => fullDecimal(value, 2));

    drawGroupedBarChart(
      'directReceiveTimingModeChart',
      packetSizes.map(v => fullInteger(v) + ' B'),
      modeLabels.map(label => ({
        label,
        values: packetSizes.map(size => {
          const row = rowsForMode(label).find(r => Number(r.packetBytes) === size);
          return row ? Number(row.serverNanoseconds.median) : 0;
        })
      })),
      value => fullDecimal(value, 2));

    drawGroupedBarChart(
      'prebufferedTimingModeChart',
      packetSizes.map(v => fullInteger(v) + ' B'),
      modeLabels.map(label => ({
        label,
        values: packetSizes.map(size => {
          const row = rowsForMode(label).find(r => Number(r.packetBytes) === size);
          return row ? Number(row.prebufferedServerNanoseconds?.median || 0) : 0;
        })
      })),
      value => fullDecimal(value, 2));

    drawGroupedBarChart(
      'clientTimingModeChart',
      packetSizes.map(v => fullInteger(v) + ' B'),
      modeLabels.map(label => ({
        label,
        values: packetSizes.map(size => {
          const row = rowsForMode(label).find(r => Number(r.packetBytes) === size);
          return row ? Number(row.clientNanoseconds.median) : 0;
        })
      })),
      value => fullDecimal(value, 2));

    const aggregateRows = Array.isArray(report.aggregateTiming)
      ? report.aggregateTiming
      : [];

    const selectedAggregateRows = aggregateRows.filter(row =>
      (row.ratePolicy || 'Unlimited') === $('aggregateRatePolicy').value &&
      (row.confirmationMode || 'None') === $('aggregateConfirmation').value);

    if (aggregateRows.length) {
      drawGroupedBarChart(
        'aggregateDirectTimingChart',
        packetSizes.map(v => fullInteger(v) + ' B'),
        modeLabels.map(label => ({
          label,
          values: packetSizes.map(size => {
            const row = selectedAggregateRows.find(r =>
              modeName(r) === label && Number(r.packetBytes) === size);
            return row ? Number(row.directReceive?.nanosecondsPerPacket || 0) : 0;
          })
        })),
        value => fullDecimal(value, 2));

      drawGroupedBarChart(
        'aggregateClientTimingChart',
        packetSizes.map(v => fullInteger(v) + ' B'),
        modeLabels.map(label => ({
          label,
          values: packetSizes.map(size => {
            const row = selectedAggregateRows.find(r =>
              modeName(r) === label && Number(r.packetBytes) === size);
            return row ? Number(row.fullClient?.nanosecondsPerPacket || 0) : 0;
          })
        })),
        value => fullDecimal(value, 2));
    }

    const localBatches = Array.isArray(report.localBatchMatrix)
      ? report.localBatchMatrix
      : [];

    if (localBatches.length) {
      const batchSizes = [...new Set(localBatches.map(r => Number(r.batchBytes)))];

      drawGroupedBarChart(
        'localBatchRateChart',
        batchSizes.map(v => fullInteger(v) + ' B'),
        modeLabels.map(label => ({
          label,
          values: batchSizes.map(size => {
            const row = localBatches.find(r =>
              modeName(r) === label && Number(r.batchBytes) === size);
            return row ? Number(row.batchRate) : 0;
          })
        })),
        fullInteger);
    }

    drawLineChart(
      'workerScalingChart',
      report.workerScaling.map(r => String(r.workers)),
      report.workerScaling.map(r => Number(r.packetRate)),
      'Workers → packet transactions/s (software path)',
      fullInteger);

    drawGroupedBarChart(
      'ratePolicyChart',
      report.ratePolicies.map(r => r.ratePolicy),
      [{ label: 'Packets/s', values: report.ratePolicies.map(r => Number(r.packetRate)) }],
      fullInteger);

    drawGroupedBarChart(
      'confirmationChart',
      report.confirmationModes.map(r => r.confirmationMode),
      [{ label: 'Transactions/s', values: report.confirmationModes.map(r => Number(r.packetRate)) }],
      fullInteger);

    const pokeRows = Array.isArray(report.pokeBenchmarks)
      ? report.pokeBenchmarks
      : [];

    if (pokeRows.length) {
      drawGroupedBarChart(
        'pokeRateChart',
        pokeRows.map(row => fullInteger(row.packetBytes) + ' B'),
        [{
          label: 'Exact echoes/s',
          values: pokeRows.map(row => Number(row.echoesPerSecond || 0))
        }],
        fullInteger);

      drawGroupedBarChart(
        'pokeLatencyChart',
        pokeRows.map(row => fullInteger(row.packetBytes) + ' B'),
        [{
          label: 'Local ns/echo',
          values: pokeRows.map(row => Number(row.roundTripNanoseconds?.median || 0))
        }],
        value => fullDecimal(value, 2));
    }

    drawZeroAwareAllocationChart(
      'allocationChart',
      report.allocations);

    renderProtocolComparison(report);
    renderDhmpValueStory(report);
  }

  function metric(label, value, unit = '') {
    return '<article class="metric-card"><span>' + label + '</span><strong>' +
      value + '</strong><small>' + unit + '</small></article>';
  }

  function modeName(row) {
    return row.receiveMode + (row.nativeSmoothing ? ' + Native Smoothing' : '');
  }

  function render(report) {
    latestReport = report;
    const generated = report.generatedUtc ? new Date(report.generatedUtc) : null;
    const validDate = generated && !Number.isNaN(generated.getTime());
    const timestamp = validDate ? generated.toLocaleString() : 'Unknown';
    const revision = report.buildRevision || 'Not provided by server';
    $('reportProvenance').textContent =
      'Measured: ' + timestamp + ' | UTC: ' +
      (validDate ? generated.toISOString() : 'unknown') +
      ' | Server build: ' + revision +
      ' | Download filename uses measurement time, not download time.';
    $('report').classList.remove('hidden');
    $('downloadJson').disabled = false;

    const s = report.summary;

    const protocolRows = Array.isArray(report.protocolComparisons)
      ? report.protocolComparisons
      : [];
    const afxdp = protocolRows.find(row =>
      String(row.protocol || '').toLowerCase().includes('af_xdp'));
    const rawIpv6 = protocolRows.find(row =>
      String(row.protocol || '').toLowerCase().includes('raw ipv6'));
    const bestDirectSlotThroughput = Math.max(
      0,
      ...((report.pathMatrix || [])
        .map(row => Number(row.logicalPayloadGigabytesPerSecond || 0))));
    const rawToAfxdp =
      afxdp && rawIpv6 && Number(rawIpv6.packetRate || 0) > 0
        ? Number(afxdp.packetRate || 0) / Number(rawIpv6.packetRate || 0)
        : 0;
    const pokeRows = Array.isArray(report.pokeBenchmarks)
      ? report.pokeBenchmarks
      : [];
    const miniPoke = pokeRows.find(row =>
      Number(row.packetBytes) === 16);

    $('proofStrip').innerHTML =
      '<article class="proof-card"><span>Measured kernel bypass</span><strong>' +
      (afxdp ? fullInteger(afxdp.packetRate) : '—') +
      '</strong><small>AF_XDP packet operations/s from this Full Report run.</small></article>' +
      '<article class="proof-card"><span>AF_XDP vs Raw IPv6</span><strong>' +
      (rawToAfxdp > 0 ? fullDecimal(rawToAfxdp, 2) + '×' : '—') +
      '</strong><small>Same-run operation-rate multiple at a 1,408-byte application payload.</small></article>' +
      '<article class="proof-card"><span>Direct-slot logical ceiling</span><strong>' +
      fullDecimal(bestDirectSlotThroughput, 1) +
      ' GB/s</strong><small>Payload-equivalent rate at the copy-free DHMP processing ceiling. Transport/NIC/RAM byte movement is not timed.</small></article>' +
      '<article class="proof-card"><span>Steady-state allocation</span><strong>' +
      fullDecimal(s.worstMeasuredFullPathAllocationBytesPerCall, 3) +
      ' B/call</strong><small>' +
      (s.allCorrectnessChecksPassed ? 'All correctness checks passed.' : 'One or more correctness checks failed.') +
      '</small></article>' +
      '<article class="proof-card"><span>Poke mini echo ceiling</span><strong>' +
      (miniPoke ? fullInteger(miniPoke.echoesPerSecond) : '—') +
      '</strong><small>16-byte Span-based exact echoes/s; local control-plane processing ceiling, not network RTT.</small></article>';

    const http = protocolRows.find(row =>
      String(row.protocol || '').toLowerCase().startsWith('http/'));
    const tcp = protocolRows.find(row =>
      String(row.protocol || '').toLowerCase().startsWith('tcp/'));
    const udp = protocolRows.find(row =>
      String(row.protocol || '').toLowerCase().startsWith('udp/'));

    const advantage = $('advantageGrid');
    if (advantage) {
      const afxdpRate = Number(afxdp?.packetRate || 0);
      const rawRate = Number(rawIpv6?.packetRate || 0);
      const udpRate = Number(udp?.packetRate || 0);
      const tcpRate = Number(tcp?.packetRate || 0);
      const httpRate = Number(http?.packetRate || 0);

      advantage.innerHTML =
        '<article class="proof-card"><span>Kernel bypass gain</span><strong>' +
        (rawRate > 0 ? fullDecimal(afxdpRate / rawRate, 2) + '×' : '—') +
        '</strong><small>DHMP AF_XDP versus DHMP Raw IPv6 in the same run.</small></article>' +
        '<article class="proof-card"><span>Measured AF_XDP rate</span><strong>' +
        (afxdpRate > 0 ? fullInteger(afxdpRate) : '—') +
        '</strong><small>Packet operations per second on the measured kernel-bypass path.</small></article>' +
        '<article class="proof-card"><span>Direct-slot headroom</span><strong>' +
        fullDecimal(bestDirectSlotThroughput, 1) +
        ' GB/s</strong><small>Logical payload-equivalent processing ceiling after direct receive into the mode-owned destination; not physical wire or memory bandwidth.</small></article>' +
        '<article class="proof-card"><span>Steady-state allocation</span><strong>' +
        fullDecimal(s.worstMeasuredFullPathAllocationBytesPerCall, 3) +
        ' B/call</strong><small>' +
        (s.allCorrectnessChecksPassed ? 'All correctness checks passed.' : 'Correctness checks require attention.') +
        '</small></article>';
    }

    $('summaryGrid').innerHTML =
      metric('Fastest core', Number(s.fastestCoreNanosecondsPerPacket).toFixed(2), 'ns/packet') +
      metric('Core processing ceiling', fullInteger(s.fastestCorePacketCeiling), 'packet-process ops/s') +
      metric('Fastest single path', fullInteger(s.fastestSinglePathPacketRate), 'packet transactions/s') +
      metric('Best aggregate', fullInteger(s.bestAggregatePacketRate), 'packet transactions/s') +
      metric('Best aggregate workers', s.bestAggregateWorkers, 'workers') +
      metric('Worst measured allocation', Number(s.worstMeasuredFullPathAllocationBytesPerCall).toFixed(3), 'B/call') +
      metric('Correctness', s.allCorrectnessChecksPassed ? 'PASS' : 'FAIL', 'all checks');

    const e = report.environment;
    $('environmentGrid').innerHTML =
      metric('OS', e.os, '') +
      metric('Architecture', e.architecture, '') +
      metric('Runtime', e.runtime, '') +
      metric('Logical CPUs', e.logicalProcessors, '') +
      metric('Server GC', e.serverGC ? 'Yes' : 'No', '') +
      metric('GC latency', e.gcLatencyMode, '') +
      metric('Stopwatch frequency', fullInteger(e.stopwatchFrequency), 'ticks/s') +
      metric('DHMP record', e.recordSize, 'bytes');

    $('matrixBody').innerHTML = report.pathMatrix.map(row =>
      '<tr>' +
      '<td>' + modeName(row) + '</td>' +
      '<td>' + Number(row.packetBytes).toLocaleString() + '</td>' +
      '<td>' + Number(row.recordsPerPacket).toLocaleString() + '</td>' +
      '<td>' + Number(row.processorNanoseconds.median).toFixed(2) + '</td>' +
      '<td>' + Number(row.processorNanoseconds.coefficientOfVariationPercent).toFixed(2) + '%</td>' +
      '<td>' + Number(row.serverNanoseconds.median).toFixed(2) + '</td>' +
      '<td>' + Number(row.prebufferedServerNanoseconds?.median || 0).toFixed(2) + '</td>' +
      '<td>' + Number(row.clientNanoseconds.median).toFixed(2) + '</td>' +
      '<td>' + fullInteger(row.packetRate) + '</td>' +
      '<td>' + fullInteger(row.logicalRecordsPerSecond) + '</td>' +
      '<td>' + Number(row.logicalPayloadGigabytesPerSecond).toFixed(2) + '</td>' +
      '</tr>').join('');

    const aggregateRowsForTable = Array.isArray(report.aggregateTiming)
      ? report.aggregateTiming
      : [];

    $('aggregateTimingBody').innerHTML = aggregateRowsForTable.map(row =>
      '<tr>' +
      '<td>' + modeName(row) + '</td>' +
      '<td>' + escapeHtml(row.ratePolicy || 'Unlimited') + '</td>' +
      '<td>' + escapeHtml(row.confirmationMode || 'None') + '</td>' +
      '<td>' + fullInteger(row.returnBytesPerForwardPacket || 0) + '</td>' +
      '<td>' + Number(row.packetBytes).toLocaleString() + '</td>' +
      '<td>' + fullInteger(row.packetsPerPass) + '</td>' +
      '<td>' + fullInteger(row.directReceive?.passesPerClockCheck || 1) + '</td>' +
      '<td>' + fullInteger(row.directReceive?.totalPackets || 0) + '</td>' +
      '<td>' + fullDecimal(row.directReceive?.elapsedMilliseconds || 0, 3) + '</td>' +
      '<td>' + fullDecimal(row.directReceive?.nanosecondsPerPacket || 0, 3) + '</td>' +
      '<td>' + fullInteger(row.directReceive?.packetRate || 0) + '</td>' +
      '<td>' + fullInteger(row.fullClient?.totalPackets || 0) + '</td>' +
      '<td>' + fullDecimal(row.fullClient?.elapsedMilliseconds || 0, 3) + '</td>' +
      '<td>' + fullDecimal(row.fullClient?.nanosecondsPerPacket || 0, 3) + '</td>' +
      '<td>' + fullInteger(row.fullClient?.packetRate || 0) + '</td>' +
      '<td>' + fullDecimal(row.fullClient?.logicalPayloadGigabytesPerSecond || 0, 2) + '</td>' +
      '<td>' + (row.fullClient?.samples?.length || 1) + '</td>' +
      '<td>' + fullDecimal(row.fullClient?.nanosecondsPerPacketStats?.median ?? row.fullClient?.nanosecondsPerPacket ?? 0, 3) + '</td>' +
      '<td>' + (row.fullClient?.nanosecondsPerPacketStats ? fullDecimal(row.fullClient.nanosecondsPerPacketStats.coefficientOfVariationPercent, 2) : '—') + '</td>' +
      '</tr>').join('');

    const localBatchRows = Array.isArray(report.localBatchMatrix)
      ? report.localBatchMatrix
      : [];

    $('localBatchBody').innerHTML = localBatchRows.map(row =>
      '<tr>' +
      '<td>' + modeName(row) + '</td>' +
      '<td>' + Number(row.batchBytes).toLocaleString() + '</td>' +
      '<td>' + Number(row.recordsPerBatch).toLocaleString() + '</td>' +
      '<td>' + Number(row.processorNanoseconds.median).toFixed(2) + '</td>' +
      '<td>' + Number(row.serverNanoseconds.median).toFixed(2) + '</td>' +
      '<td>' + Number(row.clientNanoseconds.median).toFixed(2) + '</td>' +
      '<td>' + fullInteger(row.batchRate) + '</td>' +
      '<td>' + fullInteger(row.logicalRecordsPerSecond) + '</td>' +
      '<td>' + Number(row.logicalPayloadGigabytesPerSecond).toFixed(2) + '</td>' +
      '</tr>').join('');

    $('scalingBody').innerHTML = report.workerScaling.map(row =>
      '<tr><td>' + row.workers + '</td><td>' + row.packetBytes + '</td><td>' +
      fullInteger(row.packetRate) + '</td><td>' + fullInteger(row.logicalRecordsPerSecond) +
      '</td><td>' + Number(row.logicalPayloadGigabytesPerSecond).toFixed(2) +
      '</td><td>' + Number(row.elapsedSeconds).toFixed(3) + ' s</td></tr>').join('');

    $('rateBody').innerHTML = report.ratePolicies.map(row =>
      '<tr><td>' + row.ratePolicy + '</td><td>' + row.packetBytes + '</td><td>' +
      Number(row.clientNanoseconds.median).toFixed(2) + '</td><td>' +
      Number(row.clientNanoseconds.min).toFixed(2) + '</td><td>' +
      Number(row.clientNanoseconds.max).toFixed(2) + '</td><td>' +
      Number(row.clientNanoseconds.coefficientOfVariationPercent).toFixed(2) + '%</td><td>' +
      fullInteger(row.packetRate) + '</td></tr>').join('');

    $('confirmBody').innerHTML = report.confirmationModes.map(row =>
      '<tr><td>' + row.confirmationMode + '</td><td>' + row.packetBytes + '</td><td>' +
      Number(row.roundTripNanoseconds.median).toFixed(2) + '</td><td>' +
      Number(row.roundTripNanoseconds.coefficientOfVariationPercent).toFixed(2) + '%</td><td>' +
      fullInteger(row.packetRate) + '</td><td>' + Number(row.returnBytesPerForwardPacket).toLocaleString() +
      '</td></tr>').join('');

    const pokeRowsForTable = Array.isArray(report.pokeBenchmarks)
      ? report.pokeBenchmarks
      : [];

    $('pokeBody').innerHTML = pokeRowsForTable.map(row =>
      '<tr><td>' + (Number(row.packetBytes) === 16 ? 'Mini Poke' : 'Full Echo Poke') +
      '</td><td>' + fullInteger(row.packetBytes) + '</td><td>' +
      fullDecimal(row.roundTripNanoseconds.median, 2) + '</td><td>' +
      fullDecimal(row.roundTripNanoseconds.coefficientOfVariationPercent, 2) + '%</td><td>' +
      fullInteger(row.echoesPerSecond) + '</td><td>' +
      fullDecimal(row.roundTripGigabytesPerSecond, 3) + '</td><td>' +
      fullDecimal(row.allocatedBytesPerEcho, 3) + '</td></tr>').join('');

    const ring = report.ring3Consumer;
    $('ring3ConsumerGrid').innerHTML =
      metric('Sweeper slot publish', fullDecimal(ring.sweeperNanoseconds.median, 2), 'ns/sweep') +
      metric('Latest grab', fullDecimal(ring.latestGrabNanoseconds.median, 2), 'ns/grab') +
      metric('Native Smoothing grab', fullDecimal(ring.nativeSmoothingGrabNanoseconds.median, 2), 'ns/grab') +
      metric('Smoothing grab CV', fullDecimal(ring.nativeSmoothingGrabNanoseconds.coefficientOfVariationPercent, 2), '%') +
      metric('60 Hz smoothing cost', fullDecimal(ring.nativeSmoothingNanosecondsPerSecondAt60Hz, 2), 'ns CPU time/s') +
      metric('120 Hz smoothing cost', fullDecimal(ring.nativeSmoothingNanosecondsPerSecondAt120Hz, 2), 'ns CPU time/s');

    $('allocationBody').innerHTML = report.allocations.map(row =>
      '<tr><td>' + row.receiveMode + (row.nativeSmoothing ? ' + Native Smoothing' : '') +
      '</td><td>' + row.packetBytes + '</td><td>' +
      Number(row.processorBytesPerCall).toFixed(3) + '</td><td>' +
      Number(row.serverBytesPerCall).toFixed(3) + '</td><td>' +
      Number(row.fullPathBytesPerCall).toFixed(3) + '</td></tr>').join('');

    $('checks').innerHTML = report.correctnessChecks.map(check =>
      '<div class="report-note"><strong class="' + (check.passed ? 'pass' : 'fail') + '">' +
      (check.passed ? 'PASS' : 'FAIL') + '</strong> — ' + check.name +
      '<div class="muted mono">' + check.detail + '</div></div>').join('');

    $('notes').innerHTML = report.interpretationNotes.map(note =>
      '<div class="report-note">' + note + '</div>').join('');

    renderCharts(report);
  }

  async function poll() {
    try {
      const response = await fetch('/api/report/status', { cache: 'no-store' });
      const status = await response.json();

      const total = Math.max(1, Number(status.totalSteps || 1));
      const complete = Number(status.completedSteps || 0);
      $('progressFill').style.width = Math.min(100, complete / total * 100) + '%';
      $('phase').textContent = status.phase || 'idle';

      if (status.running) {
        $('reportState').textContent = 'running';
        $('reportState').classList.add('live');
        $('runReport').disabled = true;
      } else {
        $('runReport').disabled = false;
        $('reportState').classList.remove('live');
        $('reportState').textContent = status.error ? 'failed' : (status.report ? 'complete' : 'idle');
      }

      if (status.report) render(status.report);

      if (status.error) {
        $('phase').textContent = status.error;
      }
    } catch (error) {
      $('reportState').textContent = 'unavailable';
      $('phase').textContent = String(error);
    }
  }

  async function run() {
    $('runReport').disabled = true;
    $('downloadJson').disabled = true;
    latestReport = null;
    $('report').classList.add('hidden');

    const response = await fetch('/api/report/run', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' }
    });

    if (!response.ok && response.status !== 409) {
      throw new Error('HTTP ' + response.status);
    }

    await poll();
  }

  $('runReport').addEventListener('click', () => {
    run().catch(error => {
      $('runReport').disabled = false;
      $('reportState').textContent = 'failed';
      $('phase').textContent = String(error);
    });
  });

  $('downloadJson').addEventListener('click', () => {
    if (!latestReport) return;
    const blob = new Blob(
      [JSON.stringify(latestReport, null, 2)],
      { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    const runTime = latestReport.generatedUtc
      ? new Date(latestReport.generatedUtc)
      : null;
    const stamp = runTime && !Number.isNaN(runTime.getTime())
      ? runTime.toISOString().replace(/[:.]/g, '-')
      : 'unknown-run-time';
    anchor.download = 'dhmp-full-report-' + stamp + '.json';
    anchor.click();
    URL.revokeObjectURL(url);
  });

  ['aggregateRatePolicy', 'aggregateConfirmation'].forEach(id =>
    $(id).addEventListener('change', () => { if (latestReport) renderCharts(latestReport); }));

  window.addEventListener('resize', () => {
    if (latestReport) renderCharts(latestReport);
  });

  poll();
  timer = setInterval(poll, 750);
})();
