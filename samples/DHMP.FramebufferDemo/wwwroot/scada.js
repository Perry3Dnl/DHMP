(() => {
  const PROFILES = [
    { activeTags: 1000, profile: 'single-site', title: 'Single energy site' },
    { activeTags: 10000, profile: 'industrial-site', title: 'Industrial site' },
    { activeTags: 100000, profile: 'regional-grid', title: 'Regional grid' },
    { activeTags: 1000000, profile: 'large-grid', title: 'Large grid' }
  ];

  const $ = id => document.getElementById(id);
  const state = $('state');
  const scale = $('scale');
  const scaleValue = $('scaleValue');
  const profileTitle = $('profileTitle');
  const history = [];
  let previous = null;
  let changeTimer = 0;

  function compact(v) {
    if (v >= 1e9) return (v / 1e9).toFixed(1) + 'B';
    if (v >= 1e6) return (v / 1e6).toFixed(1) + 'M';
    if (v >= 1e3) return (v / 1e3).toFixed(1) + 'K';
    return v.toFixed(v < 10 ? 1 : 0);
  }

  function drawChart(canvas, series, labels) {
    const ctx = canvas.getContext('2d');
    const w = canvas.clientWidth || 560;
    const h = canvas.clientHeight || 220;
    const dpr = Math.min(devicePixelRatio || 1, 2);
    canvas.width = Math.floor(w * dpr);
    canvas.height = Math.floor(h * dpr);
    ctx.setTransform(dpr,0,0,dpr,0,0);
    const l=48,r=12,t=16,b=26,pw=w-l-r,ph=h-t-b;
    ctx.fillStyle='#090c11';ctx.fillRect(0,0,w,h);
    let max=1;
    for(const values of series) for(const v of values) max=Math.max(max,v);
    ctx.strokeStyle='#202833';ctx.fillStyle='#758091';ctx.font='11px system-ui';
    for(let i=0;i<=4;i++){const y=t+ph*i/4;ctx.beginPath();ctx.moveTo(l,y);ctx.lineTo(w-r,y);ctx.stroke();ctx.fillText(compact(max*(1-i/4)),4,y+4);}
    const colors=['#69b7ff','#7ce38b'];
    series.forEach((values,si)=>{if(values.length<2)return;ctx.strokeStyle=colors[si];ctx.lineWidth=2;ctx.beginPath();values.forEach((v,i)=>{const x=l+pw*i/Math.max(1,values.length-1);const y=t+ph*(1-v/max);i?ctx.lineTo(x,y):ctx.moveTo(x,y)});ctx.stroke();});
    labels.forEach((label,i)=>{ctx.fillStyle=colors[i];ctx.fillRect(l+i*120,h-15,10,3);ctx.fillStyle='#8d98a7';ctx.fillText(label,l+16+i*120,h-10);});
  }

  function renderCharts() {
    drawChart($('recordsChart'), [history.map(x=>x.rx), history.map(x=>x.tx)], ['received/s','published/s']);
    drawChart($('throughputChart'), [history.map(x=>x.mbps)], ['record MB/s']);
  }

  async function setProfile(profile) {
    state.textContent='setting workload…';state.classList.remove('live');
    const response=await fetch('/api/scada/workload',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(profile)});
    if(!response.ok) throw new Error('HTTP '+response.status);
    previous=null;history.length=0;
    scaleValue.textContent=profile.activeTags.toLocaleString()+' active tags';
    profileTitle.textContent=profile.title;
    state.textContent='SCADA telemetry live';state.classList.add('live');
  }

  function updateScene(current) {
    $('frequency').textContent=current.frequencyHz.toFixed(3)+' Hz';
    $('northKv').textContent=current.northBusKv.toFixed(1)+' kV';
    $('southKv').textContent=current.southBusKv.toFixed(1)+' kV';
    $('loadMw').textContent=current.gridLoadMw.toFixed(1)+' MW';
    $('tempC').textContent=current.transformerTempC.toFixed(1)+' °C';
    $('breakerText').textContent=current.breakerClosed?'CLOSED':'OPEN';
    $('breakerGraphic').classList.toggle('open',!current.breakerClosed);
    $('alarmCount').textContent=current.alarmCount;
    const badge=$('alarmBadge');
    badge.textContent=current.alarmCount?'ATTENTION':'SYSTEM NORMAL';
    badge.classList.toggle('ok',!current.alarmCount);
    badge.classList.toggle('warn',!!current.alarmCount);
  }

  async function poll() {
    try {
      const response=await fetch('/api/scada/stats',{cache:'no-store'});
      if(!response.ok) throw new Error('HTTP '+response.status);
      const current=await response.json();
      updateScene(current);
      $('tagCount').textContent=Number(current.activeTags).toLocaleString();
      if(previous){
        const elapsed=Math.max(.001,(current.uptimeMilliseconds-previous.uptimeMilliseconds)/1000);
        const rx=Math.max(0,current.receivedRecords-previous.receivedRecords)/elapsed;
        const tx=Math.max(0,current.publishedRecords-previous.publishedRecords)/elapsed;
        const mbps=Math.max(0,current.receivedRecordBytes-previous.receivedRecordBytes)/elapsed/1e6;
        const pct=rx/Math.max(1,current.targetRecordsPerSecond)*100;
        $('tagRate').textContent=Math.round(rx).toLocaleString();
        $('throughput').textContent=mbps.toFixed(2);
        $('target').textContent=pct.toFixed(1)+'%';
        $('serverRx').textContent=Math.round(rx).toLocaleString();
        $('serverTx').textContent=Math.round(tx).toLocaleString();
        $('serverMbps').textContent=mbps.toFixed(2);
        $('serverTotal').textContent=Number(current.receivedRecords).toLocaleString();
        history.push({rx,tx,mbps});
        if(history.length>60)history.shift();
        renderCharts();
      }
      previous=current;
      state.textContent='SCADA telemetry live';state.classList.add('live');
    } catch {
      state.textContent='telemetry unavailable';state.classList.remove('live');
    }
  }

  scale.addEventListener('input',()=>{
    const profile=PROFILES[Number(scale.value)];
    scaleValue.textContent=profile.activeTags.toLocaleString()+' active tags';
    profileTitle.textContent=profile.title;
    clearTimeout(changeTimer);
    changeTimer=setTimeout(()=>setProfile(profile).catch(()=>{state.textContent='workload update failed';}),180);
  });

  window.addEventListener('resize',renderCharts);
  setProfile(PROFILES[2]).catch(()=>{});
  poll();
  setInterval(poll,1000);
})();