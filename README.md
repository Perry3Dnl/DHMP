<p align="center">
  <img src="assets/dhmp-logo.webp" alt="DHMP logo" width="512">
</p>

# DHMP — Direct Headerless Message Protocol

DHMP is an experimental fixed-contract transport for persistent machine-to-machine communication.

The design goal is simple: **negotiate what can be known once, then keep repeated metadata, per-message allocation, queue growth, and unnecessary application work out of the steady-state hot path.**

DHMP is currently being developed as a protocol design plus reference implementations and benchmark labs. The main implementation target is .NET; native C labs are used to explore receive-path architectures before promising ideas are ported back into the .NET prototype.

> **Current highlighted `Latest` implementation:** Ring-3 Fixed-Slab Latest with a single-atomic `FRONT / MIDDLE / BACK` ownership exchange — three permanently retained state slots and one fixed reusable I/O workspace.

## At a glance

| Property | DHMP model |
| --- | --- |
| Framing | Fixed logical frame size negotiated once |
| Per-frame DHMP header | None in steady state |
| Connection model | Persistent continuous byte stream |
| Receive semantics | **Every** or **Latest** |
| Latest retention | Bounded newest-state window |
| Latest overflow | Always overwrite the oldest retained state |
| Delivery semantics | **Unconfirmed** or **Verified** |
| Secure mode | **DHMPS** over platform TLS |
| Memory model | Fixed reusable transport workspace + bounded retained state |
| Main target | Service-to-service, game/server state, telemetry, replication, devices |

## Why DHMP?

Many application protocols repeatedly send information that both peers already know after connection setup: schema identity, payload size, route information, message type, framing metadata, and other fixed contract details.

DHMP explores the opposite model:

```text
connection setup
    ↓
negotiate contract + fixed frame size + semantics
    ↓

steady state
[frame][frame][frame][frame][frame]...
```

rather than:

```text
[type][length][metadata][payload]
[type][length][metadata][payload]
[type][length][metadata][payload]
```

DHMP does **not** attempt to replace TCP reliability or TLS security. It sits above the underlying transport and removes application-level work that does not need to be repeated for every fixed-contract frame.

## Continuous data-pump model

A logical DHMP frame does not need to equal one socket read or write.

```text
TCP byte stream
      │
      ▼
┌──────────────────────────────┐
│ fixed reusable I/O workspace │
│ frame frame frame ... frame  │
└──────────────────────────────┘
      │
      ├── Every  → expose every complete frame
      │
      └── Latest → retain only newest useful state
```

This lets the implementation amortize one socket operation across many logical frames.

For `Latest`, obsolete complete frames do not need to become queued application objects at all. Once the negotiated frame size is known, the receiver can identify complete frame boundaries using fixed-size arithmetic and skip state that can no longer be observed.

## Receive semantics

### Every

`Every` exposes every complete logical frame in order.

Typical workloads:

- command streams;
- replication/change feeds;
- logs;
- telemetry where each sample matters;
- transactions or events that must not be discarded.

### Latest

`Latest` is for current-state workloads where newer state makes older state obsolete.

Typical workloads:

- game/server state;
- player or vehicle transforms;
- controller/device state;
- dashboards;
- simulation state;
- live UI state;
- high-frequency telemetry snapshots.

If the stream contains:

```text
[1][2][3][4][5]
```

and the consumer has fallen behind, a `Latest` implementation is allowed to discard stale state instead of forcing the application to process a growing backlog.

## Current Latest fast path: Ring-3 Fixed-Slab

The current highlighted native architecture uses two separate fixed-memory concepts:

1. **transport workspace** — a reusable contiguous slab that lets the OS perform efficient larger socket reads;
2. **retained state** — exactly three permanent logical frame slots.

```text
network
   │
   ▼
┌──────────────────────────┐
│ fixed receive workspace  │
│ overwritten every cycle  │
└──────────────────────────┘
   │
   │ identify complete frames
   │ skip obsolete history
   ▼
┌────────┬────────┬────────┐
│ slot 0 │ slot 1 │ slot 2 │
└────────┴────────┴────────┘
   newest three retained states
```

When all three retained slots are occupied and a newer state arrives, the **oldest retained state is always overwritten**.

The implementation does not shift old frame data forward, grow a queue, or clear memory before reuse. The role/index changes; the storage stays where it is.

For a 32-byte contract:

```text
3 × 32 B = 96 B retained application-state payload
```

The retained-state requirement stays constant regardless of how long the connection runs.

