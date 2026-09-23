<p align="center">
  <img src="assets/dhmp-logo.webp" alt="DHMP logo" width="512">
</p>

# DHMP — Direct Headerless Message Protocol

DHMP is an experimental **fixed-contract protocol and adaptive runtime architecture** for persistent machine-to-machine communication.

The design principle is:

> **Negotiate what can be known once, then keep repeated metadata, unnecessary copies, per-message synchronization, queue growth, and avoidable processing out of the steady-state path.**

The project has moved beyond a single Ring or Slab experiment. The current retained direction is one **Adaptive Fixed-Contract** engine with specialized execution paths for `Latest`, ordinary `Every`, and numerical `Every / ComputeBlock` workloads.

<p align="center">
  <img src="benchmarks/results/charts/adaptive-current-architecture-2026-09-23.svg" alt="DHMP Adaptive Fixed-Contract architecture">
</p>

> **Current focus:** port the retained Adaptive architecture into .NET, then tune the transport/runtime layer around it. Historical Ring/Slab models remain in the benchmark documentation for reproducibility but are no longer the main README model.

## Current model at a glance

| Layer | Retained direction |
| --- | --- |
| Wire contract | Negotiate fixed record or fixed block layout once |
| Transport | DHMP over TCP; DHMPS over TLS/TCP |
| Semantics | `Every` or `Latest` — explicit and never changed by AutoTune |
| `Latest` | Compact Ring-3 `FRONT / MIDDLE / BACK` |
| `Every` | Bounded reusable slab ownership |
| Numeric `Every` | Optional computation-ready field-major block contract |
| Processing | Contract-selected fused scalar/SIMD batch routine |
| Delivery API | Batch-first lease/view |
| Delivery worker | Optional, when real downstream work repays the handoff |
| Forwarding | Owned output slab can become the sender buffer directly |
| Memory | Fixed reusable pools; move ownership instead of payload whenever possible |
| Reliability | Unconfirmed or Verified/replay model |
| Main target | Services, games, telemetry, simulation, replication, devices |

# Latest retained optimization results

The chart below summarizes the **current retained improvements**, each against its own controlled matched baseline. These gains come from different A/B harnesses and **must not be multiplied or added together**.

<p align="center">
  <img src="benchmarks/results/charts/adaptive-current-optimization-gains-2026-09-23.svg" alt="Current DHMP Adaptive optimization gains">
</p>

| Optimization | Current result | Retained decision |
| --- | ---: | --- |
| `Every` output-slab publication | **3.89×** median end-to-end result rate vs per-result publication | Keep |
| ComputeBlock SoA + AVX2 | **1.74×** isolated processor result rate vs AoS + AVX2 | Keep as optional numeric contract |
| Fused batch processing | **+16.5%** median throughput vs one scalar call/message | Keep |
| Borrowed receive slab | **+5.8%** median throughput vs receive→copy→public slab | Keep when wire-compatible |
| Direct slab-to-send | **+4.9%** median forwarding throughput vs sender scratch copy | Keep |
| Dedicated delivery worker | **+18.7% to +48.5%** in tested conversion workloads | Optional / AutoTune |
| Fixed 32-byte carry | **~63.7% lower carry housekeeping** vs variable memmove | Keep |
| Single-token Ring-3 producer ownership | **~+6.9% producer throughput** vs per-slot atomics | Keep |
| Cache-line-padded Ring-3 | **~78% worse producer cost** | Reject |
| Consumer exchange instead of CAS | No reliable CPU win; slower medians | Reject as default |

All retained correctness runs reported zero validation errors.

## 1. Latest — compact newest-state path

`Latest` is for state where newer data makes older queued state obsolete.

```text
TCP / TLS
   ↓
fixed reusable receive workspace
   ↓
identify complete frames
   ↓
skip obsolete complete states
   ↓
copy only newest useful state
   ↓
Ring-3
FRONT / MIDDLE / BACK
   ↓
consumer
```

For a 32-byte contract, retained application payload is only:

```text
3 × 32 B = 96 B
```

