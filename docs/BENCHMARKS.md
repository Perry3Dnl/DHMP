# Benchmark notes and reference results

These are experimental localhost results from the current .NET 10 prototype. They are useful for comparing revisions on the same machine; they are not universal performance claims.

## Full protocol comparison

Configuration: persistent localhost connection, no TLS, no compression, one sequential request/reply at a time, identical N-byte opaque application frame in request and response, 10,000 warmup iterations, 100,000 iterations, 5 passes.

Median round trips/second:

| Bytes | DHMP | TCP + 4-byte length binary | MessagePack/TCP | HTTP/1.1 binary | HTTP/2 binary | gRPC/Protobuf |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 32 | 42,565 | 38,046 | 24,168 | 20,535 | 12,806 | 11,460 |
| 64 | 53,433 | 50,496 | 45,625 | 23,393 | 10,742 | 11,357 |
| 128 | 44,022 | 44,101 | 32,159 | 18,973 | 12,134 | 10,235 |
| 256 | 43,794 | 43,014 | 39,797 | 22,910 | 15,578 | 10,625 |
| 512 | 56,336 | 51,670 | 44,455 | 23,035 | 15,236 | 11,006 |
| 1024 | 56,239 | 51,858 | 44,465 | 22,795 | 15,369 | 13,887 |

The main comparison to keep in perspective is DHMP versus raw length-prefixed binary TCP. Both are deliberately lean and often close.

## DHMP/DHMPS versus HTTP/HTTPS quick comparison

Configuration: persistent localhost connection, same opaque N-byte request/response body, no compression, TLS setup outside the steady-state timing, 500 warmups, 2,000 iterations, 3 passes.

Secure median round trips/second:

| Bytes | DHMPS | HTTPS/1.1 binary | HTTPS/2 binary |
| ---: | ---: | ---: | ---: |
| 32 | 16,712 | 6,219 | 6,419 |
| 64 | 35,605 | 10,909 | 10,284 |
| 128 | 42,763 | 11,567 | 15,128 |
| 256 | 35,124 | 14,377 | 11,728 |
| 512 | 38,932 | 12,830 | 13,255 |
| 1024 | 38,133 | 12,974 | 11,999 |

This quick test is directional only. The architecture and API/tooling differences between these transports are much larger than a single sequential latency benchmark captures.

## Delivery-mode no-ACK run

The raw CSV is stored under `benchmarks/results/delivery-mode-results-2026-09-22.csv`.

A benchmark bookkeeping issue currently reports retained-frame counters for Unconfirmed as well; those counters should not be interpreted as actual Unconfirmed protocol retention. The Verified replay/missing/recovery measurements are the relevant fields.

## Continuous streaming landscape — 2026-09-22

Raw data: [streaming-landscape-results-2026-09-22.csv](../benchmarks/results/streaming-landscape-results-2026-09-22.csv)

Charts:

- [grouped frames/sec](../benchmarks/results/charts/streaming-grouped-frames-2026-09-22.svg)
- [performance relative to DHMP](../benchmarks/results/charts/streaming-relative-to-dhmp-2026-09-22.svg)
- [frames/sec scaling](../benchmarks/results/charts/streaming-scaling-frames-2026-09-22.svg)
- [payload MiB/sec scaling](../benchmarks/results/charts/streaming-scaling-mibps-2026-09-22.svg)

This benchmark removed application request/reply waiting and continuously streamed fixed-size frames. It is a better view of frame-size scaling than the earlier unary round-trip test.

DHMP end-to-end medians from the run:

| Payload | Frames/sec | Payload MiB/sec |
| ---: | ---: | ---: |
| 16 B | 206,206 | 3.15 |
| 32 B | 227,854 | 6.95 |
| 64 B | 223,684 | 13.65 |
| 128 B | 222,411 | 27.15 |
| 256 B | 224,966 | 54.92 |
| 512 B | 217,922 | 106.41 |
| 1 KiB | 207,060 | 202.21 |
| 2 KiB | 191,655 | 374.33 |
| 4 KiB | 178,634 | 697.79 |
| 8 KiB | 141,064 | 1,102.07 |
| 16 KiB | 89,845 | 1,403.82 |
| 32 KiB | 76,298 | 2,384.32 |
| 64 KiB | 24,357 | 1,522.31 |

