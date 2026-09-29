"""Summarize the controlled same-harness two-buffer/single-buffer comparison."""
import json
import statistics
import sys
from pathlib import Path

root = Path(sys.argv[1])
summary = {}
print("| Profile | Baseline median ns/package (min–max) | Current median ns/package (min–max) | Median time change |")
print("| --- | ---: | ---: | ---: |")
for profile in ("receive", "fragmented"):
    results = {}
    counts = None
    for variant in ("baseline", "current"):
        measurements = []
        for run in range(1, 6):
            lines = (root / f"{profile}-{variant}-{run}.txt").read_text().splitlines()
            line, = [line for line in lines if line.startswith("benchmark=")]
            fields = dict(item.split("=", 1) for item in line.split())
            assert fields["profile"] == profile
            assert int(fields["allocated_bytes"]) == 0
            observed = tuple(fields[key] for key in ("messages", "bytes", "publications", "crossing_packages", "guard"))
            if counts is None:
                counts = observed
            assert observed == counts, "Variants did not perform equivalent publication work"
            ns = float(fields["wall_s"]) * 1e9 / int(fields["messages"])
            assert ns > 0
            measurements.append(ns)
        results[variant] = {
            "runs_ns_per_package": measurements,
            "median": statistics.median(measurements),
            "min": min(measurements),
            "max": max(measurements),
        }
    before, after = results["baseline"], results["current"]
    change = (after["median"] / before["median"] - 1) * 100
    results["median_time_change_percent"] = change
    summary[profile] = results
    print(f'| {profile} | {before["median"]:.4f} ({before["min"]:.4f}–{before["max"]:.4f}) '
          f'| {after["median"]:.4f} ({after["min"]:.4f}–{after["max"]:.4f}) | {change:+.2f}% |')
print("\nNegative time change means less time per package. Five alternating runs per variant; "
      "same warmed-up harness, typed boundary and pre-created delegates. Both variants passed "
      "the same correctness checks and allocated zero bytes during measured processing. "
      "Shared-runner measurements are indicative, not a universal speed guarantee or network throughput.")
(root / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