The retained ownership model uses one shared **32-bit `MIDDLE` token**. The producer keeps `BACK` locally; the consumer keeps `FRONT` locally. The producer uses one atomic publication exchange and the consumer retains CAS acquisition because the exchange alternative removed retries but regressed CPU cost.

`Latest` does not materialize every obsolete frame. It drains the TCP byte stream, mathematically identifies complete frames, and publishes only the newest useful state allowed by the contract.

Typical fits: game transforms, controller/device state, simulation state, dashboards, live UI state, and current-value telemetry.

## 2. Every — bounded slab path

`Every` preserves every logical record in order, so unread results may not be overwritten.

The current fast path is:

```text
bounded reusable slab pool
        ↓
receive directly into owned slab
        ↓
publish {offset, count}
        ↓
optional fused conversion
        ↓
batch adapter / developer
        │
        └── or direct slab-to-send
        ↓
release slab
```

The most important change was replacing **one ownership publication per result** with **one publication per populated slab**.

<p align="center">
  <img src="benchmarks/results/charts/adaptive-every-output-slab-2026-09-23.svg" alt="DHMP Every output-slab benchmark">
</p>

| `Every` handoff | End-to-end | Processor CPU/result | Consumer CPU/result | Results/publication |
| --- | ---: | ---: | ---: | ---: |
| Per-result publication | 33.35 M/s | 29.933 ns | 29.978 ns | 1.0 |
| **Bounded output slabs** | **129.80 M/s** | **6.988 ns** | **7.699 ns** | **372.3** |

Both variants retained the same **96 KiB bounded output payload capacity**. The slab path preserved and validated all 30 million results.

[Raw results](benchmarks/results/every-output-slab-e2e-32b-raw-9run-2026-09-23.csv) · [Summary](benchmarks/results/every-output-slab-e2e-32b-summary-9run-2026-09-23.csv)

### Borrow the receive slab when possible

When the wire representation is already suitable for developer code, the receive slab itself can become the public batch.

| Path | End-to-end | Logical payload | Receiver CPU/frame |
| --- | ---: | ---: | ---: |
| Receive workspace → copy → public slab | 119.63 M/s | 3.83 GB/s | 6.305 ns |
| **Receive slab becomes public slab** | **126.51 M/s** | **4.05 GB/s** | **6.184 ns** |

Only TCP split-frame boundary fragments need copying. The complete-frame region remains in the borrowed slab until downstream code releases the lease.

[Summary](benchmarks/results/every-receive-slab-lease-ab-summary-12run-2026-09-23.csv)

## 3. Fuse the computation across the batch

Slab batching removes storage and ownership overhead, but developer-facing processing can still waste CPU if it invokes one conversion routine per message.

The current processing experiment performs the same numeric transform using per-message scalar calls, one fused scalar routine per batch, explicit AVX2, and AVX-512 gather/scatter.

<p align="center">
  <img src="benchmarks/results/charts/adaptive-fused-processing-2026-09-23.svg" alt="DHMP fused batch processing benchmark">
</p>

| Processor | End-to-end | Processor CPU/result |
| --- | ---: | ---: |
| Per-message scalar | 30.82 M/s | 32.451 ns |
| **Fused scalar batch** | **35.92 M/s** | **27.771 ns** |
| Fused AVX2 | 35.81 M/s | 27.872 ns |
| Fused AVX-512 gather/scatter | 34.34 M/s | 29.061 ns |

The retained lesson is **fuse first, SIMD second**. The widest ISA is not automatically best; the negotiated data layout determines whether vectorization repays its gather/transpose cost.

[Summary](benchmarks/results/every-fused-vector-processing-ab-summary-12run-2026-09-23.csv)

## 4. ComputeBlock — make the wire format computation-ready

For array-native numerical workloads, DHMP can optionally negotiate the layout of a **whole fixed block** rather than only a repeated record.

Same 12 KiB logical block:

```text
AoS record block
[x y z vx vy vz temp scale]
[x y z vx vy vz temp scale]
...

SoA computation block
[x x x x ...]
[y y y y ...]
[z z z z ...]
[vx vx vx ...]
[vy vy vy ...]
[vz vz vz ...]
[temp ...]
[scale ...]
```