The useful pattern is that DHMP stayed near roughly 206k–228k frames/sec from 16 B through 1 KiB while payload throughput rose from about 3.15 MiB/sec to 202 MiB/sec. This supports the design expectation that small/medium fixed frames are dominated by per-operation overhead until byte movement becomes the limiting factor.

## Reusable-slab / model-design experiment — 2026-09-22

Raw data: [model-design-results-2026-09-22.csv](../benchmarks/results/model-design-results-2026-09-22.csv)

Charts:

- [Every semantics](../benchmarks/results/charts/model-design-every-2026-09-22.svg)
- [Latest semantics](../benchmarks/results/charts/model-design-latest-2026-09-22.svg)

This experiment was changed to match the intended DHMP data-pump model more closely:

- a reusable 256 KiB send slab and receive slab;
- many logical fixed-size frames per socket I/O operation;
- no per-frame queue or message allocation;
- **Every** validates/exposes every complete logical frame;
- **Latest** drains the byte stream but skips obsolete complete frames and publishes only the newest complete state from a receive batch;
- trailing partial frames are preserved for the next read.

The 32-byte run illustrates the mechanism:

| Mode | Input frames/sec | Payload MiB/sec | Frames/socket read | Published | Skipped |
| --- | ---: | ---: | ---: | ---: | ---: |
| DHMP Every | 17.20 M | 525.0 | 6,096 | 100% | 0% |
| DHMP Latest | 195.67 M | 5,971.5 | 8,192 | 0.0122% | 99.9878% |

The Latest number means logical frame-equivalents flowing through the fixed stream, **not 195 million application callbacks/sec**. In that run one 256 KiB receive contained 8,192 logical 32-byte frames and Latest only needed to publish the newest complete state.

### Important timing caveat

The current model-design run is an architecture-validation benchmark, not yet a sustained-throughput claim. Its byte-budgeted quick passes can be only a few milliseconds long; the 32-byte Latest timed region is roughly 1.3 ms at the observed throughput. Large differences between otherwise similar slab baselines show that socket batching, scheduling and short-run timing materially affect the absolute rates.

Therefore the multi-million-frame figures should currently be read as evidence that the implementation can amortize one socket operation across thousands of logical frames. The next performance run should use fixed wall-clock measurement windows (for example 2–5 seconds per pass) before quoting sustained message-rate ceilings.

## Gen-2 processor-path screening — 2026-09-22

The processor experiments have been consolidated into a two-run screening format.

Retained anchor sizes:
- 16 B
- 32 B
- 256 B
- 1 KiB

Test split:
- **Every:** zero simulated consumer work; every complete frame must be processed.
- **Latest:** 10 µs simulated consumer work; stale complete states may be conflated.
- adaptive slab target: `clamp(frameSize * 512, 16 KiB, 1 MiB)`.
- selected retained points reported zero validation errors.

Consolidated medians: [gen2-processor-summary-2026-09-22.csv](../benchmarks/results/gen2-processor-summary-2026-09-22.csv)

Raw retained two-run source data: [gen2-raw-2run-results-2026-09-22.zip](../benchmarks/results/gen2-raw-2run-results-2026-09-22.zip)

Charts:
- [Every input/processed rate](../benchmarks/results/charts/gen2-every-input-2026-09-22.svg)
- [Latest input rate](../benchmarks/results/charts/gen2-latest-input-2026-09-22.svg)
- [Latest useful publication rate](../benchmarks/results/charts/gen2-latest-published-2026-09-22.svg)

Key retained results:

| Frame | Every baseline | Best Every tested | Latest baseline input | Max Latest input | Latest baseline published | Max published |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 16 B | 269.5 M/s | 448.0 M/s | 349.2 M/s | 484.5 M/s | 79.7k/s | 81.8k/s |
| 32 B | 196.5 M/s | 228.6 M/s | 149.2 M/s | 283.3 M/s | 69.5k/s | 86.5k/s |
| 256 B | 43.2 M/s | 43.2 M/s | 36.1 M/s | 36.1 M/s | 71.6k/s | 81.0k/s |
| 1 KiB | 8.06 M/s | 9.09 M/s | 8.22 M/s | 8.85 M/s | 53.0k/s | 66.1k/s |

The winning implementation is not the same at every size or for every objective. For example, at 32 B Ring8 Spin maximized input rate, while Slab6 Hybrid produced more useful states. At 256 B the baseline retained the highest input rate while Slab5 Spin improved publication throughput at the cost of ingest rate.

