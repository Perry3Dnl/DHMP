import csv, subprocess, time, statistics

BIN='./showcase_v5_fair'
paths=['dhmp','raw','varint','len4','ws','http1','http2','grpc','mqtt','nats','udp']
orders=[
 paths,
 list(reversed(paths)),
 ['raw','http2','mqtt','dhmp','grpc','len4','nats','ws','varint','http1','udp'],
 ['udp','http1','varint','ws','nats','len4','grpc','dhmp','mqtt','http2','raw'],
 ['grpc','dhmp','http1','raw','nats','http2','varint','mqtt','ws','udp','len4'],
]
floats=['seconds','input_fps','payload_GBps','measured_wire_GBps','static_wire_bytes_per_record','recv_ops_s','receiver_cpu_ns_frame','published_fps']
ints=['producer_pubs','consumer_retries','framing_errors','payload_sequence_errors','consumer_validation_errors','udp_payload_errors','config_errors','sndbuf','rcvbuf']
rows=[]
for run,order in enumerate(orders,1):
    print('RUN',run,flush=True)
    for p in order:
        cp=subprocess.run([BIN,p,'1.0'],text=True,capture_output=True,timeout=4)
        if cp.returncode!=0:
            print('FAIL',run,p,cp.stdout,cp.stderr,flush=True)
            raise SystemExit(cp.returncode)
        line=cp.stdout.strip().splitlines()[-1]
        print(run,p,line,flush=True)
        d={kv.split('=',1)[0]:kv.split('=',1)[1] for kv in line.split() if '=' in kv}
        row={'run':run,'path':p}
        for k in floats: row[k]=float(d[k])
        for k in ints: row[k]=int(d[k])
        rows.append(row)
        time.sleep(.02)

raw='../results/showcase-v5-fair-32b-raw-5run-2026-09-23.csv'
with open(raw,'w',newline='') as f:
    w=csv.DictWriter(f,fieldnames=rows[0].keys()); w.writeheader(); w.writerows(rows)

summary=[]
for p in paths:
    rs=[x for x in rows if x['path']==p]
    med=lambda k:statistics.median(x[k] for x in rs)
    summary.append({
      'path':p,
      'median_input_fps':med('input_fps'),
      'min_input_fps':min(x['input_fps'] for x in rs),
      'max_input_fps':max(x['input_fps'] for x in rs),
      'median_payload_GBps':med('payload_GBps'),
      'median_wire_GBps':med('measured_wire_GBps'),
      'static_wire_bytes_per_record':med('static_wire_bytes_per_record'),
      'median_receiver_cpu_ns_frame':med('receiver_cpu_ns_frame'),
      'min_receiver_cpu_ns_frame':min(x['receiver_cpu_ns_frame'] for x in rs),
      'max_receiver_cpu_ns_frame':max(x['receiver_cpu_ns_frame'] for x in rs),
      'median_recv_ops_s':med('recv_ops_s'),
      'median_published_fps':med('published_fps'),
      'framing_errors':sum(x['framing_errors'] for x in rs),
      'payload_sequence_errors':sum(x['payload_sequence_errors'] for x in rs),
      'consumer_validation_errors':sum(x['consumer_validation_errors'] for x in rs),
      'udp_payload_errors':sum(x['udp_payload_errors'] for x in rs),
      'config_errors':sum(x['config_errors'] for x in rs)
    })

out='../results/showcase-v5-fair-32b-summary-5run-2026-09-23.csv'
with open(out,'w',newline='') as f:
    w=csv.DictWriter(f,fieldnames=summary[0].keys()); w.writeheader(); w.writerows(summary)