The SoA layout travels over the wire, so the receiver does not first transpose ordinary messages into field arrays.

<p align="center">
  <img src="benchmarks/results/charts/adaptive-computeblock-cpu-2026-09-23.svg" alt="DHMP computation-ready block processor benchmark">
</p>

| Layout / routine | Processor CPU/result | CPU-side result rate |
| --- | ---: | ---: |
| AoS scalar | 2.434 ns | 410.9 M/s |
| AoS + AVX2 transpose | 1.080 ns | 925.9 M/s |
| SoA scalar | 2.370 ns | 421.9 M/s |
| **SoA + AVX2 contiguous fields** | **0.621 ns** | **1.610 B/s** |

Compared with AVX2 over ordinary AoS records, the computation-ready block reduced isolated transform cost by about **42.5%** and raised processor-side result rate by about **1.74×**.

The integrated localhost pipeline improved only about **1.7%**, because network, slab ownership, waiting, and validation then dominate. ComputeBlock is therefore an **optional specialization**, not a universal replacement for ordinary records.

Best fits: simulation, numerical telemetry, physics/state arrays, bulk coordinate transforms, signal/data processing, and similar array-native workloads.

[Wire summary](benchmarks/results/block-layout-wire-ab-summary-9run-2026-09-23.csv) · [Processor summary](benchmarks/results/block-layout-processor-micro-summary-7run-2026-09-23.csv)

## 5. Delivery and forwarding stay batch-oriented

The same ownership model continues across the application boundary.

| Layer | Retained direction | Result |
| --- | --- | ---: |
| Delivery worker | Give the worker a whole populated slab, not one message | +18.7% to +48.5% wall-clock throughput in tested conversion weights |
| Public API | Batch-first lease/view | One adapter crossing per ~384 records in the retained tests |
| Forwarding | Send directly from the owned wire-ready output slab | 41.93 → **43.98 M/s** (~+4.9%) |
| Placement | Keep producer/consumer/worker cache topology in mind | Promising but hardware-dependent |

A delivery worker is useful only when it performs real work—decoding, conversion, or application preparation. A helper that only forwards pointers adds another handoff without relieving the protocol processor.

The worker is therefore an **optional runtime choice**, not a protocol requirement.

[Delivery-worker summary](benchmarks/results/every-delivery-worker-ab-summary-7run-2026-09-23.csv) · [Forwarding summary](benchmarks/results/every-forward-direct-slab-ab-summary-12run-2026-09-23.csv)

# The retained rule

The newest architecture is built around one principle:

> **Move ownership and descriptions whenever possible; move payload bytes only when semantics or representation require it.**

That means:

- fixed contract negotiated once;
- no repeated DHMP length/header for ordinary fixed records;
- fixed reusable transport workspaces;
- compact Ring-3 retained state for `Latest`;
- bounded slab pools for `Every`;
- one handoff per populated batch instead of per message;
- borrowed receive slabs for directly consumable payloads;
- direct processor-output-to-sender ownership;
- fused batch processing;
- optional computation-ready block layout;
- batch-first language adapters;
- optional parallel delivery worker;
- fixed memory rather than growing queues.

# Protocol vs runtime optimization

DHMP deliberately separates interoperability rules from implementation tuning.

### Protocol-level

- fixed-contract handshake;
- fixed record or fixed block boundary;
- `Every` / `Latest`;
- Unconfirmed / Verified;
- optional computation-ready block layout;
- DHMP / DHMPS.

### Runtime-level

- Ring-3 implementation;
- slab size/count;
- 32-bit atomic ownership token;
- fixed-width carry path;
- fused/SIMD routine selection;
- delivery worker;
- CPU/cache placement;
- send/receive batching;
- socket buffer sizing;
- polling;
- future `io_uring`, registered buffers, busy polling, kTLS and zero-copy APIs.

This keeps DHMP implementable in .NET, C/C++, Rust, Go, Java, Python adapters, Unity, and other environments without making one operating-system tuning choice part of the protocol.

# Cross-protocol benchmark status

