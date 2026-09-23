import subprocess,csv,statistics,time

BIN='./showcase_v5_fair'
paths=['dhmp','raw','varint','len4','ws','http1','http2','grpc','mqtt','nats']
orders=[]
for r in range(7):
    o=paths[r:]+paths[:r]
    if r%2: o=list(reversed(o))
    orders.append(o)

rows=[]
for run,o in enumerate(orders,1):
    for p in o:
        cp=subprocess.run([BIN,'--micro',p,'100000000'],text=True,capture_output=True,timeout=5)
        if cp.returncode:
            print('FAIL',run,p,cp.stdout,cp.stderr)
            raise SystemExit(cp.returncode)
        line=cp.stdout.strip().splitlines()[-1]
        print(run,p,line,flush=True)
        d={x.split('=',1)[0]:x.split('=',1)[1] for x in line.split() if '=' in x}
        rows.append({
          'run':run,'path':p,'frames':int(d['frames']),'cpu_s':float(d['cpu_s']),
          'cpu_ns_frame':float(d['cpu_ns_frame']),'fps_cpu':float(d['fps_cpu']),
          'payload_GBps_cpu':float(d['payload_GBps_cpu']),
          'framing_errors':int(d['framing_errors']),
          'payload_sequence_errors':int(d['payload_sequence_errors']),
          'static_wire_bytes_per_record':float(d['static_wire_bytes_per_record'])
        })
        time.sleep(.01)

with open('../results/showcase-v5-parser-micro-raw-7run-2026-09-23.csv','w',newline='') as f:
    w=csv.DictWriter(f,fieldnames=rows[0].keys()); w.writeheader(); w.writerows(rows)

out=[]
for p in paths:
    rs=[r for r in rows if r['path']==p]
    med=lambda k:statistics.median(r[k] for r in rs)
    out.append({
      'path':p,
      'median_cpu_ns_frame':med('cpu_ns_frame'),
      'min_cpu_ns_frame':min(r['cpu_ns_frame'] for r in rs),
      'max_cpu_ns_frame':max(r['cpu_ns_frame'] for r in rs),
      'median_fps_cpu':med('fps_cpu'),
      'median_payload_GBps_cpu':med('payload_GBps_cpu'),
      'static_wire_bytes_per_record':med('static_wire_bytes_per_record'),
      'framing_errors':sum(r['framing_errors'] for r in rs),
      'payload_sequence_errors':sum(r['payload_sequence_errors'] for r in rs)
    })

with open('../results/showcase-v5-parser-micro-summary-7run-2026-09-23.csv','w',newline='') as f:
    w=csv.DictWriter(f,fieldnames=out[0].keys()); w.writeheader(); w.writerows(out)
