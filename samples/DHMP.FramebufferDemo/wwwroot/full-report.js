(() => {
  const $ = id => document.getElementById(id);
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
    const maxValue = Math.max(1, ...series.flatMap(s => s.values.map(Number)));
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


  function renderProtocolComparison(report) {
    const rows = Array.isArray(report.protocolComparisons)
      ? report.protocolComparisons
      : [];

    if (!rows.length) return;

    const ioRows = rows.filter(
      row => !String(row.protocol || '').toLowerCase().includes('in-memory'));

    const labels = ioRows.map(row => row.protocol);

    drawGroupedBarChart(
      'protocolPacketRateChart',
      labels,
      [{
        label: 'Measured operations/s',
        values: ioRows.map(row => Number(row.packetRate || 0))
      }],
      fullInteger);

    drawGroupedBarChart(
      'protocolThroughputChart',
      labels,
      [{
        label: 'Application payload GB/s',
        values: ioRows.map(row => Number(row.payloadGigabytesPerSecond || 0))
      }],
      value => fullDecimal(value, 3));

    const afxdp = ioRows.find(row =>
      String(row.protocol || '').toLowerCase().includes('af_xdp'));

    if (afxdp) {
      const rawIpv6 = ioRows.find(row =>
        String(row.protocol || '').toLowerCase().includes('raw ipv6'));
      const rawRate = Number(rawIpv6?.packetRate || 0);

      if (rawIpv6 && rawRate > 0) {
        drawGroupedBarChart(
          'protocolSpeedupChart',
          ['DHMP Raw IPv6'],
          [{
            label: 'AF_XDP rate multiple',
            values: [Number(afxdp.packetRate || 0) / rawRate]
          }],
          value => fullDecimal(value, 2) + '×');
      }
    }

    const body = $('protocolComparisonBody');
    if (body) {
      body.innerHTML = rows.map(row => {
        const isCeiling =
          String(row.protocol || '').toLowerCase().includes('in-memory');
        const classLabel = isCeiling
          ? '<span class="scope-badge ceiling">Software ceiling</span>'
          : '<span class="scope-badge">Measured I/O</span>';

        return '<tr>' +
          '<td>' + row.protocol + '</td>' +
          '<td style="text-align:left;white-space:normal;min-width:220px">' + row.scope + '</td>' +
          '<td>' + classLabel + '</td>' +
          '<td>' + fullInteger(row.packetBytes) + ' B</td>' +
          '<td>' + fullInteger(row.packetRate) + '</td>' +
          '<td>' + fullDecimal(row.payloadGigabytesPerSecond, 3) + '</td>' +
          '<td>' + (row.kernelBypass ? 'Yes — AF_XDP' : 'No') + '</td>' +
          '<td style="text-align:left;white-space:normal;min-width:300px">' + row.detail + '</td>' +
          '</tr>';
      }).join('');
    }
  }


  function renderDhmpValueStory(report) {
    const scaling = Array.isArray(report.workerScaling)
      ? report.workerScaling
      : [];

    if (!scaling.length) return;

    const workers = scaling.map(row => String(row.workers));

    drawLineChart(
      'softwareThroughputChart',
      workers,
      scaling.map(row => Number(row.logicalPayloadGigabytesPerSecond || 0)),
      'Workers → logical payload GB/s (software ceiling)',
      value => fullDecimal(value, 1));

    const oneWorkerRate = Number(scaling[0].packetRate || 0);
    drawGroupedBarChart(
      'workerSpeedupChart',
      workers.map(value => value + ' worker' + (value === '1' ? '' : 's')),
      [{
        label: 'Speedup vs 1 worker',
        values: scaling.map(row =>
          oneWorkerRate > 0
            ? Number(row.packetRate || 0) / oneWorkerRate
            : 0)
      }],
      value => fullDecimal(value, 2) + '×');

    drawLineChart(
      'recordsScalingChart',
      workers,
      scaling.map(row => Number(row.logicalRecordsPerSecond || 0)),
      'Workers → logical records/s',
      fullInteger);
  }

  function renderCharts(report) {
    const packetSizes = [...new Set(report.pathMatrix.map(r => Number(r.packetBytes)))];
    const modeLabels = ['Sequential', 'Latest', 'Latest + Native Smoothing'];

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

    drawLineChart(
      'workerScalingChart',
      report.workerScaling.map(r => String(r.workers)),
      report.workerScaling.map(r => Number(r.packetRate)),
      'Workers → packet transactions/s',
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
    const bestScaling = Math.max(
      0,
      ...(report.workerScaling || [])
        .map(row => Number(row.logicalPayloadGigabytesPerSecond || 0)));
    const rawToAfxdp =
      afxdp && rawIpv6 && Number(rawIpv6.packetRate || 0) > 0
        ? Number(afxdp.packetRate || 0) / Number(rawIpv6.packetRate || 0)
        : 0;

    $('proofStrip').innerHTML =
      '<article class="proof-card"><span>Measured kernel bypass</span><strong>' +
      (afxdp ? fullInteger(afxdp.packetRate) : '—') +
      '</strong><small>AF_XDP packet operations/s from this Full Report run.</small></article>' +
      '<article class="proof-card"><span>AF_XDP vs Raw IPv6</span><strong>' +
      (rawToAfxdp > 0 ? fullDecimal(rawToAfxdp, 2) + '×' : '—') +
      '</strong><small>Same-run operation-rate multiple at a 1,408-byte application payload.</small></article>' +
      '<article class="proof-card"><span>Software processing ceiling</span><strong>' +
      fullDecimal(bestScaling, 1) +
      ' GB/s</strong><small>Logical in-memory payload processing; explicitly not physical wire throughput.</small></article>' +
      '<article class="proof-card"><span>Steady-state allocation</span><strong>' +
      fullDecimal(s.worstMeasuredFullPathAllocationBytesPerCall, 3) +
      ' B/call</strong><small>' +
      (s.allCorrectnessChecksPassed ? 'All correctness checks passed.' : 'One or more correctness checks failed.') +
      '</small></article>';

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
      '<td>' + Number(row.clientNanoseconds.median).toFixed(2) + '</td>' +
      '<td>' + fullInteger(row.packetRate) + '</td>' +
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
    anchor.download = 'dhmp-full-report-' + new Date().toISOString().replace(/[:.]/g, '-') + '.json';
    anchor.click();
    URL.revokeObjectURL(url);
  });

  window.addEventListener('resize', () => {
    if (latestReport) renderCharts(latestReport);
  });

  poll();
  timer = setInterval(poll, 750);
})();