The previous README displayed the native **Ring-3 v4 zero-hold** comparison against raw TCP, length-prefixed TCP, WebSocket framing, HTTP framing, gRPC framing, MQTT, NATS, and UDP.

That benchmark is still valid for the **Latest/Ring-3 v4 path it measured**, but it predates the newer `Every`, fused-processing, borrowed-slab, direct-forwarding, and ComputeBlock work. It is therefore **not shown as a headline graph here and is not relabeled as the Adaptive engine**.

The old comparison remains available in the benchmark history:

- [Showcase v4 methodology](benchmarks/native-showcase/V4_ZERO_HOLD.md)
- [Showcase v4 summary CSV](benchmarks/results/showcase-v4-zero-hold-32b-summary-5run-2026-09-23.csv)
- [Full benchmark history](docs/BENCHMARKS.md)

A new cross-protocol headline graph should be published only after the current retained Adaptive implementation is rerun inside one controlled comparison harness.

# Delivery semantics

Receive semantics and recovery semantics are independent.

### Unconfirmed

The sender streams without requiring DHMP-level proof/replay for every logical record. TCP still supplies ordered reliable byte delivery while the connection remains alive.

### Verified

Verified keeps the hot data path streaming:

- no per-frame DHMP ACK;
- sender retains uncertain logical history;
- reconnect/checkpoint establishes the accepted position;
- only the uncertain tail is replayed.

The remaining work is a clean bounded checkpoint/history implementation.

# DHMP and DHMPS

**DHMP** is the plain fixed-contract application protocol.

**DHMPS** carries the same model through standard platform TLS. DHMP does not invent custom cryptography.

TLS remains a substantial CPU layer and has not yet received the same optimization depth as the plain native path.

# Benchmark discipline

Different benchmark families answer different questions.

- **Integrated native pipeline tests** measure socket + ownership + processing paths.
- **Processor microbenchmarks** isolate CPU/layout behavior.
- **Slow-consumer tests** validate ownership and conflation safety.
- **.NET tests** measure the actual .NET prototype and framework integrations.
- **Cross-protocol showcase tests** compare framing/transport shapes inside one native harness.

Absolute numbers from different harnesses should not be mixed. Optimization percentages in the README are controlled A/B results against their stated baseline.

All current retained benchmark source, raw CSV data, summaries, and historical results are kept in the repository.

# Current status and next work

The strongest retained native architecture now includes:

- Adaptive Fixed-Contract runtime selection;
- Ring-3 `Latest` with one 32-bit shared ownership token;
- fixed-width carry handling;
- bounded output-slab `Every`;
- borrowed receive-slab delivery;
- direct slab-to-send forwarding;
- fused batch processing;
- contract-selected SIMD;
- optional SoA ComputeBlock wire contracts;
- optional batch delivery worker;
- topology-aware placement as an AutoTune candidate.

The next major work is:

1. **Port these retained Adaptive paths into the .NET implementation.**
2. Build one new integrated benchmark harness around the current Adaptive code, then regenerate the cross-protocol comparison from fresh measurements.
3. Tune receive slab size, sender batch size, `SO_RCVBUF`, `SO_SNDBUF`, polling, CPU placement, and TLS.
4. Repeat the combined tests on physical LAN hardware with longer runs and hardware counters.
5. Measure ComputeBlock latency/accumulation cost as well as throughput.
6. Implement bounded Verified checkpoints.

# Documentation

- [Architecture comparison and retained model](docs/ARCHITECTURE_COMPARISON.md)
- [Current development status](docs/CURRENT_STATUS.md)
- [Protocol draft](docs/PROTOCOL_DRAFT.md)
- [Benchmark methodology and full history](docs/BENCHMARKS.md)
- [Transport tuning follow-up](docs/TRANSPORT_TUNING_TODO.md)
- [Native showcase labs](benchmarks/native-showcase)
- [Native processor labs](benchmarks/native-gen2)
- [Raw benchmark results and charts](benchmarks/results)

# Requirements

The main implementation target is **.NET 10**.

The newest architecture work currently lives in native Linux/C benchmark labs so designs can be tested quickly before the retained paths are ported into the production-facing .NET implementation.