### Ring-3 reference result

![Ring-3 Fixed-Slab Latest sustained benchmark](benchmarks/results/charts/ring3-fixed-slab-32b-2026-09-23.svg)

Standalone native Linux/C localhost reference, five 2-second passes:

| Metric | Result |
| --- | ---: |
| Frame size | **32 B** |
| Median logical input rate | **86.18 M frames/s** |
| Median logical payload rate | **2.76 GB/s** |
| Median receiver CPU / logical frame | **3.71 ns** |
| Retained application-state payload | **96 B** |
| Fixed receive workspace | **12 KiB** |
| Obsolete frames skipped/overwritten | **~99.20%** |
| Measured run range | **63.02–109.09 M frames/s** |

This is a localhost architecture benchmark, not a claim that a physical network delivered 2.76 GB/s. The run range is intentionally published because scheduler and loopback conditions materially affect absolute rates.

Source: [`benchmarks/native-gen2/ring3_fixed_slab_latest.c`](benchmarks/native-gen2/ring3_fixed_slab_latest.c)  
Raw results: [`ring3-fixed-slab-latest-32b-2026-09-23.csv`](benchmarks/results/ring3-fixed-slab-latest-32b-2026-09-23.csv)

### Latest Ring-3 CPU optimization results

The Ring-3 ownership path has now been reduced from a per-slot atomic state machine to a classic SPSC triple-buffer exchange. The producer owns `BACK`, the consumer owns `FRONT`, and one 32-bit atomic `MIDDLE` token transfers ownership.

Seven alternating native CPU runs with 32-byte state and a 10 µs zero-copy consumer hold:

| Ring-3 ownership engine | Producer publications/s | Producer CPU / publication | Producer claim retries | Useful consumer publications/s | Validation errors |
| --- | ---: | ---: | ---: | ---: | ---: |
| Per-slot state machine | 30.281 M | 33.023 ns | ~30.11 M | 89.745k | 0 |
| **Single-atomic triple exchange** | **32.359 M** | **30.900 ns** | **0** | 89.308k | **0** |

That is about **+6.9% producer publication throughput** and **-6.4% producer CPU cost** while removing the producer-side slot-claim retry loop.

Two additional retained CPU-path improvements are ready for integration:

| Processor detail | Before | After | Change |
| --- | ---: | ---: | ---: |
| 32-byte negotiated contract processing | 4.809 ns/batch | **4.738 ns/batch** | **~1.5% lower** |
| Partial-frame carry handling | 4.794 ns/batch | **1.739 ns/batch** | **~63.7% lower** |

The contract-specialized path replaces runtime division/runtime-size copy with fixed 32-byte arithmetic/copy. The carry fast path replaces variable-size `memmove` with one fixed 32-byte vector transfer while retaining the actual valid carry count separately.

These are **processor-path microbenchmarks**, not new end-to-end TCP throughput figures. The native showcase v2 results below still represent the last integrated transport run; the next showcase revision will combine the single-atomic exchange, 32-byte specialization, and fixed-width carry path before new network numbers are published.

Source/results:

- [single-atomic triple exchange source](benchmarks/native-gen2/ring3_triple_exchange_ab.c)
- [single-atomic raw results](benchmarks/results/ring3-triple-exchange-ab-2026-09-23.csv)
- [contract specialization raw results](benchmarks/results/ring3-contract-specialize-ab-2026-09-23.csv)
- [fixed carry raw results](benchmarks/results/ring3-carry32-ab-2026-09-23.csv)

## Performance comparisons

DHMP has several benchmark families. Results from different harnesses are deliberately kept separate.

### .NET request/reply comparison

Persistent localhost connection, same opaque payload sizes, no TLS, sequential request/reply.

![DHMP vs common protocols](benchmarks/results/charts/readme-dhmp-vs-common-protocols.svg)

Median round trips per second:

| Payload | DHMP | TCP + 4-byte length | MessagePack/TCP | HTTP/1.1 | HTTP/2 | gRPC/Protobuf |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 32 B | **42,565** | 38,046 | 24,168 | 20,535 | 12,806 | 11,460 |
| 64 B | **53,433** | 50,496 | 45,625 | 23,393 | 10,742 | 11,357 |
| 128 B | 44,022 | **44,101** | 32,159 | 18,973 | 12,134 | 10,235 |
| 256 B | **43,794** | 43,014 | 39,797 | 22,910 | 15,578 | 10,625 |
| 512 B | **56,336** | 51,670 | 44,455 | 23,035 | 15,236 | 11,006 |
| 1 KiB | **56,239** | 51,858 | 44,465 | 22,795 | 15,369 | 13,887 |

