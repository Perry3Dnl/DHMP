(() => {
  const receiveButtons=[...document.querySelectorAll('[data-receive]')];
  const incoming=document.getElementById('incomingRecords');
  const published=document.getElementById('publishedRecords');
  const receiveCopy=document.getElementById('receiveCopy');
  const receiveOut=document.getElementById('receiveOut');
  const receiveSkipped=document.getElementById('receiveSkipped');

  function makeRecord(n, className=''){
    const el=document.createElement('span');
    el.className='record '+className;
    el.textContent='R'+n;
    return el;
  }

  function setReceive(mode){
    receiveButtons.forEach(b=>b.classList.toggle('active',b.dataset.receive===mode));
    incoming.replaceChildren(...[1,2,3,4,5,6].map(n=>makeRecord(n,mode==='latest'&&n<6?'dim':'')));
    if(mode==='latest'){
      published.replaceChildren(makeRecord(6,'hot'));
      receiveCopy.textContent='Latest publishes only the last complete record from each packet. Intermediate records in that packet are obsolete by design.';
      receiveOut.textContent='1';receiveSkipped.textContent='5';
    }else{
      published.replaceChildren(...[1,2,3,4,5,6].map(n=>makeRecord(n,'hot')));
      receiveCopy.textContent='Sequential publishes every complete record from the packet in arrival order. It still does not add a network delivery guarantee.';
      receiveOut.textContent='6';receiveSkipped.textContent='0';
    }
  }
  receiveButtons.forEach(b=>b.addEventListener('click',()=>setReceive(b.dataset.receive)));
  setReceive('latest');

  function restartAnimation(el,className){
    el.classList.remove(className);
    void el.offsetWidth;
    el.classList.add(className);
  }

  const fafButton=document.getElementById('fafButton');
  const fafPacket=document.getElementById('fafPacket');
  const fafStatus=document.getElementById('fafStatus');
  fafButton.addEventListener('click',()=>{
    fafStatus.textContent='Local backend accepted record → SendAsync completed.';
    restartAnimation(fafPacket,'fly-right');
  });

  const confirmedButton=document.getElementById('confirmedButton');
  const confirmedPacket=document.getElementById('confirmedPacket');
  const confirmAck=document.getElementById('confirmAck');
  const confirmedStatus=document.getElementById('confirmedStatus');
  confirmedButton.addEventListener('click',()=>{
    confirmedStatus.textContent='Record submitted… waiting for application-owned confirmation ID.';
    restartAnimation(confirmedPacket,'fly-right');
    setTimeout(()=>{
      restartAnimation(confirmAck,'fly-left');
      confirmedStatus.textContent='Confirmation observed → send completed. No retransmission was required.';
    },720);
  });

  const blindButton=document.getElementById('blindButton');
  const blindPacket=document.getElementById('blindPacket');
  const blindStatus=document.getElementById('blindStatus');
  const iotDevice=document.getElementById('iotDevice');
  blindButton.addEventListener('click',()=>{
    iotDevice.classList.add('awake');
    iotDevice.innerHTML='sensor<br><small>awake</small>';
    blindStatus.textContent='Boot → IPv6 ready → emit exactly one plaintext fixed record.';
    restartAnimation(blindPacket,'blind-fly');
    setTimeout(()=>{
      iotDevice.classList.remove('awake');
      iotDevice.innerHTML='sensor<br><small>sleeping</small>';
      blindStatus.textContent='Sender disposed → sensor sleeping. No handshake or confirmation.';
    },650);
  });

  function runSmooth(){
    const line=document.getElementById('smoothTimeline');
    line.replaceChildren();
    for(let i=0;i<10;i++){
      const tick=document.createElement('span');
      tick.className='tick';
      tick.style.left=(5+i*10)+'%';
      tick.style.animation='fadein .2s '+(i*.08)+'s both';
      line.appendChild(tick);
    }
  }

  function runReject(){
    const line=document.getElementById('rejectTimeline');
    line.replaceChildren();
    for(let i=0;i<14;i++){
      const tick=document.createElement('span');
      tick.className='tick'+(i>=10?' rejected':'');
      tick.style.left=(4+i*7)+'%';
      tick.title=i>=10?'Rejected: Pmax window exhausted':'Accepted';
      line.appendChild(tick);
    }
  }

  document.getElementById('smoothButton').addEventListener('click',runSmooth);
  document.getElementById('rejectButton').addEventListener('click',runReject);
  runSmooth();runReject();
})();