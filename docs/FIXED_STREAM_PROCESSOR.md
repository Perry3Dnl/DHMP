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