The important baseline is lean binary TCP. DHMP should not be presented as magically making TCP itself faster; the goal is to remain close to the lean transport floor while adding fixed-contract and receive/delivery semantics.

### DHMPS vs HTTPS

DHMPS is DHMP wrapped in normal platform TLS.

![DHMPS vs HTTPS](benchmarks/results/charts/readme-dhmps-vs-https.svg)

Secure median round trips per second:

| Payload | DHMPS | HTTPS/1.1 | HTTPS/2 |
| ---: | ---: | ---: | ---: |
| 32 B | **16,712** | 6,219 | 6,419 |
| 64 B | **35,605** | 10,909 | 10,284 |
| 128 B | **42,763** | 11,567 | 15,128 |
| 256 B | **35,124** | 14,377 | 11,728 |
| 512 B | **38,932** | 12,830 | 13,255 |
| 1 KiB | **38,133** | 12,974 | 11,999 |

TLS setup is outside the steady-state timing in this comparison.

### Native 32-byte transport/framing showcase v2

Ring-3 is now implemented **inside the same native showcase harness** as Ring8, Slab6, DHMPS, raw TCP, length-prefixed TCP, WebSocket framing, HTTP chunk framing, and UDP. All rows below therefore use the same timing model and workload.

Test profile: 32-byte logical payloads, 12 KiB fixed receive workspace, 500 ms warmup, 1.5 s measured interval, three runs per path, 10 µs simulated consumer work, CPU pinning when available, and TLS 1.3 for DHMPS.

![Native 32 B input rate — showcase v2](benchmarks/results/charts/showcase-v2-32b-input-2026-09-23.svg)

![Native 32 B useful publication rate — showcase v2](benchmarks/results/charts/showcase-v2-32b-published-2026-09-23.svg)

| Path | Logical input frames/s | Useful publications/s | Retained state |
| --- | ---: | ---: | ---: |
| DHMP Slab6 | **179.2 M** | **78.2k** | 192 B |
| **DHMP Ring-3 Fixed-Slab** | **174.2 M** | **74.1k** | **96 B** |
| DHMP Ring8 | 150.0 M | 72.8k | 256 B |
| HTTP/1.1 / chunk framing | 123.7 M | 75.0k | 64 B* |
| TCP / 4-byte length | 119.5 M | 72.0k | 64 B* |
| WebSocket / binary framing | 104.7 M | 60.8k | 64 B* |
| Raw TCP / fixed frame | 73.0 M | 52.6k | 64 B* |
| DHMPS / TLS / Ring-3 | 65.1 M | 71.6k | 96 B |
| UDP / datagram | 0.37 M | 86.1k | 64 B* |

`*` The non-DHMP rows use the harness's small publication state storage; those bytes are not protocol-level retention guarantees.

The key Ring-3 result is the trade-off: it lands very close to Slab6 on both ingest and useful publication rate while retaining only **three 32-byte states = 96 B**, half the retained state of Slab6 and substantially less than Ring8.

The WebSocket and HTTP rows are framing/parser microbenchmarks, **not full ASP.NET Core server stacks**. UDP in showcase v2 is an unbatched datagram path, so it should not be read as a general UDP ceiling. All retained runs reported zero validation errors.

Raw data: [showcase-v2-32b-raw-3run-2026-09-23.csv](benchmarks/results/showcase-v2-32b-raw-3run-2026-09-23.csv)  
Summary: [showcase-v2-32b-summary-3run-2026-09-23.csv](benchmarks/results/showcase-v2-32b-summary-3run-2026-09-23.csv)

## Continuous streaming results

The .NET continuous-streaming benchmark removes request/reply waiting and sends fixed-size frames continuously.

| Payload | DHMP frames/s | Payload throughput |
| ---: | ---: | ---: |
| 16 B | ~206k | ~3.15 MiB/s |
| 32 B | ~228k | ~6.95 MiB/s |
| 64 B | ~224k | ~13.65 MiB/s |
| 128 B | ~222k | ~27.15 MiB/s |
| 256 B | ~225k | ~54.92 MiB/s |
| 512 B | ~218k | ~106.41 MiB/s |
| 1 KiB | ~207k | ~202.21 MiB/s |
| 2 KiB | ~192k | ~374.33 MiB/s |
| 4 KiB | ~179k | ~697.79 MiB/s |
| 8 KiB | ~141k | ~1.10 GiB/s |

