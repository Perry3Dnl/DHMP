<p align="center">
  <img src="assets/dhmp-logo.webp" alt="DHMP logo" width="512">
</p>

# DHMP — Direct Headerless Message Protocol

DHMP is an experimental fixed-contract protocol and runtime architecture for persistent machine-to-machine communication.

The core idea is simple:

> **Negotiate what can be known once, then keep repeated metadata, unnecessary copies, per-message allocation, queue growth, and avoidable application work out of the steady-state path.**

The current retained design is no longer one fixed processor loop. It is an **Adaptive Fixed-Contract engine**: the application explicitly chooses the required semantics (`Every` or `Latest`), the session negotiates its fixed contract once, and the runtime selects the fastest validated implementation path that preserves those semantics.

The native Linux/C labs are currently ahead of the .NET prototype and are used to prove architecture changes before they are ported into the .NET implementation.

> **Current focus:** one adaptive runtime with specialized `Latest`, ordinary `Every`, and computation-block `Every` paths. Old experimental models remain in the benchmark history but are no longer the README focus.

## At a glance

| Property | Current DHMP direction |
| --- | --- |
| Steady-state framing | Fixed contract negotiated once; no repeated DHMP length/header for ordinary fixed records |
| Transport | TCP for DHMP; TLS/TCP for DHMPS |
| Receive semantics | **Every** or **Latest** — always explicit |
| Latest implementation | Compact Ring-3 `FRONT / MIDDLE / BACK` |
| Every implementation | Bounded reusable slab ownership |
| High-throughput API | Batch-first |
| Numeric specialization | Optional fixed computation-ready block contract |
| Processing | Contract-selected fused scalar/SIMD routine |
| Memory model | Fixed reusable pools; ownership moves instead of payload where possible |
| Delivery | Inline or optional batch delivery worker |
| Forwarding | Owned output slab can become the next sender buffer directly |
| Delivery reliability | Unconfirmed or Verified/replay model |
| Main target | Service-to-service, games, telemetry, simulation, replication, devices |

## The current retained model: Adaptive Fixed-Contract

The wire semantics remain small. Most optimizations are implementation choices selected after the contract is known.

```text
application chooses semantics
        │
        ├── Latest
        │
        └── Every
        │
        ▼
handshake negotiates fixed contract
  frame/block size
  representation
  plain/TLS
        │
        ▼
runtime selects the fastest valid path
        │
        ├── Latest / Ring-3
        ├── Every / borrowed slabs
        └── Every / computation block
        │
        ▼
optional runtime specialization
  fused processing
  SIMD routine
  delivery worker
  direct slab forwarding
  topology / transport tuning
```

**Adaptive never changes application semantics.** It may choose a faster implementation for `Every`, but it may not silently turn `Every` into `Latest`.

## Three retained execution profiles

### 1. Latest — newest useful state

`Latest` is for state where newer data makes older queued state obsolete.

```text
TCP / TLS
   ↓
fixed reusable receive workspace
   ↓
identify complete frames
   ↓
conflate obsolete complete states
   ↓
copy only newest useful state
   ↓
compact Ring-3
 FRONT / MIDDLE / BACK
   ↓
consumer
```

For a 32-byte contract, the three retained payload slots are only:

```text
3 × 32 B = 96 B
```

The current ownership model uses one shared 32-bit `MIDDLE` token. The producer keeps `BACK` locally and the consumer keeps `FRONT` locally. The design does not shift retained state, grow a queue, or clear old payload memory before reuse.

Typical fits: game transforms, controller state, simulation state, dashboards, live UI state, and high-frequency current-value telemetry.

### 2. Every — ordinary record stream

`Every` preserves every logical record in order. The current fast path is based on **bounded slab ownership**, not one atomic publication per message.

```text
bounded reusable slab pool
        ↓
recv directly into owned slab
        ↓
publish {offset, count}
        ↓
optional fused conversion
        ↓
batch adapter / developer
        │
        └── or direct slab-to-send forwarding
        ↓
release slab
```

If the received representation is already suitable for the application, the **receive slab itself becomes the public batch**. Only split-frame boundary fragments are copied.

If conversion needs a different representation, the processor writes results sequentially into a reusable output slab and publishes the populated batch once.

Typical fits: commands, events, replication, logs, transactions, and telemetry where every sample matters.

### 3. Every / ComputeBlock — numeric and array-native workloads