This supports an implementation-level adaptive strategy selected after the fixed contract is known, rather than adding a new per-frame protocol feature.

### Native-lab caveat

These Gen-2 results come from the Linux/C architecture lab. They are intended to compare processor-path algorithms under the same local conditions. They are **not directly comparable** to the Windows/.NET benchmark rates elsewhere in this document and are not production throughput claims.

The source lab archive is stored at [benchmarks/native-gen2/DHMP-native-gen2-source.zip](../benchmarks/native-gen2/DHMP-native-gen2-source.zip).

## Native showcase comparison — 2026-09-22

This run places the two retained DHMP Latest processor models and a TLS-wrapped DHMPS variant beside five familiar wire/framing baselines in one native Linux/C harness.

Configuration:
- 32 B and 256 B application payloads
- two measured runs per point
- 1.5 s per run
- 10 µs simulated consumer work
- persistent localhost connections
- setup/handshakes excluded from the timed region
- no compression
- Latest/conflating consumer semantics applied to every case
- DHMP adaptive slab target: `clamp(frameSize * 512, 16 KiB, 1 MiB)`
- zero validation errors in the retained runs

Raw data: [showcase-raw-2run-2026-09-22.csv](../benchmarks/results/showcase-raw-2run-2026-09-22.csv)

Summary: [showcase-summary-2run-2026-09-22.csv](../benchmarks/results/showcase-summary-2run-2026-09-22.csv)

Charts:
- [32 B input rate](../benchmarks/results/charts/showcase-32b-input-2026-09-22.svg)
- [256 B input rate](../benchmarks/results/charts/showcase-256b-input-2026-09-22.svg)

Two-run mean input rates:

| Path | 32 B | 256 B |
| --- | ---: | ---: |
| DHMP Latest / Ring8 Spin | 92.63 M/s | 12.84 M/s |
| DHMP Latest / Slab6 Hybrid | 100.35 M/s | 11.27 M/s |
| DHMPS / TLS / Slab6 Hybrid | 66.51 M/s | 8.49 M/s |
| Raw TCP / fixed-size | 93.94 M/s | 14.26 M/s |
| TCP / 4-byte length | 107.79 M/s | 13.04 M/s |
| UDP / batched datagrams | 0.626 M/s | 0.616 M/s |
| WebSocket / binary framing | 33.50 M/s | 4.39 M/s |
| HTTP/1.1 / chunk framing | 105.47 M/s | 10.80 M/s |

Interpretation: the lean raw/length-prefixed stream baselines remain close to or above DHMP in parts of this microbenchmark, which is expected. DHMP's intended value is not an impossible speedup over raw TCP; it is retaining a very thin hot path while adding negotiated fixed contracts and explicit `Every`/`Latest` semantics. DHMPS shows the cost of standard TLS while retaining the same application framing model.

The WebSocket and HTTP cases here are framing/parser microbenchmarks, not complete framework stacks. For actual ASP.NET Core/gRPC/WebSocket stack comparisons, use the .NET landscape sections above rather than mixing the two harnesses.

## Native showcase: DHMP models, DHMPS, and five baselines — 2026-09-23

Raw data:

- [showcase-32b-raw-2run-2026-09-23.csv](../benchmarks/results/showcase-32b-raw-2run-2026-09-23.csv)
- [showcase-32b-summary-2run-2026-09-23.csv](../benchmarks/results/showcase-32b-summary-2run-2026-09-23.csv)

Charts:

- [input rate](../benchmarks/results/charts/showcase-32b-input-2026-09-23.svg)
- [useful publication rate](../benchmarks/results/charts/showcase-32b-published-2026-09-23.svg)

Configuration: native Linux/C localhost, 32-byte logical payloads, 10 µs simulated consumer work, adaptive slab target `clamp(frameSize × 512, 16 KiB, 1 MiB)`, 500 ms warmup, 1.5 second measured interval, 2 runs per path, CPU pinning when available, and TLS 1.3 for DHMPS.

| Path | Input frames/s | Useful publications/s |
| --- | ---: | ---: |
| DHMP Ring8 Spin | 111.8 M | 59.1k |
| DHMP Slab6 Hybrid | 124.7 M | 28.3k |
| DHMPS / TLS / Slab6 | 58.6 M | 37.4k |
| Raw TCP / fixed frame | 125.9 M | 29.2k |
| TCP / 4-byte length | 141.7 M | 18.4k |
| UDP / batched datagrams | 0.46 M | 6.8k |
| WebSocket / binary framing | 107.3 M | 43.7k |
| HTTP/1.1 / chunk framing | 29.4 M | 11.2k |

