# DHMP current development status

Updated: 2026-09-23

The retained direction is now **DHMP Adaptive Fixed-Contract**: the application explicitly chooses `Every` or `Latest`, the handshake fixes the record/block contract, and the runtime selects the fastest validated implementation path that preserves those semantics.

See the consolidated design: [ARCHITECTURE_COMPARISON.md](ARCHITECTURE_COMPARISON.md).

## Current retained profiles

### Latest

- fixed reusable receive workspace;
- early mathematical conflation of obsolete complete states;
- compact Ring-3 `FRONT / MIDDLE / BACK`;
- 96 B retained application payload for a 32-byte contract;
- one shared 32-bit MIDDLE token;
- producer exchange + consumer CAS;
- fixed-width carry handling;
- contract-specialized framing.

The consumer-exchange alternative is ownership-correct but remains rejected as the speed default because it removed retries without producing a CPU win.

### Every / ordinary records

- bounded reusable slab pool;
- one ownership publication per populated slab rather than per result;
- borrowed receive slab when the wire representation is directly consumable;
- output slab only when conversion requires a different representation;
- batch-first adapter;
- direct slab-to-send forwarding when results are already wire-ready;
- bounded backpressure rather than overwriting unread results.

The strongest integrated Every handoff A/B measured **129.80 M results/s** for output-slab publication versus **33.35 M/s** for per-result publication, with zero validation errors.

### Every / ComputeBlock

Optional numerical contract:

- fixed computation-ready block negotiated once;
- field-major SoA representation travels over the wire;
- contiguous field arrays feed the selected SIMD routine directly;
- no receiver-side AoS→SoA transpose when producer and consumer both use the block representation.

Processor-only AVX2 cost measured **0.621 ns/result** for SoA versus **1.080 ns/result** for AoS+transpose, about **42.5% lower compute cost / 1.74× processor rate**.

The integrated gain was much smaller because transport and handoff then dominated, so ComputeBlock remains a specialization rather than the default record format.

## Current retained optimization evidence

| Area | Retained result |
| --- | ---: |
| Every output-slab publication | 33.35 → **129.80 M/s** |
| Borrowed receive slab | 119.63 → **126.51 M/s** |
| Direct slab-to-send | 41.93 → **43.98 M/s** |
| Fused batch processing | 30.82 → **35.92 M/s** |
| ComputeBlock AoS AVX2 → SoA AVX2 | 1.080 → **0.621 ns/result** |
| Dedicated delivery worker | **+18.7% to +48.5%** wall-clock in tested conversion weights |
| Ring-3 single-token producer ownership | **~+6.9% producer throughput** |
| Fixed 32-byte carry | **~63.7% lower carry housekeeping** |
| Cache-line-padded Ring-3 | **~78% worse producer update cost** — rejected |

Percentages and absolute rates above come from different controlled A/B harnesses and are not additive.

## Public/runtime API direction

The high-performance public surface should be batch-first.

Conceptually:

```text
Every:
  lease/view of populated slab
  → developer consumes batch
  → release lease

Latest:
  newest retained state
  → developer consumes state
```

Per-message convenience APIs can sit above the batch surface, but should not force the core to perform one ownership transfer or cross-thread callback per record.

## Delivery worker

A dedicated worker is retained as an optional runtime feature when it performs real work such as decoding, conversion, or adapter preparation.

It improves wall-clock throughput by overlapping protocol receive work and delivery preparation, but can increase aggregate CPU and can regress on poor CPU/cache placement.

Therefore it belongs in runtime configuration/AutoTune, not in the wire protocol.

## CPU/cache placement

Topology-aware placement is promising but not yet a universal rule.

The isolated Ring-3 handoff improved on one reported cache-close pair, while the full loopback test did not consistently prefer that placement. The benchmark host is shared/virtualized.

Retain topology awareness as an AutoTune candidate and repeat on physical multi-core/NUMA hardware.

## Transport/runtime tuning

Still open and high-value:

- receive workspace size sweep;
- sender batch-size sweep;
- `SO_RCVBUF` / `SO_SNDBUF`;
- `TCP_NODELAY` / coalescing strategy;
- polling and busy-poll behavior;
- CPU/NIC affinity;
- `io_uring`, registered buffers and multishot receive;
- TLS record/buffer sizing;
- kTLS/zero-copy paths where appropriate.

Tracked in [TRANSPORT_TUNING_TODO.md](TRANSPORT_TUNING_TODO.md).

## Cross-protocol benchmark status

The current common native framing benchmark is **showcase v6**, audited and rerun with one 32-byte logical message per framing unit.

v6 remains a framing-kernel comparison rather than a full production-stack benchmark. Raw fixed TCP is slightly faster than DHMP in the retained v6 medians, which is the expected transport-floor sanity check.

## Verified delivery

Retained semantic direction:

- no per-frame or periodic hot-path ACK requirement;
- sender retains uncertain history;
- reconnect/checkpoint identifies accepted position;
- replay only the uncertain tail.

The remaining implementation task is bounded asynchronous checkpoint/history reclamation.

## Current engineering priorities

1. Port the retained Adaptive paths into the .NET implementation.
2. Build one fresh integrated Adaptive benchmark harness.
3. Rerun the protocol comparison from current code and regenerate the README comparison graph.
4. Tune transport/runtime settings.
5. Repeat on physical LAN hardware with longer runs and hardware counters.
6. Measure ComputeBlock accumulation latency and sender-side layout cost with real array-native producers.
7. Implement bounded Verified checkpoints.
8. Continue separating protocol rules from implementation accelerators.
