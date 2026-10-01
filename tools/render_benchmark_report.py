#!/usr/bin/env python3
"""Render committed measurement JSON into a README section, detailed tables and SVG figures."""
import json
from pathlib import Path
import statistics
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt

root = Path(__file__).resolve().parents[1]
folder = root / 'docs/benchmark-results/2026-10-01'
components = json.loads((folder / 'components-kernel.json').read_text())
network = json.loads((folder / 'network.json').read_text())
initial = json.loads((folder / 'initial-components-kernel.json').read_text())
validation = json.loads((folder / 'validation.json').read_text())
bench_url = f"https://github.com/Perry3Dnl/DHMP/actions/runs/{components['run']}"
rows = {row['name']: row for row in components['summary']}

def metric(name, field): return rows[name]['metrics'][field]
def net(name, field):
    values = [row[field] for row in network['api_benchmarks'] + network['echo_benchmarks'] if row['name'] == name]
    return dict(median=statistics.median(values), min=min(values), max=max(values))
def fmt(value, decimals=3): return f'{value:,.{decimals}f}'
def interval(m, scale=1, decimals=3):
    return f"{fmt(m['median']*scale, decimals)} ({fmt(m['min']*scale, decimals)}–{fmt(m['max']*scale, decimals)})"

assets = root / 'docs/assets'; assets.mkdir(parents=True, exist_ok=True)
plt.rcParams.update({'font.family':'DejaVu Sans','font.size':11,'svg.fonttype':'none','svg.hashsalt':'dhmp-2026-10-01'})
def figure(labels, measures, title, subtitle, target):
    medians = [m['median'] for m in measures]
    errors = [[m['median']-m['min'] for m in measures],[m['max']-m['median'] for m in measures]]
    fig, ax = plt.subplots(figsize=(9, 5.1))
    fig.subplots_adjust(left=.24, right=.96, top=.78, bottom=.23)
    fig.set_facecolor('#fafbfc'); ax.set_facecolor('#fafbfc')
    bars = ax.barh(labels, medians, xerr=errors, capsize=5, color=['#2383a8','#485ca6','#408c79'][:len(labels)])
    ax.invert_yaxis(); ax.set_xlim(0, max(m['max'] for m in measures)*1.25)
    ax.set_xlabel('Useful application MB/s (decimal) — higher is faster')
    ax.set_title(title, loc='left', fontsize=15, fontweight='bold', pad=32)
    ax.text(0,1.04,subtitle,transform=ax.transAxes,fontsize=9,color='#465364')
    ax.grid(axis='x',alpha=.2); ax.set_axisbelow(True)
    for spine in ax.spines.values(): spine.set_visible(False)
    for bar, m in zip(bars, measures):
        ax.text(m['max']+max(medians)*.025,bar.get_y()+bar.get_height()/2,f"{m['median']:.1f}",va='center',fontweight='bold')
    fig.text(.025,.035,'Median of 5 repetitions; error bars show full min–max range. No physical-NIC speed claim.',fontsize=9,color='#465364')
    fig.savefig(assets / target, metadata={'Date':None}); plt.close(fig)

memory_names = ['protected-one-way-record-1200','protected-full-echo-record-1200']
figure(['Protected one-way','Protected full echo'],
    [{k:v*1000 for k,v in metric(name,'consumed_GBps').items()} for name in memory_names],
    'Optional echo: measured processing cost','Memory composition • 1200-byte records • 1160 useful bytes • authenticated packet protection', 'full-suite-memory.svg')
figure(['1 outstanding send','8 outstanding sends','32 outstanding sends'],
    [net(f'raw-protected-echo-concurrency-{c}','useful_MBps') for c in (1,8,32)],
    'Protected full echo over actual raw IPv6','Two namespaces on one hosted VM • MTU 1280 • at least 0.5 seconds per repetition', 'full-suite-raw-echo.svg')

