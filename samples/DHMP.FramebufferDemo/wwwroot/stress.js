(() => {
  const PACKETS=[16,256,1024,4096,16384,32768,65520];
  const $=id=>document.getElementById(id);
  const packetSlider=$('packetSize');
  const workerSlider=$('workers');
  const packetRateCapSlider=$('packetRateCap');
  const packetRateCapLabel=$('packetRateCapLabel');
  const PACKET_RATE_CAPS=[0,1e6,2e6,5e6,10e6,20e6,30e6,40e6,50e6,75e6,100e6];
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
  let configurationPending=false;
  let configurationInFlight=0;
  let configurationRevision=0;
  let minimumServerConfigurationVersion=0;
  let selectedReceiveMode='Sequential';
  let selectedNativeSmoothing=false;
  let selectedRatePolicy='Unlimited';
  let selectedConfirmationMode='None';
  let selectedPacketRateCap=0;

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


  function renderSelectedControls(){
    receiveButtons.forEach(button=>{
      const smoothing=button.dataset.nativeSmoothing==='true';
      button.classList.toggle(
        'active',
        button.dataset.receiveMode===selectedReceiveMode &&
        smoothing===selectedNativeSmoothing);
    });

    rateButtons.forEach(button=>
      button.classList.toggle(
        'active',
        button.dataset.ratePolicy===selectedRatePolicy));

    confirmationButtons.forEach(button=>
      button.classList.toggle(
        'active',
        button.dataset.confirmationMode===selectedConfirmationMode));
  }

  async function configure(){
    configurationPending=false;
    configurationInFlight++;
    const revision=++configurationRevision;

    const packetBytes=PACKETS[Number(packetSlider.value)];
    const workers=Number(workerSlider.value);
    const packetRateCap=selectedPacketRateCap;
    const receiveMode=selectedReceiveMode;
    const ratePolicy=selectedRatePolicy;
    const nativeSmoothing=selectedNativeSmoothing;
    const confirmationMode=selectedConfirmationMode;

    packetLabel.textContent=packetBytes.toLocaleString()+' bytes';
    workerLabel.textContent=workers.toString();
    state.textContent='reconfiguring…';state.classList.remove('live');

    try{
      const response=await fetch('/api/stress/configure',{
        method:'POST',
        headers:{'Content-Type':'application/json'},
        body:JSON.stringify({
          packetBytes,
          workers,
          receiveMode,
          ratePolicy,
          nativeSmoothing,
          confirmationMode,
          packetRateCap
        })
      });

      if(!response.ok)throw new Error('HTTP '+response.status);

      const applied=await response.json();

      // Ignore completion from an older overlapping configure request.
      if(revision!==configurationRevision)return;

      minimumServerConfigurationVersion=Math.max(
        minimumServerConfigurationVersion,
        Number(applied.configurationVersion||0));

      // Invalidate telemetry requests that may have started while this POST
      // was still applying the previous server configuration.
      configurationRevision++;

      selectedReceiveMode=applied.receiveMode;
      selectedNativeSmoothing=Boolean(applied.nativeSmoothing);
      selectedRatePolicy=applied.ratePolicy;
      selectedConfirmationMode=applied.confirmationMode;
      selectedPacketRateCap=Number(applied.packetRateCap||0);

      renderSelectedControls();

      previous=null;
      recordsHistory.length=0;
      throughputHistory.length=0;
      renderCharts();
    }finally{
      configurationInFlight=Math.max(0,configurationInFlight-1);
    }
  }

  async function poll(){
    const pollRevision=configurationRevision;
    try{
      const response=await fetch('/api/stress/stats',{cache:'no-store'});
      if(!response.ok)throw new Error('HTTP '+response.status);
      const cur=await response.json();

      const canonicalRecordBytes=Number(cur.recordSize||0);
      const canonicalPacketBytes=Number(cur.packetBytes||0);
      const canonicalRecordsPerPacket=Number(cur.recordsPerPacket||0);

      if(
        canonicalRecordBytes!==canonicalPacketBytes ||
        canonicalRecordsPerPacket!==1
      ){
        previous=null;
        state.textContent='benchmark contract mismatch';
        state.classList.remove('live');
        return;
      }

      const packetIndex=PACKETS.indexOf(canonicalRecordBytes);
      if(packetIndex>=0 && document.activeElement!==packetSlider){
        packetSlider.value=String(packetIndex);
      }
      packetLabel.textContent=canonicalRecordBytes.toLocaleString()+' bytes';

      if(
        previous &&
        Number(cur.configurationVersion)!==Number(previous.configurationVersion)
      ){
        previous=null;
        recordsHistory.length=0;
        throughputHistory.length=0;
        renderCharts();
      }

      $('packetBytes').textContent=canonicalPacketBytes.toLocaleString();
      $('recordsPerPacket').textContent=Number(cur.recordsPerPacket).toLocaleString();
      $('activeWorkers').textContent=cur.workers;
      $('logicalProcessors').textContent=cur.logicalProcessors;
      $('receiveMode').textContent=cur.receiveMode;
      $('nativeSmoothing').textContent=cur.nativeSmoothing?'Ring-3 ON':'OFF';
      $('ratePolicy').textContent=cur.ratePolicy;
      $('confirmationMode').textContent=cur.confirmationMode;
      $('packetRateCapOut').textContent=cur.packetRateCap>0?compact(cur.packetRateCap)+'/s':'Unlimited';
      const serverConfigurationVersion=
        Number(cur.configurationVersion||0);

      if(
        !configurationPending &&
        configurationInFlight===0 &&
        pollRevision===configurationRevision &&
        serverConfigurationVersion>=minimumServerConfigurationVersion
      ){
        selectedReceiveMode=cur.receiveMode;
        selectedNativeSmoothing=Boolean(cur.nativeSmoothing);
        selectedRatePolicy=cur.ratePolicy;
        selectedConfirmationMode=cur.confirmationMode;
        selectedPacketRateCap=Number(cur.packetRateCap||0);
        const capIndex=PACKET_RATE_CAPS.indexOf(selectedPacketRateCap);
        if(capIndex>=0){
          packetRateCapSlider.value=String(capIndex);
          packetRateCapLabel.textContent=selectedPacketRateCap>0?compact(selectedPacketRateCap)+'/s':'Unlimited';
        }

        renderSelectedControls();
      }
      $('coreProcessNs').textContent=Number(cur.coreProcessNanosecondsPerPacket||0).toFixed(2)+' ns/packet';
      $('allocProcessor').textContent=Number(cur.processorAllocatedBytesPerPacket||0).toFixed(3)+' B/call';
      $('allocServer').textContent=Number(cur.serverAllocatedBytesPerPacket||0).toFixed(3)+' B/call';
      $('allocClient').textContent=Number(cur.clientAllocatedBytesPerPacket||0).toFixed(3)+' B/call';
      $('allocFullPath').textContent=Number(cur.fullPathAllocatedBytesPerPacket||0).toFixed(3)+' B/call';
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
        const allocatedBytes=Math.max(0,Number(cur.totalAllocatedBytes)-Number(previous.totalAllocatedBytes));
        const allocationRateBytes=allocatedBytes/sec;
        const allocatedPerPacket=allocatedBytes/packetDelta;
        const gen0Delta=Math.max(0,Number(cur.gen0Collections)-Number(previous.gen0Collections));
        const gen1Delta=Math.max(0,Number(cur.gen1Collections)-Number(previous.gen1Collections));
        const gen2Delta=Math.max(0,Number(cur.gen2Collections)-Number(previous.gen2Collections));

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
        $('allocatedPerPacket').textContent=allocatedPerPacket.toFixed(3)+' B/packet';
        $('allocationRate').textContent=(allocationRateBytes/1e6).toFixed(2)+' MB/s';
        $('gcCollections').textContent=gen0Delta+' / '+gen1Delta+' / '+gen2Delta;

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
    configurationPending=true;
    clearTimeout(configureTimer);
    configureTimer=setTimeout(()=>{
      configureTimer=0;
      configure().catch(()=>{
        configurationPending=false;
        state.textContent='configuration failed';
      });
    },180);
  }

  packetSlider.addEventListener('input',()=>{packetLabel.textContent=PACKETS[Number(packetSlider.value)].toLocaleString()+' bytes';queueConfigure()});
  workerSlider.addEventListener('input',()=>{workerLabel.textContent=workerSlider.value;queueConfigure()});
  packetRateCapSlider.addEventListener('input',()=>{
    selectedPacketRateCap=
      PACKET_RATE_CAPS[Number(packetRateCapSlider.value)];

    packetRateCapLabel.textContent=
      selectedPacketRateCap>0
        ? compact(selectedPacketRateCap)+'/s'
        : 'Unlimited';

    // The explicit hard cap is the only limiter for this test.
    selectedRatePolicy='Unlimited';

    rateButtons.forEach(button=>
      button.classList.toggle(
        'active',
        button.dataset.ratePolicy==='Unlimited'));

    queueConfigure();
  });
  receiveButtons.forEach(button=>button.addEventListener('click',()=>{
    selectedReceiveMode=button.dataset.receiveMode;
    selectedNativeSmoothing=button.dataset.nativeSmoothing==='true';
    renderSelectedControls();
    queueConfigure();
  }));
  rateButtons.forEach(button=>button.addEventListener('click',()=>{
    selectedRatePolicy=button.dataset.ratePolicy;
    renderSelectedControls();
    queueConfigure();
  }));
  confirmationButtons.forEach(button=>button.addEventListener('click',()=>{
    selectedConfirmationMode=button.dataset.confirmationMode;
    if(selectedConfirmationMode!=='None'){
      selectedReceiveMode='Sequential';
      selectedNativeSmoothing=false;
      receiveButtons.forEach(item=>item.classList.toggle('active',item.dataset.receiveMode==='Sequential'&&item.dataset.nativeSmoothing!=='true'));
    }
    renderSelectedControls();
    queueConfigure();
  }));
  window.addEventListener('resize',renderCharts);

  // Do not trust browser-restored range positions before server state arrives.
  packetRateCapSlider.value='0';
  packetRateCapLabel.textContent='Unlimited';

  poll();
  setInterval(poll,1000);
})();