All retained runs completed with zero validation errors.

This is a hot-path framing/ingestion/conflation microbenchmark, not a universal protocol ranking. The WebSocket and HTTP rows measure their framing/parsing path rather than complete application-server stacks. The TCP baselines are intentionally lean and should be expected to remain extremely competitive. DHMPS uses normal TLS 1.3; TLS handshake time is outside the measured steady-state interval.


## Ring-3 Fixed-Slab Latest reference — 2026-09-23

This experiment separates **I/O workspace** from **retained application state**.

The receiver uses:

- a negotiated fixed frame size;
- one fixed reusable contiguous receive workspace;
- exactly three permanent retained frame slots;
- overwrite-oldest semantics when newer state arrives;
- no per-frame allocation;
- no queue growth;
- no shifting of retained frames;
- no clearing of old frame memory;
- fixed-size arithmetic to skip obsolete complete frames before publication.

The three retained slots are the semantic state window. The receive slab is transport workspace only and is overwritten by the next socket read.

For the retained 32-byte reference:

- frame size: 32 B
- retained Ring-3 payload memory: 96 B
- receive workspace: 12 KiB
- transport: TCP loopback
- language/runtime: native C on Linux
- compiler profile used for the reference executable: `-O3 -march=native -pthread`
- measured interval: 2 seconds
- retained passes: 5

Raw data: [ring3-fixed-slab-latest-32b-2026-09-23.csv](../benchmarks/results/ring3-fixed-slab-latest-32b-2026-09-23.csv)

Reference source: [ring3_fixed_slab_latest.c](../benchmarks/native-gen2/ring3_fixed_slab_latest.c)

| Run | Frames/s | Payload GB/s | Receiver CPU ns/frame | Obsolete skipped/overwritten |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 109.09 M | 3.49 | 3.59 | 99.2015% |
| 2 | 86.18 M | 2.76 | 3.68 | 99.1973% |
| 3 | 90.70 M | 2.90 | 3.71 | 99.2025% |
| 4 | 78.01 M | 2.50 | 4.21 | 99.1934% |
| 5 | 63.02 M | 2.02 | 4.82 | 99.1840% |
| **Median** | **86.18 M** | **2.76** | **3.71** | **99.1973%** |

The run-to-run range is intentionally shown. This benchmark was executed in a shared/containerized environment where scheduling and loopback behavior varied materially between passes. The result is therefore useful as a reproducible architecture reference, not as a universal throughput ceiling.

The important implementation result is that forcing TCP to receive directly into only three tiny state slots is not necessarily optimal. A small fixed contiguous I/O workspace allows the kernel to amortize socket work efficiently, while DHMP still retains only three logical state frames. In other words, **bounded semantic state and efficient I/O batch size do not have to be the same thing**.

An earlier tuned native exploration of this slab-to-Ring-3 shape reached about 160 M 32-byte logical frames/s (~5.12 GB/s) in a short local run. Because that figure was more environment-sensitive and was not reproduced by the standalone reference harness above, it is retained only as an architecture-exploration observation rather than the published sustained reference value.



## Native showcase v2 — Ring-3 integrated — 2026-09-23

Ring-3 Fixed-Slab Latest has now been moved from a standalone architecture test into the same native comparison harness as the existing DHMP and baseline paths.

Configuration:

- native Linux/C localhost;
- 32-byte logical payloads;
- fixed 12 KiB receive workspace;
- 500 ms warmup;
- 1.5 second measured interval;
- three runs per path;
- 10 µs simulated consumer work;
- sender/receiver/consumer CPU pinning when available;
- TLS 1.3 for DHMPS;
- zero validation errors in all retained runs.

Raw data: [showcase-v2-32b-raw-3run-2026-09-23.csv](../benchmarks/results/showcase-v2-32b-raw-3run-2026-09-23.csv)

Summary: [showcase-v2-32b-summary-3run-2026-09-23.csv](../benchmarks/results/showcase-v2-32b-summary-3run-2026-09-23.csv)

Charts:

- [logical input rate](../benchmarks/results/charts/showcase-v2-32b-input-2026-09-23.svg)
- [useful publication rate](../benchmarks/results/charts/showcase-v2-32b-published-2026-09-23.svg)