cpu = next(line.split(':',1)[1].strip() for line in components['cpu'].splitlines() if line.startswith('Model name:'))
ratio = metric(memory_names[1],'ns_per_operation')['median']/metric(memory_names[0],'ns_per_operation')['median']
readme = f'''<!-- BEGIN FULL BENCHMARK RESULTS -->
## Latest validation and benchmarks — 1 October 2026

**{validation['total_executions']} test executions passed: {validation['tests_per_os']} cases on Linux and Windows.** The full hosted suite also passed {len(network['checks'])} network acceptance checks, including deliberate loss, restart, clean shutdown and the optional full-echo profile.

[Benchmark run]({bench_url}) · [Test run]({validation['url']}) · [Full results and contracts](docs/FULL_BENCHMARK_2026_10_01.md) · [Raw measurement JSON](docs/benchmark-results/2026-10-01)

Five repetitions per case on one GitHub-hosted VM: **4 vCPU, {cpu}, .NET 10**. All units are decimal. These are memory, kernel-loopback and virtual-network measurements; physical two-machine/NIC throughput remains unmeasured.

### Protected one-way versus optional full echo: memory composition

Both cases use 1200-byte records and 1160 useful application bytes. Timing includes real protection/decryption and byte checks, with no sockets. Echo also sends the entire return record and matches its confirmation. Useful bytes are counted once.

| Mode | Useful MB/s, median (min–max) | Time per send/confirmation | Approx. allocated bytes/op |
| --- | ---: | ---: | ---: |
| Protected one-way | {interval(metric(memory_names[0],'consumed_GBps'),1000,1)} | {fmt(metric(memory_names[0],'ns_per_operation')['median']/1000,2)} µs | {fmt(metric(memory_names[0],'allocated_bytes_per_operation')['median'],0)} |
| Protected full echo | {interval(metric(memory_names[1],'consumed_GBps'),1000,1)} | {fmt(metric(memory_names[1],'ns_per_operation')['median']/1000,2)} µs | {fmt(metric(memory_names[1],'allocated_bytes_per_operation')['median'],0)} |

![Protected one-way versus full echo in memory](docs/assets/full-suite-memory.svg)

Full echo took **{ratio:.2f}×** the CPU time per operation in this harness. It remains opt-in; neither mode adds automatic retransmission.

### Full echo through actual protected raw IPv6 sockets

These peers run in two namespaces on the **same VM**, not on separate physical machines. Records are encrypted and their returned content is checked. Each sample lasts at least 0.5 seconds after warm-up, with rotated concurrency order. This is measured useful completion throughput, not a physical link capacity.

| Outstanding sends | Useful MB/s, median (min–max) | Median sample p50 RTT | Median sample p95 RTT |
| ---: | ---: | ---: | ---: |
'''
for c in (1,8,32):
    name=f'raw-protected-echo-concurrency-{c}'
    readme+=f"| {c} | {interval(net(name,'useful_MBps'),decimals=1)} | {fmt(net(name,'p50_ms')['median'])} ms | {fmt(net(name,'p95_ms')['median'])} ms |\n"
readme+='\n![Full echo through protected raw IPv6](docs/assets/full-suite-raw-echo.svg)\n'
readme+='''
### Existing ASP.NET API path

The local HTTP driver calls the website, which calls its backend through protected raw DHMP. Measurements include that whole application path. The fixture intentionally caps each API sender at **500 records/s**; the 16 KB POST spans many records and therefore reflects configured pacing, not maximum protocol bandwidth. Five samples of 40 successful requests per route, after warm-up.

| API call | Requests/s, median | Median sample p50 | Median sample p95 |
| --- | ---: | ---: | ---: |
'''
for name,label in [('api-get','GET'),('api-json-post','16 KB JSON POST + echo response')]:
    readme+=f"| {label} | {fmt(net(name,'requests_per_second')['median'],1)} | {fmt(net(name,'p50_ms')['median'])} ms | {fmt(net(name,'p95_ms')['median'])} ms |\n"
readme+='''
### Unprotected raw kernel loopback: verified delivery

Every delivered record's identity and bytes are checked. Each run offers 200,000 records of 32 bytes, without intentional pacing. Unique received payload and loss are reported separately; duplicate records never inflate throughput. Drain waiting is excluded from the active-rate denominator.

| Records/packet | Unique received MB/s, median (min–max) | Loss %, median (min–max) |
| ---: | ---: | ---: |
'''
for batch in (1,8,44):
    name=f'kernel-loopback-batch-{batch}'
    readme+=f"| {batch} | {interval(metric(name,'consumed_GBps'),1000,1)} | {interval(metric(name,'loss_pct'),decimals=2)} |\n"
loss=metric('kernel-loopback-batch-44','loss_pct')['median']
initial_kernel = next(row['metrics'] for row in initial['summary'] if row['name'] == 'kernel-loopback-batch-44')
initial_loss = initial_kernel['loss_pct']
readme+=f'''
**The primary run had no observed kernel loss. An earlier run on a different hosted CPU lost {initial_loss["median"]:.2f}% at batch 44 ({initial_loss["min"]:.2f}–{initial_loss["max"]:.2f}%).** Both sets of raw measurements are retained. This shows that unpaced sender/receiver/kernel overload can occur; a loss-free run does not establish reliable delivery. The protected echo and API measurements above are different workloads and cannot be ranked against this kernel test as equivalent modes.

Core, mock IPv4/IPv6, control-codec, generation-filter, security/feedback and key-derivation timings are retained in the [complete report](docs/FULL_BENCHMARK_2026_10_01.md). Large logical offered rates from the historical-contract memory harnesses are not network bandwidth. License, routing, replay, limits, cancellation and lifetime behavior are covered by tests; long-duration soak, physical networks, fairness and independent security review remain open.
<!-- END FULL BENCHMARK RESULTS -->
'''
p=root/'README.md';text=p.read_text()
if '<!-- BEGIN FULL BENCHMARK RESULTS -->' in text:
    before, rest=text.split('<!-- BEGIN FULL BENCHMARK RESULTS -->',1)
    _, after=rest.split('<!-- END FULL BENCHMARK RESULTS -->',1)
    text=before+readme.rstrip()+after
