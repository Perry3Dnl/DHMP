(() => {
  const $ = id => document.getElementById(id);
  let latestReport = null;
  let timer = 0;

  function compact(value) {
    const v = Number(value || 0);
    if (v >= 1e12) return (v / 1e12).toFixed(2) + 'T';
    if (v >= 1e9) return (v / 1e9).toFixed(2) + 'B';
    if (v >= 1e6) return (v / 1e6).toFixed(2) + 'M';
    if (v >= 1e3) return (v / 1e3).toFixed(1) + 'K';
    return v.toFixed(v < 10 ? 2 : 0);
  }

  function metric(label, value, unit = '') {
    return '<article class="metric-card"><span>' + label + '</span><strong>' +
      value + '</strong><small>' + unit + '</small></article>';
  }

  function modeName(row) {
    return row.receiveMode + (row.nativeSmoothing ? ' + Ring-3' : '');
  }

  function render(report) {
    latestReport = report;
    $('report').classList.remove('hidden');
    $('downloadJson').disabled = false;

    const s = report.summary;
    $('summaryGrid').innerHTML =
      metric('Fastest core', Number(s.fastestCoreNanosecondsPerPacket).toFixed(2), 'ns/packet') +
      metric('Core processing ceiling', compact(s.fastestCorePacketCeiling), 'packet-process ops/s') +
      metric('Fastest single path', compact(s.fastestSinglePathPacketRate), 'packet transactions/s') +
      metric('Best aggregate', compact(s.bestAggregatePacketRate), 'packet transactions/s') +
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
      metric('Stopwatch frequency', compact(e.stopwatchFrequency), 'ticks/s') +
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
      '<td>' + compact(row.packetRate) + '</td>' +
      '<td>' + compact(row.logicalRecordsPerSecond) + '</td>' +
      '<td>' + Number(row.logicalPayloadGigabytesPerSecond).toFixed(2) + '</td>' +
      '</tr>').join('');

    $('scalingBody').innerHTML = report.workerScaling.map(row =>
      '<tr><td>' + row.workers + '</td><td>' + row.packetBytes + '</td><td>' +
      compact(row.packetRate) + '</td><td>' + compact(row.logicalRecordsPerSecond) +
      '</td><td>' + Number(row.logicalPayloadGigabytesPerSecond).toFixed(2) +
      '</td><td>' + Number(row.elapsedSeconds).toFixed(3) + ' s</td></tr>').join('');

    $('rateBody').innerHTML = report.ratePolicies.map(row =>
      '<tr><td>' + row.ratePolicy + '</td><td>' + row.packetBytes + '</td><td>' +
      Number(row.clientNanoseconds.median).toFixed(2) + '</td><td>' +
      Number(row.clientNanoseconds.min).toFixed(2) + '</td><td>' +
      Number(row.clientNanoseconds.max).toFixed(2) + '</td><td>' +
      Number(row.clientNanoseconds.coefficientOfVariationPercent).toFixed(2) + '%</td><td>' +
      compact(row.packetRate) + '</td></tr>').join('');

    $('confirmBody').innerHTML = report.confirmationModes.map(row =>
      '<tr><td>' + row.confirmationMode + '</td><td>' + row.packetBytes + '</td><td>' +
      Number(row.roundTripNanoseconds.median).toFixed(2) + '</td><td>' +
      Number(row.roundTripNanoseconds.coefficientOfVariationPercent).toFixed(2) + '%</td><td>' +
      compact(row.packetRate) + '</td><td>' + Number(row.returnBytesPerForwardPacket).toLocaleString() +
      '</td></tr>').join('');

    $('allocationBody').innerHTML = report.allocations.map(row =>
      '<tr><td>' + row.receiveMode + (row.nativeSmoothing ? ' + Ring-3' : '') +
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

  poll();
  timer = setInterval(poll, 750);
})();