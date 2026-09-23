import subprocess,csv,statistics

paths=['dhmp','raw','len4','ws','http','udp','dhmps']
orders=[
    paths,
    list(reversed(paths)),
    ['raw','len4','dhmp','http','dhmps','ws','udp'],
]
rows=[]

for run,order in enumerate(orders,1):
    for p in order:
        cmd=['./showcase_v3',p,'1.5']
        if p=='dhmps':
            cmd += ['cert.pem','key.pem']
        cp=subprocess.run(cmd,text=True,capture_output=True,timeout=10)
        print(run,p,cp.stdout.strip(),flush=True)
        if cp.returncode != 0:
            print(cp.stderr,flush=True)
            raise SystemExit(cp.returncode)
        d={kv.split('=',1)[0]:kv.split('=',1)[1]
           for kv in cp.stdout.strip().split() if '=' in kv}
        row={'run':run,'path':p}
        for k in ['seconds','input_fps','published_fps','payload_GBps',
                  'recv_ops_s','receiver_cpu_ns_frame','skipped_pct']:
            row[k]=float(d[k])
        for k in ['producer_pubs','consumer_retries',
                  'validation_errors','framing_errors']:
            row[k]=int(d[k])
        rows.append(row)

raw='../results/showcase-v3-32b-raw-3run-2026-09-23.csv'
with open(raw,'w',newline='') as f:
    w=csv.DictWriter(f,fieldnames=rows[0].keys())
    w.writeheader()
    w.writerows(rows)

summary=[]
for p in paths:
    rr=[r for r in rows if r['path']==p]
    s={'path':p}
    for k in ['input_fps','published_fps','payload_GBps','recv_ops_s',
              'receiver_cpu_ns_frame','skipped_pct']:
        s[k]=statistics.median(r[k] for r in rr)
    s['min_input_fps']=min(r['input_fps'] for r in rr)
    s['max_input_fps']=max(r['input_fps'] for r in rr)
    s['validation_errors']=sum(r['validation_errors'] for r in rr)
    s['framing_errors']=sum(r['framing_errors'] for r in rr)
    summary.append(s)

sumfile='../results/showcase-v3-32b-summary-3run-2026-09-23.csv'
with open(sumfile,'w',newline='') as f:
    w=csv.DictWriter(f,fieldnames=summary[0].keys())
    w.writeheader()
    w.writerows(summary)

print('SUMMARY',flush=True)
for s in summary:
    print(s,flush=True)
