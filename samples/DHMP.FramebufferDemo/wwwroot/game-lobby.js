(() => {
  const PLAYERS=[1000,5000,10000,20000,40000,75000,100000,250000];
  const TICKS=[10,20,30,45,60];
  const SIZES=[16,32,64,128,256,512];
  const INTEREST=[16,32,64,128,256,512,'ALL'];
  const $=id=>document.getElementById(id);

  let liveRecordsPerSecond=0;
  let liveGbPerSecond=0;
  let lobbyMode='Latest';
  let scenario='interest';

  const players=$('players');
  const tickRate=$('tickRate');
  const stateSize=$('stateSize');
  const interest=$('interest');

  function compact(value){
    if(value>=1e12)return (value/1e12).toFixed(2)+'T';
    if(value>=1e9)return (value/1e9).toFixed(2)+'B';
    if(value>=1e6)return (value/1e6).toFixed(2)+'M';
    if(value>=1e3)return (value/1e3).toFixed(1)+'K';
    return Math.round(value).toLocaleString();
  }

  function fixed(value,digits=2){
    if(!Number.isFinite(value))return '—';
    return value.toFixed(digits);
  }

  function current(){
    const playerCount=PLAYERS[Number(players.value)];
    const hz=TICKS[Number(tickRate.value)];
    const bytes=SIZES[Number(stateSize.value)];
    const rawInterest=INTEREST[Number(interest.value)];
    const peerCount=scenario==='all'||rawInterest==='ALL'
      ? Math.max(0,playerCount-1)
      : Math.min(Number(rawInterest),Math.max(0,playerCount-1));
    return {playerCount,hz,bytes,rawInterest,peerCount};
  }

  function render(){
    const {playerCount,hz,bytes,rawInterest,peerCount}=current();

    $('playersLabel').textContent=playerCount.toLocaleString();
    $('tickLabel').textContent=hz+' Hz';
    $('stateSizeLabel').textContent=bytes+' bytes';
    $('interestLabel').textContent=(scenario==='all'||rawInterest==='ALL')?'ALL players':peerCount.toLocaleString()+' players';

    const inboundRecords=playerCount*hz;
    const outboundRecords=playerCount*peerCount*hz;
    const totalRecords=inboundRecords+outboundRecords;

    const ingressBytes=inboundRecords*bytes;
    const fanoutBytes=outboundRecords*bytes;
    const wireBits=(ingressBytes+fanoutBytes)*8;

    const processingMultiplier=lobbyMode==='Latest' ? 1 : 1;
    const requiredProcessing=totalRecords*processingMultiplier;
    const capacityUse=liveRecordsPerSecond>0 ? requiredProcessing/liveRecordsPerSecond*100 : 0;
    const headroom=requiredProcessing>0&&liveRecordsPerSecond>0 ? liveRecordsPerSecond/requiredProcessing : 0;

    $('ingressGb').textContent=fixed(ingressBytes/1e9);
    $('fanoutGb').textContent=fixed(fanoutBytes/1e9);
    $('updatesRate').textContent=compact(totalRecords);
    $('wireGbps').textContent=fixed(wireBits/1e9);

    $('incomingRecords').textContent=compact(inboundRecords);
    $('outgoingRecords').textContent=compact(outboundRecords);
    $('totalRecords').textContent=compact(requiredProcessing);
    $('headroom').textContent=headroom>0 ? headroom.toFixed(headroom<10?2:1)+'×' : '—';

    $('playersOut').textContent=playerCount.toLocaleString();
    $('tickOut').textContent=hz;
    $('interestOut').textContent=(scenario==='all'||rawInterest==='ALL')?'ALL':peerCount.toLocaleString();
    $('stateOut').textContent=bytes;
    $('scenarioTitle').textContent=playerCount.toLocaleString()+'-player '+(scenario==='all'?'all-to-all':'interest-managed')+' lobby';

    const clamped=Math.max(0,Math.min(100,capacityUse));
    $('capacityUse').textContent=capacityUse>999?'999%+':fixed(capacityUse,2)+'%';
    $('capacityUseBar').textContent=capacityUse>999?'999%+':fixed(capacityUse,2)+'%';
    $('capacityFill').style.width=clamped+'%';

    $('liveCeilingRecords').textContent=liveRecordsPerSecond?compact(liveRecordsPerSecond):'—';
    $('liveCeilingGb').textContent=liveGbPerSecond?fixed(liveGbPerSecond,2):'—';

    document.querySelectorAll('[data-lobby-mode]').forEach(button=>
      button.classList.toggle('active',button.dataset.lobbyMode===lobbyMode));
    document.querySelectorAll('[data-scenario]').forEach(button=>
      button.classList.toggle('active',button.dataset.scenario===scenario));

    interest.disabled=scenario==='all';
  }

  async function pollCeiling(){
    try{
      const response=await fetch('/api/stress/stats',{cache:'no-store'});
      if(!response.ok)throw new Error('HTTP '+response.status);
      const current=await response.json();

      if(pollCeiling.previous){
        const previous=pollCeiling.previous;
        const sec=Math.max(.001,(current.uptimeMilliseconds-previous.uptimeMilliseconds)/1000);
        liveRecordsPerSecond=Math.max(0,(current.recordsSubmitted-previous.recordsSubmitted)/sec);
        liveGbPerSecond=Math.max(0,(current.bytesSubmitted-previous.bytesSubmitted)/sec/1e9);
      }

      pollCeiling.previous=current;
      $('lobbyState').textContent='live DHMP ceiling connected';
      $('lobbyState').classList.add('live');
      render();
    }catch{
      $('lobbyState').textContent='live ceiling unavailable';
      $('lobbyState').classList.remove('live');
    }
  }

  [players,tickRate,stateSize,interest].forEach(input=>input.addEventListener('input',render));

  document.querySelectorAll('[data-lobby-mode]').forEach(button=>button.addEventListener('click',()=>{
    lobbyMode=button.dataset.lobbyMode;
    render();
  }));

  document.querySelectorAll('[data-scenario]').forEach(button=>button.addEventListener('click',()=>{
    scenario=button.dataset.scenario;
    render();
  }));

  render();
  pollCeiling();
  setInterval(pollCeiling,1000);
})();