else:
    position=text.index('## V1 data-plane rule')
    text=text[:position]+readme+'\n'+text[position:]
p.write_text(text)

report=f'''# Full DHMP benchmark — 1 October 2026

Measured source commit: `{components['commit']}` (GitHub's tested merge commit). [Run]({bench_url}). Kernel: `{components['kernel']}`. CPU: **{cpu}**, 4 vCPU. The stored JSON includes the complete reported CPU description. Benchmark programs target .NET 10 in Release. Raw samples are retained under [benchmark-results/2026-10-01](benchmark-results/2026-10-01).

The README presents selected results; the tables below retain every measured configuration. Median and full min–max ranges come from five repetitions. No TCP/UDP comparison, independent security certification or physical two-host pass is claimed.

## Correctness coverage

[Test run]({validation['url']}): {validation['total_executions']} executions, all passed. Each case runs once per OS; Linux and Windows counts are repetitions across platforms, not distinct tests. Native Linux successes are exercised by the privileged labs; ordinary Windows tests do not establish a Windows raw-IP backend. Some platform guard tests return on their inapplicable OS rather than using xUnit skip markers.

| Suite | Linux cases passed | Windows cases passed |
| --- | ---: | ---: |
'''
for name,count in validation['suites'].items():report+=f'| {name} | {count} | {count} |\n'
report+=f'''
The {len(network['checks'])} hosted network checks passed. Capture recorded {network['packet_counts']['data_253']:,} data packets (253), {network['packet_counts']['control_254']} setup packets (254), and zero inter-peer TCP/UDP packets. Capture snap length is {network['capture_snaplen']} bytes, so packet contents are truncated; application content is verified separately in the receiver. Configs, PSKs and issuer private keys are excluded from evidence.

## Measurement contracts

- Component cases: 100 ms warm-up per case, five rotated repetitions of at least 500 ms. Core cases inspect every byte returned by the processor using a scalar byte-summing consumer. Latest consumes only the last 32-byte record; Sequential consumes the whole batch. The consumer dominates these timings; they are not the isolated framing-core ceiling.
- Protection cases: complete encrypt/decrypt and byte equality, fresh packet counters. The one-way and echo composition cases both use 1200-byte records with 1160 useful bytes. Echo counts useful bytes once and performs a protected return path. These are memory peers, not sockets. Approximate allocations use process-wide GC allocation deltas; small background/JIT allocations may be included.
- The generation filter reads its application generation and an observation byte; its reported time is an operation cost, not full-body processing throughput. Control/feedback cases encode, authenticate where applicable, decode and compare fields. Session construction includes HKDF/crypto setup/disposal and fresh Guid creation; it is not the network handshake latency.
- Historical-contract mocks: 2 million-message warm-up, 100 million offered 32-byte messages, five rotated process runs. They read the newest record's identity, not every offered byte, and exclude real sockets. Mock batches 128/512 exceed the real network packet budget and are memory-only. The old core sanity sink only observes one int32 from the newest record, explaining its large logical offered number.
- Kernel loopback: unprotected raw IPv6, batches 1/8/44, 200,000 offered records per run, five rotated repetitions. All bytes/identities are checked before unique records are counted; duplicates, loss and reordering are explicit. Rates use send-to-last-arrival active time and exclude final drain waiting. Increasing buffer requests is subject to Linux's actual socket buffer limits. This is one VM's kernel, with no physical NIC and no deliberate pacing.
- API: warm-up 5 calls per route, 5 × 40 calls, one request at a time. Local HTTP website driving plus the protected raw backend round trip, including JSON serialization, application handlers and both sides' configured 500-record/s pacing. It is not an unconstrained API saturation test.
- Raw DECO/1: 1200-byte records / 1160 useful bytes, protected in both directions, MTU 1280. Warm-up 64 confirmations; 5 rotated repetitions at concurrency 1/8/32, each at least 0.5 seconds and 32 groups. Every request content is verified; confirmation requires matching returned bytes. No intentional pacing below the client's effectively unlimited local RejectWindow budget. RTT is measured at send admission, including local queuing. p50 is a true sample median; p95 uses nearest rank. Tables show median per-sample percentiles, not pooled percentiles.

CPU and workload differences prevent comparison with older runs as proof of regression/improvement. A throughput ratio from the memory composition does not predict NIC throughput or API pacing cost. The kernel loss result is a measured overload symptom, not a precise diagnosis of which kernel queue drops packets.

## Component results

| Case | ns/op median (min–max) | Offered GB/s median | Consumed GB/s median | Approx. allocated bytes/op median |
| --- | ---: | ---: | ---: | ---: |
'''
for row in components['summary']:
    if not row['scope'].startswith('memory-'):continue
    m=row['metrics'];report+=f"| {row['name']} | {interval(m['ns_per_operation'],decimals=2)} | {fmt(m['offered_GBps']['median'])} | {fmt(m['consumed_GBps']['median'])} | {fmt(m['allocated_bytes_per_operation']['median'],2)} |\n"