Three-run medians:

| Path | Logical input frames/s | Useful publications/s | Payload GB/s | Retained state |
| --- | ---: | ---: | ---: | ---: |
| DHMP Slab6 | **179.2 M** | **78.2k** | **5.73** | 192 B |
| **DHMP Ring-3 Fixed-Slab** | **174.2 M** | **74.1k** | **5.58** | **96 B** |
| DHMP Ring8 | 150.0 M | 72.8k | 4.80 | 256 B |
| HTTP/1.1 / chunk framing | 123.7 M | 75.0k | 3.96 | harness state |
| TCP / 4-byte length | 119.5 M | 72.0k | 3.82 | harness state |
| WebSocket / binary framing | 104.7 M | 60.8k | 3.35 | harness state |
| Raw TCP / fixed frame | 73.0 M | 52.6k | 2.33 | harness state |
| DHMPS / TLS / Ring-3 | 65.1 M | 71.6k | 2.08 | 96 B |
| UDP / datagram | 0.37 M | 86.1k | 0.012 | harness state |

The important comparison is Ring-3 versus the other DHMP `Latest` paths under the same harness. Ring-3 is within about 3% of Slab6's median input rate and about 5% of its useful publication rate in this run, while using **96 B rather than 192 B** of retained 32-byte application-state payload. It also exceeds Ring8's median input rate while using substantially less retained state.

The v2 WebSocket and HTTP rows remain framing/parser microbenchmarks rather than complete framework stacks. UDP is an unbatched datagram path in v2 and should not be interpreted as a general UDP performance ceiling. Logical payload GB/s is a local hot-path/loopback measurement, not physical NIC throughput.

The source archive at [benchmarks/native-showcase/DHMP-native-showcase-source.zip](../benchmarks/native-showcase/DHMP-native-showcase-source.zip) has been replaced with the integrated v2 harness.


## Ring-3 cache-line layout A/B — 2026-09-23

A follow-up test checked whether padding each 32-byte Ring-3 slot to a dedicated 64-byte cache line would reduce producer/consumer cache-line contention.

Two layouts were compared:

- **packed:** three 32-byte slots in one 96-byte aligned block;
- **isolated:** three 32-byte slots each beginning on a separate 64-byte cache line (192 bytes physical slot area).

The test reproduces the actual full-tail update pattern used by Ring-3: the producer writes all three retained states, publishes once, and the consumer samples the newest state. Producer and consumer are pinned to separate CPUs. Each pass executes 50 million three-state producer updates and reports thread CPU time, avoiding wall-clock scheduling noise as much as possible.

Source: [ring3_cacheline_ab.c](../benchmarks/native-gen2/ring3_cacheline_ab.c)

Raw data: [ring3-cacheline-ab-2026-09-23.csv](../benchmarks/results/ring3-cacheline-ab-2026-09-23.csv)

| Layout | Physical slot area | Median ns / 3-state update | Equivalent ns / retained frame |
| --- | ---: | ---: | ---: |
| **Packed 96 B** | **96 B** | **8.215 ns** | **2.738 ns** |
| 64 B isolated slots | 192 B | 14.662 ns | 4.887 ns |

The fully isolated layout was therefore a regression in this access pattern: the median producer CPU cost per three-state update was about **78% higher**.

The reason is consistent with the memory layout. With a 64-byte-aligned packed Ring-3, slot 0 and slot 1 share the first cache line while slot 2 begins on the second cache line. A full three-state tail update therefore touches only two cache lines. Padding every slot to 64 bytes forces the producer to dirty three cache lines for the same 96 bytes of logical state.

This also explains why the earlier synthetic false-sharing experiment looked more favorable to padding: that microtest deliberately had one core repeatedly write one half of a cache line while another core repeatedly read the other half. The actual Ring-3 batch pattern is different. In the real full-tail path, compact packing reduces cache-line traffic and the newest slot is already naturally separated when the ring begins at a 64-byte boundary.

**Decision:** retain the current compact 96-byte Ring-3 slot layout. Full 64-byte slot padding is not adopted.


## Ring-2 vs Ring-3 with 10 µs zero-copy consumer hold — 2026-09-23

This test addresses the main semantic reason for keeping three slots: whether a two-slot design remains faster after adding the synchronization required to let the consumer hold a zero-copy state safely while the producer continues publishing.

Configuration:

