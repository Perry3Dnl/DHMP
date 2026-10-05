(() => {
  const PAYLOADS=[16,256,1024,1200,1408];
  const $=id=>document.getElementById(id);
  const slider=$('payloadSize');
  const label=$('payloadLabel');
  const state=$('state');
  const rawPacketHistory=[];
  const afxdpPacketHistory=[];
  const rawPayloadHistory=[];
  const afxdpPayloadHistory=[];
  let previous=null;
  let configureTimer=0;

  function compact(v){
    if(v>=1e9)return (v/1e9).toFixed(2)+'B';
    if(v>=1e6)return (v/1e6).toFixed(2)+'M';
    if(v>=1e3)return (v/1e3).toFixed(1)+'K';
    return v.toFixed(v<10?2:0);
  }

  function draw(canvas, rawValues, afxdpValues, suffix){
    const ctx=canvas.getContext('2d');
    const w=canvas.clientWidth||600,h=canvas.clientHeight||250,dpr=Math.min(devicePixelRatio||1,2);
    canvas.width=Math.floor(w*dpr);canvas.height=Math.floor(h*dpr);ctx.setTransform(dpr,0,0,dpr,0,0);
    const l=58,r=12,t=14,b=30,pw=w-l-r,ph=h-t-b;
    ctx.fillStyle='#090d12';ctx.fillRect(0,0,w,h);

    let max=1;
    for(const v of rawValues)max=Math.max(max,v);
    for(const v of afxdpValues)max=Math.max(max,v);

    ctx.strokeStyle='#202833';ctx.fillStyle='#748091';ctx.font='11px system-ui';
    for(let i=0;i<=4;i++){
      const y=t+ph*i/4;
      ctx.beginPath();ctx.moveTo(l,y);ctx.lineTo(w-r,y);ctx.stroke();
      ctx.fillText(compact(max*(1-i/4)),4,y+4);
    }

    function line(values,stroke){
      if(values.length<2)return;
      ctx.strokeStyle=stroke;ctx.lineWidth=2;ctx.beginPath();
      values.forEach((v,i)=>{
        const x=l+pw*i/Math.max(1,values.length-1);
        const y=t+ph*(1-v/max);
        i?ctx.lineTo(x,y):ctx.moveTo(x,y);
      });
      ctx.stroke();
    }

    line(rawValues,'#a5b4c5');
    line(afxdpValues,'#69b7ff');

    ctx.fillStyle='#a5b4c5';ctx.fillRect(l,h-16,10,3);
    ctx.fillStyle='#8d98a7';ctx.fillText('Raw IPv6',l+15,h-10);
    ctx.fillStyle='#69b7ff';ctx.fillRect(l+92,h-16,10,3);
    ctx.fillStyle='#8d98a7';ctx.fillText('AF_XDP '+suffix,l+107,h-10);
  }

  function renderCharts(mode=''){
    draw($('packetChart'),rawPacketHistory,afxdpPacketHistory,mode);
    draw($('throughputChart'),rawPayloadHistory,afxdpPayloadHistory,mode);
  }

  async function configure(){
    const payloadBytes=PAYLOADS[Number(slider.value)];
    label.textContent=payloadBytes.toLocaleString()+' bytes';
    state.textContent='reconfiguring…';
    state.classList.remove('live');

    const response=await fetch('/api/afxdp/configure',{
      method:'POST',
      headers:{'Content-Type':'application/json'},
      body:JSON.stringify({payloadBytes})
    });

    if(!response.ok)throw new Error('HTTP '+response.status);

    previous=null;
    rawPacketHistory.length=0;
    afxdpPacketHistory.length=0;
    rawPayloadHistory.length=0;
    afxdpPayloadHistory.length=0;
    renderCharts();
  }

  async function poll(){
    try{
      const response=await fetch('/api/afxdp/stats',{cache:'no-store'});
      if(!response.ok)throw new Error('HTTP '+response.status);
      const cur=await response.json();

      $('mode').textContent=cur.mode;
      $('activeMode').textContent=cur.mode;
      $('payloadBytes').textContent=Number(cur.payloadBytes).toLocaleString();
      $('recordsPerPacket').textContent=Number(cur.recordsPerPacket).toLocaleString();
      $('packetsPerSample').textContent=Number(cur.packetsPerSample).toLocaleString();
      $('interfaceName').textContent=cur.interfaceName;
      $('failureCount').textContent=Number(cur.failures).toLocaleString();
      $('runs').textContent=Number(cur.runs).toLocaleString();
      $('detail').textContent=cur.detail||'none';

      const payloadIndex=PAYLOADS.indexOf(Number(cur.payloadBytes));
      if(payloadIndex>=0 && document.activeElement!==slider){
        slider.value=String(payloadIndex);
        label.textContent=Number(cur.payloadBytes).toLocaleString()+' bytes';
      }

      if(previous && cur.configurationVersion===previous.configurationVersion){
        const rawTicks=Math.max(0,cur.rawBenchmarkTicks-previous.rawBenchmarkTicks);
        const afTicks=Math.max(0,cur.afXdpBenchmarkTicks-previous.afXdpBenchmarkTicks);
        const rawSeconds=rawTicks/Math.max(1,cur.stopwatchFrequency);
        const afSeconds=afTicks/Math.max(1,cur.stopwatchFrequency);

        const rawPackets=Math.max(0,cur.rawPacketsCompleted-previous.rawPacketsCompleted);
        const afPackets=Math.max(0,cur.afXdpPacketsCompleted-previous.afXdpPacketsCompleted);
        const rawBytes=Math.max(0,cur.rawPayloadBytesCompleted-previous.rawPayloadBytesCompleted);
        const afBytes=Math.max(0,cur.afXdpPayloadBytesCompleted-previous.afXdpPayloadBytesCompleted);

        if(rawSeconds>0 && afSeconds>0 && rawPackets>0 && afPackets>0){
          const rawPps=rawPackets/rawSeconds;
          const afPps=afPackets/afSeconds;
          const rawGb=rawBytes/rawSeconds/1e9;
          const afGb=afBytes/afSeconds/1e9;
          const speedup=afPps/rawPps;
          const payloadSpeedup=afGb/rawGb;
          const recordsPerPacket=Math.max(1,Number(cur.recordsPerPacket));

          $('rawPacketRate').textContent=compact(rawPps);
          $('afxdpPacketRate').textContent=compact(afPps);
          $('rawPayloadGb').textContent=rawGb.toFixed(3);
          $('afxdpPayloadGb').textContent=afGb.toFixed(3);
          $('rawRecordRate').textContent=compact(rawPps*recordsPerPacket);
          $('afxdpRecordRate').textContent=compact(afPps*recordsPerPacket);
          $('speedup').textContent=speedup.toFixed(2)+'×';
          $('payloadSpeedup').textContent=payloadSpeedup.toFixed(2)+'×';
          $('speedupCard').textContent=speedup.toFixed(2)+'×';

          rawPacketHistory.push(rawPps);
          afxdpPacketHistory.push(afPps);
          rawPayloadHistory.push(rawGb);
          afxdpPayloadHistory.push(afGb);

          for(const a of [rawPacketHistory,afxdpPacketHistory,rawPayloadHistory,afxdpPayloadHistory]){
            if(a.length>60)a.shift();
          }

          renderCharts(cur.mode);
        }
      }

      previous=cur;

      if(cur.mode==='ZeroCopy'){
        state.textContent='AF_XDP zero-copy comparison live';
        state.classList.add('live');
      }else if(cur.mode==='Copy'){
        state.textContent='AF_XDP copy-mode comparison live';
        state.classList.add('live');
      }else if(cur.mode==='Probing'){
        state.textContent='running paired sample…';
        state.classList.remove('live');
      }else{
        state.textContent='comparison unavailable';
        state.classList.remove('live');
      }
    }catch{
      state.textContent='telemetry unavailable';
      state.classList.remove('live');
    }
  }

  function queueConfigure(){
    clearTimeout(configureTimer);
    configureTimer=setTimeout(()=>configure().catch(()=>{state.textContent='configuration failed';}),180);
  }

  slider.addEventListener('input',()=>{
    label.textContent=PAYLOADS[Number(slider.value)].toLocaleString()+' bytes';
    queueConfigure();
  });

  window.addEventListener('resize',()=>renderCharts(previous?.mode||''));

  poll();
  setInterval(poll,1000);
})();