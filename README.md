<p align="center">
  <img src="assets/dhmp-logo.webp" alt="DHMP logo" width="512">
</p>

# DHMP — Direct Headerless Message Protocol

DHMP is an experimental fixed-contract application transport designed to keep repeated metadata, per-message allocation, and unnecessary application work out of the hot path.

The central idea is simple:

- negotiate the contract once when a connection/session is established;
- use the negotiated fixed logical frame size as framing;
- keep steady-state application traffic headerless at the DHMP layer;
- treat the connection as a continuous byte stream rather than one socket operation per logical message;
- support both **Every** and **Latest** receive semantics;
- support plain **DHMP** and TLS-wrapped **DHMPS**;
- keep delivery/recovery semantics separate from receive semantics.

This repository currently contains the protocol design and .NET 10 benchmark prototype. Some experiment code still uses the earlier internal name `FixedWire`; those names are being left alone until the protocol model is stable.

## Why DHMP?

Many application protocols repeat information on every message that is already known after connection setup: message type, schema identity, payload length, routing metadata, and other framing data.

DHMP explores the opposite model:

```text
handshake:
  establish contract + frame size + session semantics

steady state:
  [frame][frame][frame][frame][frame]...
```

instead of:

```text
[type][length][metadata][payload]
[type][length][metadata][payload]
[type][length][metadata][payload]
```

The goal is not to replace TCP reliability or TLS security. DHMP sits above the transport and removes application-level work that does not need to be repeated for every fixed-contract frame.

## Data-pump model

DHMP is intended to operate as a continuous reusable-memory data pump.

A socket read may contain many logical DHMP frames:

```text
TCP byte stream
      |
      v
+------------------------------+
| reusable receive slab        |
| frame frame frame ... frame  |
+------------------------------+
      |
      +--> Every  -> process every complete frame
      |
      +--> Latest -> publish only the newest complete frame
```

A logical frame boundary does **not** need to equal a socket read/write boundary.

The current model-design benchmark uses reusable 256 KiB send and receive slabs so thousands of logical frames can be amortized across one socket operation.

For **Latest**, stale complete frames do not need to become queued application messages at all. They can be skipped by fixed-size arithmetic while preserving only the newest complete state and any trailing partial frame.

## Receive semantics

DHMP currently defines two receive policies.

### Every

Expose every complete logical frame in order.

Useful for workloads such as replication, command streams, logs, telemetry where every event matters, or any application that must inspect every frame.

### Latest

Expose the newest complete state available when the consumer runs and intentionally skip obsolete complete states.

Useful for controller state, live UI state, game state, dashboards, device state, or other workloads where freshness is more important than processing every intermediate update.

These receive policies are independent from delivery/recovery semantics.

## Delivery semantics

### Unconfirmed

The sender continuously emits frames and does not require DHMP-level proof that the peer accepted them.

TCP still provides normal ordered/reliable byte delivery while the connection is alive. If the session dies, uncertain application frames may be abandoned.

### Verified

The normal data path remains the same continuous stream:

- no per-frame ACK;
- no periodic ACK just because data is flowing;
- sender does not wait before sending the next frame;
- sender retains unverified logical history;
- reconnect/verification establishes the receiver's accepted stream position;
- only the uncertain tail is replayed.

Because retained history cannot grow forever, Verified ultimately requires asynchronous checkpoints so old confirmed history can be discarded without changing the hot data path.

## DHMP and DHMPS

**DHMP** is the plain transport.

**DHMPS** is DHMP carried over normal platform TLS.

DHMPS does not invent custom cryptography. The current .NET prototype uses the platform TLS stack. TLS framing exists below DHMP, but DHMP itself still does not add a repeated application header to every logical frame.

## Performance at a glance

The front-page comparison focuses on the current **.NET localhost protocol benchmarks**, where DHMP and DHMPS are measured against familiar application-protocol stacks using the same payload sizes.

![DHMP vs common protocols](benchmarks/results/charts/readme-dhmp-vs-common-protocols.svg)

In the full persistent request/reply benchmark, DHMP is compared with length-prefixed TCP, MessagePack/TCP, HTTP/1.1, HTTP/2, and gRPC/Protobuf. These are **round trips per second**, not raw network bandwidth.

![DHMPS vs HTTPS](benchmarks/results/charts/readme-dhmps-vs-https.svg)

The secure comparison keeps connection/TLS setup outside the steady-state timing and compares DHMPS with HTTPS/1.1 and HTTPS/2 using the same opaque payload sizes.

These charts are intended as a clear implementation-level comparison, not a claim that DHMP makes TCP itself faster. Native processor-path experiments, wire/framing microbenchmarks, raw CSV files, methodology, and caveats remain documented in [docs/BENCHMARKS.md](docs/BENCHMARKS.md) and [benchmarks/results](benchmarks/results).

### Newest native Latest fast path

The latest repository update adds the **Ring-3 Fixed-Slab Latest** reference path. It keeps only three permanent application-state frames while using a separate fixed receive workspace for efficient socket I/O.

![Ring-3 Fixed-Slab Latest sustained benchmark](benchmarks/results/charts/ring3-fixed-slab-32b-2026-09-23.svg)

The five 2-second passes measured **63.02–109.09 M logical 32-byte frames/s**, with a **median of 86.18 M/s** (2.76 GB/s payload), **3.71 ns receiver CPU per logical frame**, and only **96 B of retained application-state payload**. This native/Linux architecture result is shown separately from the .NET protocol charts above because the harnesses are not directly comparable.