- native Linux/C;
- 32-byte state;
- producer and consumer pinned to separate CPUs;
- 10 µs consumer hold per acquired state;
- 2-second measured interval;
- 7 retained runs per variant;
- same atomic slot-state protocol for correctness: `FREE / WRITING / PUBLISHED / READING`;
- payload integrity checked on every acquired state;
- zero validation errors in all retained runs.

Source: [ring2_vs_ring3_hold10us.c](../benchmarks/native-gen2/ring2_vs_ring3_hold10us.c)

Raw data: [ring2-vs-ring3-hold10us-2026-09-23.csv](../benchmarks/results/ring2-vs-ring3-hold10us-2026-09-23.csv)

Median results:

| Variant | Producer publications/s | Producer CPU ns/publication | Consumer useful publications/s | Validation errors |
| --- | ---: | ---: | ---: | ---: |
| Ring-2 safe swap | 30.640 M | 32.636 ns | 88.861k | 0 |
| **Ring-3 triple buffer** | **30.649 M** | **32.624 ns** | **90.498k** | **0** |

The producer rates are effectively identical (about 0.03% apart), while Ring-3 delivered about **1.8% more useful consumer publications** in the median retained run.

The important result is that Ring-2's smaller 64-byte payload footprint did **not** translate into a measurable producer-throughput advantage once a safe zero-copy ownership protocol and a 10 µs consumer hold were included. The synchronization needed to keep the two-slot design correct consumes the raw-copy advantage seen in isolated memory tests.

This makes Ring-3 the stronger default for `Latest` zero-copy semantics: it preserves a separate reader-held state while the producer continues publishing, without showing a throughput penalty in this test.

An earlier aggressively minimized lock-free prototype produced occasional torn-state validation failures and is not retained as a valid result. Only zero-error runs are published.


## Ring-3 single-atomic triple exchange — 2026-09-23

The safe Ring-3 ownership path was simplified from a per-slot atomic state machine to a classic SPSC triple-buffer exchange.

The baseline uses:

- three per-slot ownership states (`FREE / WRITING / PUBLISHED / READING`);
- slot-claim CAS loops;
- a shared published index;
- release/free transitions after publication and consumption.

The new design keeps ownership roles local where possible:

- `FRONT` is consumer-owned;
- `BACK` is producer-owned;
- one shared atomic `MIDDLE` token stores the middle-slot index plus a dirty bit.

Producer publication is one atomic exchange:

```text
write BACK
atomic_exchange(MIDDLE, BACK | DIRTY)
returned MIDDLE index becomes new BACK
```

Consumer acquisition is one load plus a CAS only when a newer state exists:

```text
load MIDDLE
if DIRTY:
    CAS(MIDDLE, FRONT)
    old MIDDLE becomes new FRONT
```

Configuration:

- native Linux/C;
- 32-byte state;
- 10 µs zero-copy consumer hold;
- producer and consumer pinned to separate CPUs;
- 2-second measured interval;
- seven alternating runs per variant;
- payload integrity validation on every consumed state;
- zero validation errors in all retained runs.

Source: [ring3_triple_exchange_ab.c](../benchmarks/native-gen2/ring3_triple_exchange_ab.c)

Raw data: [ring3-triple-exchange-ab-2026-09-23.csv](../benchmarks/results/ring3-triple-exchange-ab-2026-09-23.csv)

Median results:

| Ring-3 ownership engine | Producer publications/s | Producer CPU ns/publication | Producer claim retries | Consumer useful publications/s | Consumer CAS retries | Validation errors |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Per-slot state machine | 30.281 M | 33.023 ns | 30.11 M | 89.745k | 2.61 M | 0 |
| **Single-atomic triple exchange** | **32.359 M** | **30.900 ns** | **0** | 89.308k | **12** | 0 |

The new ownership engine improved median producer publication throughput by about **6.9%** and reduced producer CPU cost per publication by about **6.4%**.

Its larger architectural benefit is removing contention machinery from the producer hot path. The producer no longer scans/claims slots or retries CAS operations; it owns `BACK` outright and performs one atomic ownership transfer per publication. Consumer useful-publication rate stayed essentially unchanged in this 10 µs-hold workload, while consumer CAS retries dropped by more than five orders of magnitude.

**Decision:** retain three slots, but prefer the single-atomic `FRONT / MIDDLE / BACK` exchange as the current Ring-3 ownership model for the next integrated benchmark.
