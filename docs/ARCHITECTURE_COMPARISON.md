# DHMP architecture comparison and retained model

The current retained implementation direction is **Adaptive Fixed-Contract**. The application still chooses `Every` or `Latest`; the runtime only selects implementation details that preserve those semantics.

Current README figures:

- [Adaptive architecture](../benchmarks/results/charts/adaptive-current-architecture-2026-09-23.svg)
- [Current matched A/B optimization gains](../benchmarks/results/charts/adaptive-current-optimization-gains-2026-09-23.svg)
- [Every output-slab result](../benchmarks/results/charts/adaptive-every-output-slab-2026-09-23.svg)
- [Fused processing result](../benchmarks/results/charts/adaptive-fused-processing-2026-09-23.svg)
- [ComputeBlock processor result](../benchmarks/results/charts/adaptive-computeblock-cpu-2026-09-23.svg)

## Current headline

The root README now focuses on the latest retained Adaptive architecture. The older Ring-3 v4 cross-protocol graph remains historical evidence for the `Latest` path it measured and is no longer presented as if it represented the newer `Every`/ComputeBlock optimizations.

This document consolidates the optimization work into one view. Percentages and rates come from different controlled A/B tests and **must not be added together as if they were independent cumulative gains**.

## Layer-by-layer comparison

| Layer | Models tested | Retained direction | Evidence / reason |
| --- | --- | --- | --- |
| Wire semantics | Every, Latest | **Both remain first-class** | They provide different correctness semantics. Latest may discard obsolete complete states; Every may not. No implementation optimization can safely merge those semantics. |
| Latest retained state | Ring-2, Ring-3, Ring-4/8 and slab variants | **Ring-3** | Ring-2 did not provide a useful throughput advantage under safe zero-copy ownership. Ring-3 provides independent FRONT/MIDDLE/BACK roles with only 96 B retained payload at 32 B/frame. |
| Ring-3 ownership | Per-slot atomics, single MIDDLE exchange, consumer CAS, consumer exchange | **Single 32-bit MIDDLE token + consumer CAS** | Single-token producer publication improved producer throughput ~6.9%. Consumer exchange removed retries but regressed CPU; keep CAS. |
| Ring-3 memory layout | Packed 96 B, cache-line-padded 192 B | **Packed 96 B** | Padding increased isolated producer update cost by ~78%. |
| Atomic token size | 8, 16, 32, 64 bit | **32 bit** | 32/64 effectively tied; sub-word atomics were materially slower. 32 bit is compact without losing speed. |
| Receive workspace | Tiny retained buffers only, separate reusable receive slab | **Separate fixed receive workspace** | A larger contiguous receive workspace amortizes socket work while semantic retained state stays tiny. Current reference uses 12 KiB. |
| Partial-frame carry | Variable memmove, fixed-width carry | **Conditional fixed 32-byte vector carry** | 4.794 ns/batch → 1.739 ns/batch (~63.7% lower carry housekeeping). |
| Fixed contract processor | Runtime size/division, contract-specialized path | **Contract-selected specialized routine** | 32-byte specialization reduced isolated processor batch cost ~1.5% and enables further layout-specific routines. |
| Latest publication | Materialize many states, mathematical conflation | **Conflate before expensive work** | Latest should publish only the newest useful state and avoid processing obsolete states whenever semantics permit. |
| Every handoff | Per-result publication, bounded output slabs | **Bounded output slabs** | 33.35 M → 129.80 M results/s in the integrated Every A/B; receiver/processor CPU 29.933 → 6.988 ns/result. |
| Every receive storage | Receive workspace then copy, borrowed receive slab | **Borrow receive slab when wire-compatible** | 119.63 M → 126.51 M frames/s (~+5.8%) with only boundary fragments copied. |
| Every transformed output | One result at a time, batch output slab | **Write directly into a claimed output slab** | Eliminates per-result ownership publication and keeps memory bounded. |
| Forwarding | Output slab → sender copy, output slab → send directly | **Send directly from owned output slab** | 41.93 M → 43.98 M results/s (~+4.9%) in the forwarding A/B. |
| Processing dispatch | Per-message calls, fused scalar batch | **Fuse processing per populated batch** | 30.82 M → 35.92 M results/s (~+16.5%); processor CPU 32.451 → 27.771 ns/result. |
| SIMD for AoS records | Scalar, AVX2 transpose, AVX-512 gather/scatter | **Select by contract; do not force widest ISA** | AVX2 roughly tied fused scalar in the integrated AoS test; AVX-512 gather/scatter was slower. |
| Computation-ready wire block | AoS record block, field-major SoA block | **Optional SoA fixed-block contract for numerical Every workloads** | Processor-only AVX2 cost 1.080 → 0.621 ns/result (~42.5% lower, ~1.74× result rate). Current end-to-end gain was only ~1.7% because other stages dominate. |
| Delivery boundary | Protocol thread performs conversion/callback, dedicated batch worker | **Optional batch worker** | With cache-aware placement the worker improved wall-clock throughput ~18.7–48.5% across tested synthetic conversion weights, but uses more aggregate CPU. |
| Public API shape | Per-message callback, batch-oriented lease/view | **Batch-first high-performance API** | Keeps ownership/callback frequency proportional to slabs rather than messages. Per-message convenience can enumerate a batch above the core. |
| CPU placement | Fixed CPU numbers, topology-aware pairing | **Topology-aware / AutoTune candidate** | Isolated Ring-3 handoff improved on one cache-close pair, while full loopback did not produce a universal placement rule. Hardware-dependent. |
| Socket/runtime tuning | Current fixed settings, systematic tuning | **Still open** | Receive slab, sender batch, socket buffers, polling, io_uring, registered buffers, TLS tuning and NIC affinity remain substantial unexplored implementation space. |
| TLS | DHMP/TCP, DHMPS/TLS 1.3 | **DHMPS remains standard secure form** | TLS cost is visible and has not yet received the same depth of tuning as the plain path. |
| Verified delivery | Unconfirmed, Verified replay/checkpoint model | **Keep semantics separate from hot data path** | No periodic per-frame ACK hot path. Bounded checkpoint/history implementation remains future work. |