## Latest benchmark results

Raw CSV files and SVG charts are stored under [benchmarks/results](benchmarks/results).

### Continuous streaming

The continuous-streaming benchmark removes request/reply waiting and sends fixed-size frames continuously.

For DHMP, the measured end-to-end frame rate stayed roughly flat from 16 B through 1 KiB while payload throughput increased substantially:

| Payload | Frames/sec | Payload throughput |
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

This is consistent with the intended model: for small and medium fixed frames, per-operation cost dominates first; as frames become larger, byte movement, TCP, memory copies, and socket buffering become the limiting factors.

### Reusable-slab model test

The newer model-design benchmark removes the artificial one-frame-per-socket-operation behavior.

At **32-byte frames**:

| Mode | Logical input frames/sec | Frames/socket read | Published | Skipped |
| --- | ---: | ---: | ---: | ---: |
| DHMP Every | ~17.2 M | ~6,096 | 100% | 0% |
| DHMP Latest | ~195.7 M | 8,192 | ~0.0122% | ~99.9878% |

The Latest number is **not** 195.7 million application callbacks per second.

It represents logical 32-byte frame-equivalents passing through the fixed byte stream while the receiver publishes only the newest useful state from each large receive batch.

In that run, a single 256 KiB receive contained:

```text
262,144 bytes / 32 bytes = 8,192 logical frames
```

and Latest could skip stale states without individually dispatching them.


### Ring-3 Fixed-Slab Latest fast path

The current native architecture lab now includes an experimental **Ring-3 Fixed-Slab Latest** receive engine.

It keeps only three permanent state slots and always overwrites the oldest retained state. Network I/O lands in one fixed reusable contiguous slab so socket calls can stay large; obsolete complete frames are skipped by fixed-size arithmetic, and only the newest three complete frames from a receive batch are copied into the Ring-3 state window.

At a 32-byte fixed contract:

- retained application-state storage is **3 × 32 B = 96 B**;
- the reference benchmark uses a fixed **12 KiB** receive workspace;
- there is no per-frame queue growth, frame shifting, or buffer clearing;
- steady-state frame storage is reused indefinitely.

A standalone Linux/C localhost reference run added on 2026-09-23 used five 2-second passes. The median was:



| Metric | Ring-3 Fixed-Slab Latest |
| --- | ---: |
| Logical input rate | **86.2 M frames/s** |
| 32 B logical payload rate | **2.76 GB/s** |
| Receiver CPU / logical frame | **3.71 ns** |
| Retained state payload | **96 B** |
| Obsolete frames skipped/overwritten | **~99.20%** |

The individual passes ranged from roughly 63 M/s to 109 M/s, showing that localhost scheduling and shared-host conditions materially affect absolute results. Earlier tuned architecture experiments with the same general slab-to-small-state-ring shape reached higher burst rates, but the reproducible standalone reference result above is the value retained for documentation.

This is an **implementation fast path for `Latest` semantics**, not a new per-frame wire format. Source: [`ring3_fixed_slab_latest.c`](benchmarks/native-gen2/ring3_fixed_slab_latest.c). Raw results: [CSV](benchmarks/results/ring3-fixed-slab-latest-32b-2026-09-23.csv).

### Benchmark caveat

The reusable-slab model run currently uses a small byte budget, so some timed regions are only a few milliseconds long. The extreme multi-million-frame figures therefore demonstrate the **architecture and batching mechanism**, not yet a sustained throughput ceiling.

The next benchmark step is fixed-duration multi-second passes before treating those burst numbers as publishable sustained performance claims.

See [docs/BENCHMARKS.md](docs/BENCHMARKS.md) for methodology and caveats.

## Current status

DHMP is experimental work, not yet a published interoperability specification.

What has been established so far:

- fixed-contract/headerless steady-state framing;
- arbitrary negotiated fixed frame sizes;
- Every and Latest receive semantics;
- plain DHMP and TLS-wrapped DHMPS;
- Unconfirmed and no-periodic-ACK Verified semantics;
- reconnect/replay of uncertain Verified tails;
- reusable-slab/bulk-I/O implementation model;
- experimental Ring-3 Fixed-Slab Latest path with constant three-frame retained state;
- benchmark comparisons against raw TCP, MessagePack, WebSocket, HTTP/1.1, HTTP/2, HTTPS, UDP and gRPC.

The next major protocol problem is **bounded Verified mode**: asynchronous verification/checkpoints must allow retained history to be released without adding ACK chatter to the normal data stream.

The current benchmark focus is the **processor/publication path**: selecting efficient internal strategies for `Every` and `Latest` without changing their protocol semantics.

## Intended use cases

DHMP is being explored for workloads such as:

- service-to-service communication;
- controller/device state;
- real-time state distribution;
- game/server state;
- telemetry;
- cache synchronization;
- replication/change feeds;
- log shipping;
- edge/backend synchronization.

It is not intended to reproduce every feature of HTTP, gRPC, Kafka, MQTT, QUIC, or message brokers inside one protocol. Higher-level concerns should stay layered above the small transport core.

## Documentation

- [Protocol draft](docs/PROTOCOL_DRAFT.md)
- [Current development status](docs/CURRENT_STATUS.md)
- [Benchmark methodology and results](docs/BENCHMARKS.md)
- [Raw benchmark results and charts](benchmarks/results)

## Requirements

The main prototype targets **.NET 10**. A separate Linux/C native lab under `benchmarks/native-gen2` is used only for rapid processor-path architecture experiments.