For simulations, numerical telemetry, bulk transforms and similar workloads, DHMP can optionally negotiate the layout of a **whole fixed computation-ready block** rather than only one repeated record.

Example 12 KiB contract:

```text
ordinary AoS block
[x y z vx vy vz temp scale]
[x y z vx vy vz temp scale]
...

computation-ready SoA block
[x x x x ...]
[y y y y ...]
[z z z z ...]
[vx vx vx ...]
[vy vy vy ...]
[vz vz vz ...]
[temp ...]
[scale ...]
```

The SoA form travels over the wire. The receiver can operate directly on contiguous field arrays without first transposing ordinary records.

This is **optional**, not the default DHMP record layout. It is strongest when both producer and consumer naturally work in arrays and the block-accumulation latency is acceptable.

---

# Current benchmark evidence

These are native Linux/C architecture-lab results on localhost. They are useful for controlled A/B decisions, not universal network-throughput claims. Logical GB/s is not physical NIC throughput.

## Latest: zero-hold transport/framing showcase

The current `Latest` headline comparison has **zero artificial consumer delay**.

Test profile:

- 32-byte logical records;
- 12 KiB reusable receive workspace;
- 256 KiB reusable sender batch;
- Ring-3 publication;
- zero artificial consumer hold;
- five rotated runs;
- sender / receiver / consumer pinned when available;
- full payload/framing validation.

![Current DHMP Latest zero-hold input comparison](benchmarks/results/charts/showcase-v4-zero-hold-32b-input-2026-09-23.svg)

![Current DHMP Latest zero-hold receiver CPU comparison](benchmarks/results/charts/showcase-v4-zero-hold-32b-cpu-2026-09-23.svg)

Selected five-run medians:

| Path | Logical input | Receiver CPU / logical frame |
| --- | ---: | ---: |
| **DHMP Latest / Ring-3 v4** | **130.62 M/s** | **2.704 ns** |
| Raw TCP / fixed 32 B | 111.73 M/s | 3.120 ns |
| TCP / one-byte varint length | 132.30 M/s | 3.676 ns |
| WebSocket binary framing | 119.32 M/s | 3.474 ns |
| HTTP/2 DATA framing | 105.57 M/s | 5.608 ns |
| gRPC / HTTP/2 framing shape | 98.92 M/s | 6.498 ns |

Raw fixed TCP and DHMP perform essentially the same fixed-record wire work. Small differences around the transport floor should be read as benchmark variance, not as DHMP somehow making TCP intrinsically faster.

The WebSocket, HTTP, gRPC, MQTT and NATS rows in this native showcase are **framing/parser hot-path shapes**, not full production server/framework stacks.

[Methodology](benchmarks/native-showcase/V4_ZERO_HOLD.md) · [Summary CSV](benchmarks/results/showcase-v4-zero-hold-32b-summary-5run-2026-09-23.csv)

## Every: publish slabs, not individual results

The strongest integrated `Every` handoff result replaced one ownership publication per 32-byte result with one publication per populated output slab.

Both sides retained the same **96 KiB bounded output payload capacity**.

![Current DHMP Every output-slab result](benchmarks/results/charts/adaptive-every-output-slab-2026-09-23.svg)

| Every handoff | End-to-end results | Processor CPU/result | Consumer CPU/result | Results/publication |
| --- | ---: | ---: | ---: | ---: |
| Per-result publication | 33.35 M/s | 29.933 ns | 29.978 ns | 1.0 |
| **Bounded output slabs** | **129.80 M/s** | **6.988 ns** | **7.699 ns** | **372.3** |

That is about **3.89× the median end-to-end result rate** in this A/B, with all 30 million results preserved and validated.

[Raw CSV](benchmarks/results/every-output-slab-e2e-32b-raw-9run-2026-09-23.csv) · [Summary CSV](benchmarks/results/every-output-slab-e2e-32b-summary-9run-2026-09-23.csv)

## Every: borrow the receive slab when possible

For wire-compatible `Every` payloads, the receiver can lend the receive slab itself to downstream code instead of copying all complete frames into a second public buffer.

Twelve-run median:

| Path | End-to-end | Logical payload | Receiver CPU/frame |
| --- | ---: | ---: | ---: |
| Receive workspace → copy → public slab | 119.63 M/s | 3.83 GB/s | 6.305 ns |
| **Receive slab becomes public slab** | **126.51 M/s** | **4.05 GB/s** | **6.184 ns** |