## Retained execution profiles

### 1. Latest state profile

Best retained architecture for state/freshness workloads:

```text
TCP/TLS receive
    ↓
fixed reusable receive slab
    ↓
contract-specialized framing + fixed carry
    ↓
skip obsolete complete states
    ↓
copy only newest useful state
    ↓
compact Ring-3 FRONT / MIDDLE / BACK
    ↓
batch/developer view
```

This profile deliberately keeps only tiny semantic state so the receive workspace can be reused immediately.

### 2. Every record profile

Best retained architecture for ordinary records where every result matters:

```text
bounded receive-slab pool
    ↓
receive directly into leased slab when possible
    ↓
publish whole populated batch
    ↓
optional fused conversion / output slab
    ↓
optional batch delivery worker
    ↓
adapter / developer OR direct slab-to-send
    ↓
release ownership
```

No unread Every result may be overwritten. Backpressure is bounded by the fixed slab pool.

### 3. Every computation-block profile

Best retained architecture for array-native numerical workloads:

```text
negotiated fixed SoA block on the wire
    ↓
receive computation-ready field arrays
    ↓
contract-selected fused SIMD routine
    ↓
bounded output slab only if a different representation is required
    ↓
batch worker / typed array adapter / direct forwarding
```

This profile is strongest when the producer and consumer already work in blocks/arrays. It is not appropriate for unrelated commands or workloads requiring immediate single-message delivery.

## What should be the one default mode?

There should not be one fixed low-level data path for all DHMP traffic because `Latest` and `Every` have different correctness requirements.

The best **single implementation mode** is therefore an **Adaptive Fixed-Contract runtime**:

```text
application chooses required semantics:
    Every or Latest
            ↓
handshake fixes:
    frame/block contract
    record or computation-block layout
    plain or TLS
            ↓
runtime selects:
    Ring-3 Latest path
        OR
    borrowed/bounded Every slab path
            ↓
runtime optionally selects:
    fused scalar / SIMD routine
    delivery worker
    direct slab forwarding
    topology-aware placement
    tuned transport settings
```

The runtime may optimize implementation choices automatically, but it must **not** automatically change `Every` into `Latest` or vice versa because that changes application semantics.

### Recommended public default

Call the implementation profile **Adaptive** (name provisional), with semantics explicit in the contract:

```text
DHMP Adaptive + Latest
DHMP Adaptive + Every
DHMP Adaptive + Every/ComputeBlock
```

If forced to choose one high-performance profile for the numerical workload tested here, the strongest processor model is:

**Every + fixed computation-ready SoA block + fused AVX2 processing + bounded slab ownership**, with a dedicated delivery worker only when profiling/AutoTune shows enough downstream work to repay the extra cross-core handoff.

For general service-to-service traffic, ordinary fixed-record `Every` with borrowed receive slabs is the safer default because it avoids block accumulation latency and sender-side repacking requirements.

## Rejected or non-default ideas

The following should not be restored as defaults without new evidence:

- Ring-2 as the general zero-copy Latest model;
- cache-line padding of the three 32-byte Ring-3 slots;
- 8/16-bit atomic ownership tokens;
- consumer atomic exchange as a speed optimization;
- unconditional AVX-512 selection;
- one-message-at-a-time Every ownership publication;
- copying Every receive data into a second slab when the original receive slab can be safely leased;
- copying an output slab into a sender batch when the transport can send directly from the owned slab.

## Next high-value validation

The architecture is now sufficiently optimized that the next most valuable work is less about another isolated nanosecond tweak and more about integration:

1. port the retained Adaptive paths into the .NET implementation;
2. sweep receive slab / sender batch / socket-buffer sizes;
3. benchmark the combined paths on physical LAN hardware rather than only localhost;
4. measure latency as well as throughput, especially for computation blocks;
5. add hardware-counter measurements for cache misses, cache-to-cache transfer and branch behavior;
6. test computation-ready blocks with array-native producers and consumers so sender repacking and object construction are measured explicitly;
7. optimize DHMPS/TLS with the same architecture.