report+='''
## Historical-contract memory measurements

**Logical offered GB/s only. These are not end-to-end or physical-network bandwidth.**

| Harness/configuration | Logical offered GB/s median (min–max) | ns/packet median |
| --- | ---: | ---: |
'''
for row in components['summary']:
    if not row['scope'].startswith('historical-'):continue
    m=row['metrics'];report+=f"| {row['name']} | {interval(m['offered_GBps'])} | {fmt(m['ns_per_operation']['median'])} |\n"
report+='''
## Kernel loopback

| Batch | Received MB/s median (min–max) | Loss % median (min–max) | Duplicates max | Reordered max | Invalid packets max |
| ---: | ---: | ---: | ---: | ---: | ---: |
'''
for batch in (1,8,44):
    m=rows[f'kernel-loopback-batch-{batch}']['metrics'];report+=f"| {batch} | {interval(m['consumed_GBps'],1000,1)} | {interval(m['loss_pct'],decimals=2)} | {m['duplicates']['max']} | {m['reordered']['max']} | {m['invalid']['max']} |\n"
report+='''
## Earlier hosted run: retain overload evidence

An initial run on **Intel Xeon Platinum 8573C, 4 vCPU** used the same kernel accounting harness. [Run](https://github.com/Perry3Dnl/DHMP/actions/runs/36831931127), source `5ecd5fb871b4ecae93f2967bdd1080907966c2a8`. Its component and kernel raw samples are retained in `initial-components-kernel.json`. Its preliminary very short network-echo timing is superseded by the longer primary run and is not used as a performance headline. Host/CPU differences prevent combining these distributions into one result.

Batch 44 uniquely received **164.8 MB/s median**, but lost **17.23% median (15.80–19.58%)** of offered records. Batches 1 and 8 had no observed loss. Duplicate, reordered and invalid counts were zero. This is important overload evidence even though the primary AMD-hosted run had no observed loss. The source change between runs increased echo sample duration and corrected API median calculation; it did not change kernel benchmark code.

## API and protected raw echo

| Case | Completions/s or useful MB/s median (min–max) | p50 ms median (min–max) | p95 ms median (min–max) |
| --- | ---: | ---: | ---: |
'''
for name in ['api-get','api-json-post']+[f'raw-protected-echo-concurrency-{c}' for c in (1,8,32)]:
    unit='requests_per_second' if name.startswith('api-') else 'useful_MBps'
    report+=f"| {name} | {interval(net(name,unit),decimals=2)} {'requests/s' if name.startswith('api-') else 'MB/s'} | {interval(net(name,'p50_ms'))} | {interval(net(name,'p95_ms'))} |\n"
report+='''
## Reproduce and remaining gaps

Run the `Full DHMP benchmark suite` workflow, or build the benchmark projects in Release and run `tools/run_full_benchmarks.py` followed by the privileged `tools/run_raw_api_lab.py --benchmark --echo-sample ... --capture-snaplen 128`. Supply actual built DLL paths and appropriate namespace/raw-socket privileges. `tools/render_benchmark_report.py` recreates these tables/figures from the retained measurement JSON (requires matplotlib).

All current test suites were executed. Performance measurements cover the core, security/control costs, mocks, verified kernel I/O, API path and echo option. Licensing, multi-peer lifecycle, bounded dispatch/overload, cancellation, rate policies, replay and limits have correctness evidence, but this run does not provide a dedicated performance number for every feature combination. Long-duration soak, physical NICs/two hosts, dynamic PMTUD, competing-flow fairness, public internet paths, independent security review and production release readiness remain open.
'''
(root/'docs/FULL_BENCHMARK_2026_10_01.md').write_text(report)
print('Generated README section, detailed report and two SVG figures.')