The borrowed path improved median throughput by about **5.8%**. Split-frame boundary repair remained tiny relative to the full payload; complete frames were not copied into a second application buffer.

[Summary CSV](benchmarks/results/every-receive-slab-lease-ab-summary-12run-2026-09-23.csv)

## Processing: fuse the work across the batch

Batching storage is not enough if developer-facing computation still dispatches one function per message.

The current processor experiment performs the same numerical transform through four implementations:

![Current DHMP fused-processing comparison](benchmarks/results/charts/adaptive-fused-processing-2026-09-23.svg)

| Processor | End-to-end | Processor CPU/result |
| --- | ---: | ---: |
| Per-message scalar | 30.82 M/s | 32.451 ns |
| **Fused scalar batch** | **35.92 M/s** | **27.771 ns** |
| Fused AVX2 | 35.81 M/s | 27.872 ns |
| Fused AVX-512 gather/scatter | 34.34 M/s | 29.061 ns |

The largest retained gain here came from **fusing the batch**, not simply selecting the widest SIMD ISA. The runtime should therefore select a routine by contract/layout and CPU instead of unconditionally preferring AVX-512.

[Summary CSV](benchmarks/results/every-fused-vector-processing-ab-summary-12run-2026-09-23.csv)

## ComputeBlock: make the wire representation SIMD-ready

The computation-block experiment kept the wire bytes identical: 384 × 32-byte messages = one 12 KiB block either way.

The processor-only result exposes the layout effect directly:

![Computation-ready block processor cost](benchmarks/results/charts/adaptive-computeblock-cpu-2026-09-23.svg)

| Layout / routine | CPU/result | CPU-side result rate |
| --- | ---: | ---: |
| AoS scalar | 2.434 ns | 410.9 M/s |
| AoS + AVX2 transpose | 1.080 ns | 925.9 M/s |
| SoA scalar | 2.370 ns | 421.9 M/s |
| **SoA + AVX2 contiguous fields** | **0.621 ns** | **1.610 B/s** |

Compared with AVX2 over ordinary AoS records, the computation-ready SoA block reduced isolated transform cost by about **42.5%** and raised CPU-side transform rate by about **1.74×**.

The integrated localhost pipeline showed a much smaller improvement (about **1.7%**) because transport, slab handoff and validation then dominated. That is precisely why this layout is a specialization for compute-heavy array-native workloads rather than the universal record format.

[Wire summary](benchmarks/results/block-layout-wire-ab-summary-9run-2026-09-23.csv) · [Processor summary](benchmarks/results/block-layout-processor-micro-summary-7run-2026-09-23.csv)

## Delivery and forwarding

The same ownership model extends beyond the processor:

| Optimization | Controlled result | Retained direction |
| --- | ---: | --- |
| Output slab → direct `send()` instead of sender scratch copy | 41.93 → **43.98 M/s** (~+4.9%) | Send directly from the owned wire-ready slab |
| Dedicated batch delivery worker | **+18.7% to +48.5%** wall-clock throughput across tested conversion weights | Optional; use when real downstream work repays the cross-core handoff |
| Cache-aware worker placement | Materially changed whether the worker helped | Runtime/AutoTune candidate, not a hard-coded CPU number |

The delivery worker does not necessarily reduce aggregate CPU. Its value is allowing protocol receive work and application preparation to execute concurrently on separate cores.

[Direct forwarding summary](benchmarks/results/every-forward-direct-slab-ab-summary-12run-2026-09-23.csv) · [Delivery-worker summary](benchmarks/results/every-delivery-worker-ab-summary-7run-2026-09-23.csv)

---

# What DHMP is optimizing

The retained design follows one rule through the whole pipeline:

> **Move ownership and descriptions whenever possible; move payload bytes only when the semantics or representation require it.**

That leads to:

- no repeated DHMP frame length/header for ordinary fixed contracts;
- fixed reusable transport workspaces;
- compact Ring-3 state for `Latest`;
- bounded slab pools for `Every`;
- mathematical conflation before expensive `Latest` work;
- one handoff per populated batch instead of per result;
- borrowed receive slabs for directly consumable payloads;
- direct processor-output-to-sender ownership;
- fused batch processing;
- optional computation-ready wire blocks;
- batch-first language adapters;
- optional parallel delivery workers;
- fixed memory rather than growing queues.