These results illustrate the transition from per-operation overhead at small frame sizes toward byte movement, socket buffering, and memory bandwidth at larger sizes.

## Delivery semantics

Receive semantics and delivery/recovery semantics are separate.

### Unconfirmed

The sender continuously emits frames without requiring DHMP-level proof that the peer accepted every logical frame.

TCP still provides ordered reliable byte delivery while the connection remains alive. If the session dies, uncertain application frames may be abandoned.

### Verified

Verified keeps the same no-wait hot data stream:

- no per-frame ACK;
- no periodic ACK merely because data is flowing;
- sender continues sending;
- sender retains uncertain logical history;
- reconnect/verification establishes the receiver's accepted stream position;
- only the uncertain tail is replayed.

The remaining design problem is **bounded Verified history**: asynchronous checkpoints need to confirm an accepted position so old retained history can be released without turning the normal stream into request/reply traffic.

## DHMP and DHMPS

**DHMP** is the plain application transport.

**DHMPS** is DHMP carried through normal platform TLS.

DHMPS does not invent custom cryptography. TLS framing exists below DHMP, while DHMP itself keeps the same fixed-contract application-frame model.

## Intended use cases

DHMP is being explored for workloads such as:

- service-to-service communication;
- multiplayer game/server state;
- controller/device communication;
- real-time state distribution;
- telemetry;
- simulation;
- cache synchronization;
- replication/change feeds;
- log shipping;
- edge/backend synchronization.

The strongest fit is communication that is:

- persistent;
- high-frequency;
- fixed-contract;
- small-to-medium message oriented;
- sensitive to allocation, queue growth, or stale-state processing.

DHMP is **not** intended to reproduce every feature of HTTP, gRPC, Kafka, MQTT, QUIC, or a message broker inside one protocol. Higher-level features should remain layered above the small transport core.

## Benchmark discipline

DHMP uses several different benchmark harnesses. Their absolute numbers should not be mixed casually.

- **.NET protocol benchmarks** compare real .NET stacks and application APIs.
- **native showcase benchmarks** compare framing/ingestion paths inside one C harness.
- **processor-path labs** explore algorithms and memory models.
- **Ring-3 standalone tests** validate the newest bounded-state implementation shape.

A higher native-lab number does not mean the .NET implementation currently achieves the same rate, and a logical payload rate does not mean a physical NIC transferred that many bytes per second.

Raw CSV files, source code, methodology, and caveats are retained so results remain reproducible.

## Current status

DHMP is experimental work and is not yet a frozen interoperability specification.

Established so far:

- fixed-contract/headerless steady-state framing;
- arbitrary negotiated fixed frame sizes;
- `Every` and `Latest` receive semantics;
- Ring-3 bounded newest-state implementation experiments;
- single-atomic `FRONT / MIDDLE / BACK` Ring-3 ownership exchange;
- overwrite-oldest Latest behavior;
- fixed reusable I/O workspace;
- plain DHMP and TLS-wrapped DHMPS;
- Unconfirmed delivery;
- no-periodic-ACK Verified recovery;
- reconnect/replay of uncertain Verified tails;
- reusable-slab/bulk-I/O implementation model;
- comparisons against lean TCP, MessagePack, WebSocket, HTTP, HTTPS, UDP, and gRPC.

Current engineering priorities:

1. integrate the retained Ring-3 CPU wins into the native TCP showcase and publish a new combined run;
2. port Ring-3 Fixed-Slab Latest and the single-atomic ownership path into the .NET prototype;
3. extend the integrated Ring-3 showcase across more frame sizes;
4. run fixed-duration LAN tests across physical machines;
5. tune receive-workspace sizing across negotiated frame sizes;
6. implement bounded Verified checkpoints;
7. continue separating protocol specification from implementation details;
8. build ergonomic .NET/Unity-facing APIs without adding per-frame wire overhead.

## Documentation

- [Protocol draft](docs/PROTOCOL_DRAFT.md)
- [Current development status](docs/CURRENT_STATUS.md)
- [Benchmark methodology and detailed results](docs/BENCHMARKS.md)
- [Native Gen-2 processor lab](benchmarks/native-gen2)
- [Raw benchmark results and charts](benchmarks/results)

## Requirements

The main prototype targets **.NET 10**.

A separate Linux/C native lab under `benchmarks/native-gen2` is used for architecture and processor-path experiments before selected ideas are ported to the .NET implementation.
