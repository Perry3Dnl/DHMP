(() => {
  const PAYLOADS=[16,256,1024,1200,1408];
  const $=id=>document.getElementById(id);
  const slider=$('payloadSize');
  const label=$('payloadLabel');
  const state=$('state');
  const packetHistory=[];
  const throughputHistory=[];
  let previous=null;
  let configureTimer=0;

  function compact(v){
    if(v>=1e9)return (v/1e9).toFixed(2)+'B';
    if(v>=1e6)return (v/1e6).toFixed(2)+'M';
    if(v>=1e3)return (v/1e3).toFixed(1)+'K';
    return v.toFixed(v<10?2:0);
  }

  function draw(canvas, values, label){
    const ctx=canvas.getContext('2d');
    const w=canvas.clientWidth||600,h=canvas.clientHeight||250,dpr=Math.min(devicePixelRatio||1,2);
    canvas.width=Math.floor(w*dpr);canvas.height=Math.floor(h*dpr);ctx.setTransform(dpr,0,0,dpr,0,0);
    const l=55,r=12,t=14,b=26,pw=w-l-r,ph=h-t-b;
    ctx.fillStyle='#090d12';ctx.fillRect(0,0,w,h);
    let max=1;for(const v of values)max=Math.max(max,v);
    ctx.strokeStyle='#202833';ctx.fillStyle='#748091';ctx.font='11px system-ui';
    for(let i=0;i<=4;i++){const y=t+ph*i/4;ctx.beginPath();ctx.moveTo(l,y);ctx.lineTo(w-r,y);ctx.stroke();ctx.fillText(compact(max*(1-i/4)),4,y+4)}
    if(values.length>=2){
      ctx.strokeStyle='#69b7ff';ctx.lineWidth=2;ctx.beginPath();
      values.forEach((v,i)=>{const x=l+pw*i/Math.max(1,values.length-1);const y=t+ph*(1-v/max);i?ctx.lineTo(x,y):ctx.moveTo(x,y)});
      ctx.stroke();
    }
    ctx.fillStyle='#69b7ff';ctx.fillRect(l,h-15,10,3);ctx.fillStyle='#8d98a7';ctx.fillText(label,l+16,h-10);
  }

  function renderCharts(){
    draw($('packetChart'),packetHistory,'packets/s');
    draw($('throughputChart'),throughputHistory,'GB/s');
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
    packetHistory.length=0;
    throughputHistory.length=0;
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
      $('interfaceName').textContent=cur.interfaceName;
      $('failureCount').textContent=Number(cur.failures).toLocaleString();
      $('runs').textContent=Number(cur.runs).toLocaleString();
      $('profileRuns').textContent=Number(cur.runs).toLocaleString();
      $('profilePayload').textContent=Number(cur.payloadBytes).toLocaleString();
      $('profileRecords').textContent=Number(cur.recordsPerPacket).toLocaleString();
      $('detail').textContent=cur.detail||'none';

      const payloadIndex=PAYLOADS.indexOf(Number(cur.payloadBytes));
      if(payloadIndex>=0 && document.activeElement!==slider){
        slider.value=String(payloadIndex);
        label.textContent=Number(cur.payloadBytes).toLocaleString()+' bytes';
      }

      if(previous && cur.configurationVersion===previous.configurationVersion){
        const ticks=Math.max(0,cur.benchmarkTicks-previous.benchmarkTicks);
        const seconds=ticks/Math.max(1,cur.stopwatchFrequency);
        const packets=Math.max(0,cur.packetsCompleted-previous.packetsCompleted);
        const bytes=Math.max(0,cur.payloadBytesCompleted-previous.payloadBytesCompleted);

        if(seconds>0 && packets>0){
          const pps=packets/seconds;
          const gb=bytes/seconds/1e9;
          const records=pps*Math.max(1,cur.recordsPerPacket);

          $('packetsRate').textContent=Math.round(pps).toLocaleString();
          $('gbps').textContent=gb.toFixed(3);
          $('recordsRate').textContent=Math.round(records).toLocaleString();
          $('packetRate').textContent=compact(pps);
          $('recordRate').textContent=compact(records);
          $('payloadGb').textContent=gb.toFixed(3);

          packetHistory.push(pps);
          throughputHistory.push(gb);
          if(packetHistory.length>60)packetHistory.shift();
          if(throughputHistory.length>60)throughputHistory.shift();
          renderCharts();
        }
      }

      previous=cur;

      if(cur.mode==='ZeroCopy'){
        state.textContent='AF_XDP zero-copy live';
        state.classList.add('live');
      }else if(cur.mode==='Copy'){
        state.textContent='AF_XDP copy mode live';
        state.classList.add('live');
      }else{
        state.textContent='AF_XDP unavailable';
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

  window.addEventListener('resize',renderCharts);

  poll();
  setInterval(poll,1000);
})();