# Fixed stream processor contract

`DHMPFixedStreamProcessor` is a synchronous .NET framing component, not a transport or
an application scheduler. This implementation contract does not change the DHMP wire protocol.

- Use one instance per ordered incoming byte stream. Serialize calls; neither callback
  may call `Process` on that same instance. Concurrent calls and re-entrancy are unsupported
  precondition violations, not supported operations that are guaranteed to throw.
- Supply two non-null internal handoff callbacks. Each must finish consuming its span
  before returning. Keep arbitrary application execution behind the handoff layer.
- `publishCrossBoundary` receives exactly one reconstructed package. `publishBorrowed`
  receives a nonempty run of complete packages, potentially many packages in one callback.
  Neither callback may retain borrowed storage beyond its call. An asynchronous receiver
  must copy bytes or establish an explicit lease/ownership transfer in the next layer.
- One reusable carry array holds incomplete packages. Complete input runs are not copied.
  With reusable callbacks, normal `Process` calls do not allocate managed memory.
- Empty input does not publish anything. An incomplete final package remains buffered;
  the stream owner handles truncation/end-of-stream policy. There is no implicit flush.
- A callback exception propagates immediately. The caller must abort that processing stream
  and discard the instance. There is no automatic retry, rollback, consumed-byte result,
  or guarantee about reuse after failure. No extra poisoned-state check is added per call.

## Validation

Run `dotnet run --project benchmarks/dotnet-receive-boundary-tests -c Release`.
The executable checks fragment boundaries, power-of-two and other package sizes, empty
input, ordering and byte identity, publication shape, independent streams, borrowed input,
callback failure propagation, and allocations after warm-up. Unsupported concurrent/re-entrant
use is not advertised as safe or tested as an operation that must be rejected.

## Performance comparison

The `Fixed stream carry A/B` workflow compares this processor with the two-buffer version
at commit `213e39825174384b5c92c80066b63164231949dd`. It replaces only the processor source
in a temporary baseline copy, using the same current benchmark and dependencies for both.
Both builds run the same correctness tests before measurement.

The benchmark pre-creates all callback delegates, warms up before timing, reports managed
allocations and alternates baseline/current order. It includes typical receive chunks and
a fragment-heavy pattern. Tiered compilation is disabled for both in this controlled A/B.
Results are framing plus typed-boundary costs on a shared GitHub runner, not network throughput
or proof of a universal speedup. Historical results from the old harness are not directly comparable.

## Measured result — 2026-09-29

[Validated A/B run](https://github.com/Perry3Dnl/DHMP/actions/runs/36529678013),
current commit `7c3640fe5245a13bc9666f3f36e189aa1a39ca90` (processor change
`11adddfaa23fbf491aa64fb67f9192097bd567f7`).

Both variants passed the expanded correctness suite and allocated **0 managed bytes**
during every timed sample. Release builds reported zero warnings and errors.
Five runs per variant, alternating order, .NET 10 on the same hosted Ubuntu runner,
`DOTNET_TieredCompilation=0`. Each timed sample lasted approximately 2.1–2.9 seconds.

| Profile | Logical packages per sample | Baseline median time | Single-buffer median time | Time change |
| --- | ---: | ---: | ---: | ---: |
| Receive chunks | 100,000,000,000 | 2.431210 s | 2.085630 s | -14.21% |
| Fragment-heavy | 200,000,000 | 2.809414 s | 2.417343 s | -13.96% |

These are framing plus typed-boundary measurements. The receive profile batches many
packages per callback and does **not** inspect every payload byte or process every model.
Its logical package count must not be interpreted as network throughput or application
message processing throughput. The measured reduction is specific to this harness and
runner; no claim of a universal 14% improvement is made.

Raw wall-clock seconds, in per-variant run order:

| Profile / variant | Run 1 | Run 2 | Run 3 | Run 4 | Run 5 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Receive / baseline | 2.431652 | 2.431210 | 2.402490 | 2.403354 | 2.445380 |
| Receive / current | 2.106471 | 2.085630 | 2.079144 | 2.086630 | 2.085255 |
| Fragmented / baseline | 2.840599 | 2.880531 | 2.789859 | 2.780468 | 2.809414 |
| Fragmented / current | 2.452681 | 2.451235 | 2.411780 | 2.399211 | 2.417343 |

The workflow artifact contains full output, environment information and `summary.json`.
An initial short-sample run was used to calibrate duration; the table above uses the
longer samples only. Absolute timings from separate runners should not be compared.
