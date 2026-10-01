#!/usr/bin/env python3
"""Frozen five-repetition hosted measurement suite; all raw results retained."""
import argparse
import json
import os
from pathlib import Path
import platform
import re
import statistics
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument('--output', required=True)
parser.add_argument('--dotnet', required=True)
args = parser.parse_args()
out = Path(args.output).resolve()
out.mkdir(parents=True, exist_ok=True)
rows = []

def execute(name, command):
    result = subprocess.run(command, text=True, capture_output=True, timeout=300)
    (out / (name + '.log')).write_text(result.stdout + result.stderr)
    if result.returncode != 0: raise RuntimeError(name + ' failed; inspect saved log')
    return result.stdout

component = execute('components', [args.dotnet, 'benchmarks/packet-full-suite/bin/Release/net10.0/Dhmp.FullSuite.dll'])
for line in component.splitlines():
    if line.startswith('RESULT_JSON '): rows.append(json.loads(line[12:]))
projects = ['direct-ip-core-sanity', 'mock-ip-ceiling', 'mock-ipv4-framing', 'mock-ipv6-framing']
dlls = ['Dhmp.DirectIpCoreSanity', 'Dhmp.MockIpCeiling', 'Dhmp.MockIpv4Framing', 'Dhmp.MockIpv6Framing']
for repeat in range(5):
    for index in range(4):
        choice = (index + repeat) % 4
        project = projects[choice]
        text = execute(project + '-' + str(repeat + 1), [args.dotnet, f'benchmarks/{project}/bin/Release/net10.0/{dlls[choice]}.dll', '100000000'])
        for line in text.splitlines():
            if 'logical_GBps=' not in line: continue
            fields = dict(re.findall(r'(\w+)=([^ ]+)', line))
            rows.append(dict(name=project+'-batch-'+fields['batch'], scope='historical-contract-memory-logical-offered-only-newest-observation',
                repetition=repeat+1, offered_GBps=float(fields['logical_GBps']), ns_per_operation=float(fields['ns_packet']),
                seconds=float(fields['wall_s']), messages=int(fields['messages'])))
    for batch in [1, 8, 44][repeat % 3:] + [1, 8, 44][:repeat % 3]:
        text = execute(f'kernel-{batch}-{repeat+1}', ['sudo', args.dotnet, 'benchmarks/raw-ipv6-kernel/bin/Release/net10.0/Dhmp.RawIpv6Kernel.dll', '200000', str(batch)])
        for line in text.splitlines():
            if line.startswith('RESULT_JSON '):
                row = json.loads(line[12:]); row['repetition'] = repeat + 1; rows.append(row)

groups = {}
for row in rows: groups.setdefault(row['name'], []).append(row)
summary = []
for name, samples in groups.items():
    if len(samples) != 5: raise AssertionError(name + ' missing repetitions')
    metrics = {}
    for key in samples[0]:
        if key in {'repetition'}: continue
        values = [row[key] for row in samples]
        if all(type(value) in (int, float) for value in values):
            metrics[key] = dict(median=statistics.median(values), min=min(values), max=max(values))
    summary.append(dict(name=name, scope=samples[0]['scope'], metrics=metrics))
report = dict(commit=os.environ.get('GITHUB_SHA', 'local'), run=os.environ.get('GITHUB_RUN_ID'),
    kernel=platform.platform(), cpu=subprocess.check_output(['lscpu'], text=True), repetitions=5,
    decimal_units=True, raw=rows, summary=summary, physical_two_host_pass=False)
(out / 'benchmark-results.json').write_text(json.dumps(report, indent=2))
print(json.dumps(report, indent=2))
