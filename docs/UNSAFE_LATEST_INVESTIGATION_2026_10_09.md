# UnsafeLatest investigation — 2026-10-09

Source revision: 2dc848cfbe226880adfad18e5c9f8fb49d693f9f. Research runner: 639c35023b6e94365613588b3543bcf61e30066b.
[Successful investigation job](https://github.com/Perry3Dnl/DHMP/actions/runs/37915248030/job/113769720739).

## Finding

The isolated UnsafeLatest publication is already small; the surrounding client route still has removable overhead on its successful Unlimited path. This does not establish a hardware minimum.

Five interleaved samples of at least 100 ms per case, with alternating case order and warmup. Results below are medians on an AMD EPYC 7763 GitHub runner (.NET 10.0.12, X64, four reported logical processors), not the deployed benchmark server.

| Record bytes | Existing client + report sender, ns | Prototype + same sender, ns | Time reduction |
|---:|---:|---:|---:|
| 16 | 26.51 | 17.88 | 32.5% |
| 1,408 | 39.55 | 33.70 | 14.8% |
| 65,520 | 1,370.84 | 1,362.96 | 0.6% |

These numbers use default runtime settings. With tiered compilation disabled, the prototype also wins, but both the existing client and the copy cases behave differently: 44.62 → 19.95 ns at 16 bytes and 70.60 → 42.71 ns at 1,408 bytes. The non-tiered run is a diagnostic configuration, not a forecast of deployed performance. PGO, compilation transitions and measurement conditions affect these very short paths; some default samples vary substantially.

## Component evidence

At 1,408 bytes under the default runtime: publication alone 3.45 ns, copy alone 20.75 ns, copy + publication 22.29 ns, actual report sender 29.23 ns, existing client + that sender 39.55 ns.

At 65,520 bytes: copy alone 1,345.07 ns versus the complete existing route at 1,370.84 ns. This points to copy cost dominating this local hot-buffer workload. It does not measure DRAM bandwidth or raw socket throughput.

Component medians are not additive and should not be mechanically subtracted into a cost budget. The empty-loop/delegate control is also sensitive to compiler optimization; it is recorded as context rather than subtracted from timings.

## Generated machine code

Captured in a separate process with tiered compilation disabled, using the runtime's JIT disassembly facility.

- Existing SendAsync entry: 115 bytes; calls AsyncMethodBuilderCore.Start and AsyncValueTaskMethodBuilder.get_Task.
- Generated SendAsync.MoveNext: 2,145 bytes, including await completion, pacing/reject branches and cold exception paths.
- Unlimited prototype: 352 bytes; validates record size, cancellation and the current sender/path payload ceiling, then returns the sender ValueTask.

Code size includes cold paths and does not by itself predict runtime or instruction count on the successful path. The candidate is intentionally marked NoInlining for inspection.

The existing server's unchecked publication helper is a direct callback over its reusable slot. The native UnsafeLatest receive loop already resolves that slot once, and performs no extra managed receive copy. That loop still includes socket receive, peer/truncation/length validation and an Interlocked accepted-packet counter; none of those transport costs are in these local timings.

## Next implementation to test

Add an Unlimited fast path in DhmpClient.SendAsync that forwards the sender ValueTask while keeping the live path-budget and record checks. Keep paced and rejected paths separate. Before shipping, verify asynchronous completion, sender exceptions, cancellation, faulted/canceled ValueTasks and exception timing against the current API, then rerun on the deployed CPU.

The prototype intentionally only tests the successful Unlimited route: its validation failures can throw synchronously, unlike the current async method. It is not a drop-in replacement and has not been merged into production.

Raw samples: [results](../benchmarks/unsafelatest-investigation/results-2026-10-09.json).
Optimized disassembly: [machine code](../benchmarks/unsafelatest-investigation/jit-2026-10-09.txt).