## Protocol versus implementation

Not every benchmark optimization belongs in the DHMP wire specification.

### Protocol-level concepts

- fixed-contract handshake;
- fixed record or fixed block boundary;
- `Every` / `Latest`;
- Unconfirmed / Verified delivery;
- optional negotiated computation-block layout;
- DHMP versus DHMPS.

### Runtime/implementation concepts

- Ring-3 ownership;
- slab count and slab size;
- atomic token representation;
- fused/SIMD routine selection;
- delivery worker;
- CPU/cache placement;
- socket buffers;
- sender batching;
- polling strategy;
- `io_uring`, registered buffers, busy polling, kTLS and similar platform accelerators.

This separation keeps the protocol implementable in other languages while allowing the reference runtime to be aggressively optimized.

# Delivery semantics

Receive semantics and recovery semantics are separate.

### Unconfirmed

The sender streams without requiring DHMP-level proof/replay for every logical record. TCP still provides ordered byte delivery while the connection remains alive.

### Verified

Verified keeps the normal data path streaming:

- no per-frame DHMP ACK;
- sender retains uncertain history;
- a reconnect/checkpoint establishes the accepted stream position;
- only the uncertain tail is replayed.

The remaining protocol work is to make Verified history cleanly bounded through asynchronous checkpoints without turning the hot path into request/reply traffic.

# DHMP and DHMPS

**DHMP** uses the negotiated fixed-contract model over the underlying transport.

**DHMPS** carries the same model through standard platform TLS. DHMP does not invent custom cryptography.

TLS is currently a significant CPU layer and has not yet received the same depth of tuning as the plain native path.

# Benchmark discipline

DHMP contains several benchmark families and their absolute numbers should not be mixed casually.

- **Native transport/showcase:** framing, socket, ownership and integrated pipeline experiments.
- **Processor microbenchmarks:** isolate CPU/layout algorithms.
- **.NET benchmarks:** measure the actual .NET prototype and framework stacks.
- **Slow-consumer tests:** validate ownership/conflation correctness; they are not maximum-speed headline numbers.

The README shows only the current retained architecture and current headline graphs. Historical graphs, rejected variants and old .NET results remain available for reproducibility in [BENCHMARKS.md](docs/BENCHMARKS.md).

# Current status

DHMP is still experimental and is not yet a frozen interoperability specification.

The strongest retained native architecture now includes:

- Adaptive Fixed-Contract runtime direction;
- compact 32-bit-token Ring-3 `Latest`;
- fixed-width carry handling;
- contract-specialized framing;
- bounded `Every` output slabs;
- borrowed receive-slab delivery;
- direct slab-to-send forwarding;
- fused batch processing;
- optional SIMD routine selection;
- optional computation-ready SoA block contracts;
- optional batch delivery worker;
- topology-aware placement as an AutoTune candidate.

## Next engineering work

1. **Port the retained Adaptive paths into the .NET implementation** instead of continuing to optimize obsolete native models.
2. Systematically tune receive workspace, sender batch, `SO_RCVBUF`, `SO_SNDBUF`, polling and TLS settings.
3. Repeat the combined paths on physical LAN hardware with longer runs and hardware counters.
4. Measure latency as well as throughput for computation blocks, especially accumulation delay.
5. Test computation blocks with real array-native producers and consumers so sender repacking/object-construction costs are included.
6. Implement bounded Verified checkpoints.
7. Keep the protocol specification separate from runtime-specific acceleration.

Transport/runtime tuning follow-up: [TRANSPORT_TUNING_TODO.md](docs/TRANSPORT_TUNING_TODO.md)

# Documentation

- [Architecture comparison and retained model](docs/ARCHITECTURE_COMPARISON.md)
- [Protocol draft](docs/PROTOCOL_DRAFT.md)
- [Current development status](docs/CURRENT_STATUS.md)
- [Benchmark methodology and full history](docs/BENCHMARKS.md)
- [Native showcase labs](benchmarks/native-showcase)
- [Native processor labs](benchmarks/native-gen2)
- [Raw benchmark results and charts](benchmarks/results)

# Requirements

The main implementation target is **.NET 10**.

The architecture labs are currently native Linux/C so implementation ideas can be screened quickly before selected, validated paths are ported into .NET.
