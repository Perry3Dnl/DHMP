(() => {
  const PACKETS=[16,256,1024,4096,16384,32768,65520];
  const $=id=>document.getElementById(id);
  const packetSlider=$('packetSize');
  const workerSlider=$('workers');
  const packetLabel=$('packetLabel');
  const workerLabel=$('workerLabel');
  const state=$('state');
  const receiveButtons=[...document.querySelectorAll('[data-receive-mode]')];
  const rateButtons=[...document.querySelectorAll('[data-rate-policy]')];
  const confirmationButtons=[...document.querySelectorAll('[data-confirmation-mode]')];
  const recordsHistory=[];
  const throughputHistory=[];
  let previous=null;
  let configureTimer=0;
  let selectedReceiveMode='Sequential';
  let selectedNativeSmoothing=false;
  let selectedRatePolicy='RejectWindow';
  let selectedConfirmationMode='None';

  function compact(v){
    if(v>=1e9)return (v/1e9).toFixed(2)+'B';
    if(v>=1e6)return (v/1e6).toFixed(2)+'M';
    if(v>=1e3)return (v/1e3).toFixed(1)+'K';
    return v.toFixed(v<10?2:0);
  }

  function draw(canvas, series, labels){
    const ctx=canvas.getContext('2d');
    const w=canvas.clientWidth||600,h=canvas.clientHeight||250,dpr=Math.min(devicePixelRatio||1,2);
    canvas.width=Math.floor(w*dpr);canvas.height=Math.floor(h*dpr);ctx.setTransform(dpr,0,0,dpr,0,0);
    const l=55,r=12,t=14,b=26,pw=w-l-r,ph=h-t-b;
    ctx.fillStyle='#090d12';ctx.fillRect(0,0,w,h);
    let max=1;for(const values of series)for(const v of values)max=Math.max(max,v);
    ctx.strokeStyle='#202833';ctx.fillStyle='#748091';ctx.font='11px system-ui';
    for(let i=0;i<=4;i++){const y=t+ph*i/4;ctx.beginPath();ctx.moveTo(l,y);ctx.lineTo(w-r,y);ctx.stroke();ctx.fillText(compact(max*(1-i/4)),4,y+4)}
    const colors=['#69b7ff','#7ce38b'];
    series.forEach((values,si)=>{if(values.length<2)return;ctx.strokeStyle=colors[si];ctx.lineWidth=2;ctx.beginPath();values.forEach((v,i)=>{const x=l+pw*i/Math.max(1,values.length-1);const y=t+ph*(1-v/max);i?ctx.lineTo(x,y):ctx.moveTo(x,y)});ctx.stroke()});
    labels.forEach((label,i)=>{ctx.fillStyle=colors[i];ctx.fillRect(l+i*128,h-15,10,3);ctx.fillStyle='#8d98a7';ctx.fillText(label,l+16+i*128,h-10)});
  }

  function renderCharts(){
    draw($('recordsChart'),[recordsHistory.map(x=>x.submitted),recordsHistory.map(x=>x.published)],['submitted/s','published/s']);
    draw($('throughputChart'),[throughputHistory],['GB/s']);
  }

  async function configure(){
    const packetBytes=PACKETS[Number(packetSlider.value)];
    const workers=Number(workerSlider.value);
    packetLabel.textContent=packetBytes.toLocaleString()+' bytes';
    workerLabel.textContent=workers.toString();
    state.textContent='reconfiguring…';state.classList.remove('live');
    const response=await fetch('/api/stress/configure',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({packetBytes,workers,receiveMode:selectedReceiveMode,ratePolicy:selectedRatePolicy,nativeSmoothing:selectedNativeSmoothing,confirmationMode:selectedConfirmationMode})});
    if(!response.ok)throw new Error('HTTP '+response.status);
    previous=null;recordsHistory.length=0;throughputHistory.length=0;renderCharts();
  }

  async function poll(){
    try{
      const response=await fetch('/api/stress/stats',{cache:'no-store'});
      if(!response.ok)throw new Error('HTTP '+response.status);
      const cur=await response.json();

      $('packetBytes').textContent=Number(cur.packetBytes).toLocaleString();
      $('recordsPerPacket').textContent=Number(cur.recordsPerPacket).toLocaleString();
      $('activeWorkers').textContent=cur.workers;
      $('logicalProcessors').textContent=cur.logicalProcessors;
      $('receiveMode').textContent=cur.receiveMode;
      $('nativeSmoothing').textContent=cur.nativeSmoothing?'Ring-3 ON':'OFF';
      $('ratePolicy').textContent=cur.ratePolicy;
      $('confirmationMode').textContent=cur.confirmationMode;
      selectedReceiveMode=cur.receiveMode;
      selectedNativeSmoothing=Boolean(cur.nativeSmoothing);
      selectedRatePolicy=cur.ratePolicy;
      selectedConfirmationMode=cur.confirmationMode;
      receiveButtons.forEach(button=>{
        const smoothing=button.dataset.nativeSmoothing==='true';
        button.classList.toggle('active',button.dataset.receiveMode===selectedReceiveMode&&smoothing===selectedNativeSmoothing);
      });
      rateButtons.forEach(button=>button.classList.toggle('active',button.dataset.ratePolicy===selectedRatePolicy));
      confirmationButtons.forEach(button=>button.classList.toggle('active',button.dataset.confirmationMode===selectedConfirmationMode));
      $('workerFaults').textContent=Number(cur.workerFaults).toLocaleString();
      $('workerError').textContent=cur.lastWorkerError||'none';
      workerSlider.max=Math.max(1,Math.min(16,cur.logicalProcessors*2));

      if(previous){
        const sec=Math.max(.001,(cur.uptimeMilliseconds-previous.uptimeMilliseconds)/1000);
        const submitted=(cur.recordsSubmitted-previous.recordsSubmitted)/sec;
        const published=(cur.recordsPublished-previous.recordsPublished)/sec;
        const packets=(cur.packetsSubmitted-previous.packetsSubmitted)/sec;
        const submittedBytes=(cur.bytesSubmitted-previous.bytesSubmitted)/sec;
        const gb=submittedBytes/1e9;
        const returnedBytes=Math.max(0,cur.confirmationBytesReturned-previous.confirmationBytesReturned)/sec;
        const returnedRecords=Math.max(0,cur.confirmationRecordsReturned-previous.confirmationRecordsReturned)/sec;
        const returnGb=returnedBytes/1e9;
        const combinedGb=(submittedBytes+returnedBytes)/1e9;
        const acceptedPackets=Math.max(0,cur.packetsAccepted-previous.packetsAccepted);
        const expectedPublished=acceptedPackets*Math.max(1,cur.expectedPublishedRecordsPerPacket);
        const publishedDelta=Math.max(0,cur.recordsPublished-previous.recordsPublished);
        const parity=expectedPublished>0?publishedDelta/expectedPublished*100:0;
        const sendTicks=Math.max(0,cur.sendTicks-previous.sendTicks);
        const processTicks=Math.max(0,cur.processTicks-previous.processTicks);
        const packetDelta=Math.max(1,cur.packetsSubmitted-previous.packetsSubmitted);
        const nsPerTick=1e9/Math.max(1,cur.stopwatchFrequency);

        $('recordsRate').textContent=Math.round(submitted).toLocaleString();
        $('gbps').textContent=gb.toFixed(2);
        $('packetsRate').textContent=Math.round(packets).toLocaleString();
        $('parity').textContent=parity.toFixed(2)+'%';
        $('submittedRecords').textContent=compact(submitted);
        $('publishedRecords').textContent=compact(published);
        $('payloadGb').textContent=gb.toFixed(2);
        $('packetRate').textContent=compact(packets);
        $('returnGb').textContent=returnGb.toFixed(2);
        $('combinedGb').textContent=combinedGb.toFixed(2);
        $('returnRecords').textContent=compact(returnedRecords)+'/s';
        $('publicationParity').textContent=parity.toFixed(3)+'%';
        $('sendNs').textContent=(sendTicks*nsPerTick/packetDelta).toFixed(1)+' ns/packet';
        $('processNs').textContent=(processTicks*nsPerTick/packetDelta).toFixed(1)+' ns/packet';

        recordsHistory.push({submitted,published});
        throughputHistory.push(gb);
        if(recordsHistory.length>60)recordsHistory.shift();
        if(throughputHistory.length>60)throughputHistory.shift();
        renderCharts();
      }

      previous=cur;
      if(cur.workerFaults>0 && cur.lastWorkerError){
        state.textContent='stress test recovered from worker fault';
      }else{
        state.textContent='stress test live';
      }
      state.classList.add('live');
    }catch{
      state.textContent='telemetry unavailable';state.classList.remove('live');
    }
  }

  function queueConfigure(){
    clearTimeout(configureTimer);
    configureTimer=setTimeout(()=>configure().catch(()=>{state.textContent='configuration failed';}),180);
  }

  packetSlider.addEventListener('input',()=>{packetLabel.textContent=PACKETS[Number(packetSlider.value)].toLocaleString()+' bytes';queueConfigure()});
  workerSlider.addEventListener('input',()=>{workerLabel.textContent=workerSlider.value;queueConfigure()});
  receiveButtons.forEach(button=>button.addEventListener('click',()=>{
    selectedReceiveMode=button.dataset.receiveMode;
    selectedNativeSmoothing=button.dataset.nativeSmoothing==='true';
    receiveButtons.forEach(item=>item.classList.toggle('active',item===button));
    queueConfigure();
  }));
  rateButtons.forEach(button=>button.addEventListener('click',()=>{
    selectedRatePolicy=button.dataset.ratePolicy;
    rateButtons.forEach(item=>item.classList.toggle('active',item===button));
    queueConfigure();
  }));
  confirmationButtons.forEach(button=>button.addEventListener('click',()=>{
    selectedConfirmationMode=button.dataset.confirmationMode;
    if(selectedConfirmationMode!=='None'){
      selectedReceiveMode='Sequential';
      selectedNativeSmoothing=false;
      receiveButtons.forEach(item=>item.classList.toggle('active',item.dataset.receiveMode==='Sequential'&&item.dataset.nativeSmoothing!=='true'));
    }
    confirmationButtons.forEach(item=>item.classList.toggle('active',item===button));
    queueConfigure();
  }));
  window.addEventListener('resize',renderCharts);

  poll();
  setInterval(poll,1000);
})();