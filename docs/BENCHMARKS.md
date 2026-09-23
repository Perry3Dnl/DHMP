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


## Ring-3 CPU micro-optimization sweep — 2026-09-23

After the single-atomic `FRONT / MIDDLE / BACK` exchange was selected, several smaller CPU-side costs were isolated.

### Atomic memory order

The producer exchange was reduced from `acq_rel` to `release`, and the consumer's preliminary load / failed CAS were tested with relaxed ordering while keeping an `acq_rel` successful ownership transfer.

On the retained x86-64 machine this produced no meaningful speedup. Inspection of generated code explains why: the exchange remains an `xchg` read-modify-write, acquire versus relaxed loads are ordinary loads, and the successful CAS remains a locked compare/exchange. The weaker but correct ordering is useful for expressing intent and portability, but is not counted as an x86 performance win.

### Atomic token width

The middle token needs only a slot index plus one dirty bit, so 8-, 16-, 32-, and 64-bit atomic tokens were compared with 50 million producer publications and a 10 µs consumer hold.

Source: [ring3_token_width_ab.c](../benchmarks/native-gen2/ring3_token_width_ab.c)

Raw data: [ring3-token-width-ab-2026-09-23.csv](../benchmarks/results/ring3-token-width-ab-2026-09-23.csv)

| Token width | Median producer CPU ns/publication |
| ---: | ---: |
| 8 bit | 3.941 ns |
| 16 bit | 4.030 ns |
| **32 bit** | **2.286 ns** |
| 64 bit | 2.250 ns |

A longer 32-vs-64 follow-up was effectively a tie, so 32 bits remains the preferred token width. Sub-word atomic RMW instructions were materially slower on this machine.

A tagged-pointer token was also screened; it regressed median producer cost by roughly 2.5% versus the compact index token, so direct pointer tagging was rejected.

### Contract-specialized 32-byte processor

Because frame size is negotiated once, a 32-byte contract can use a dedicated processor path rather than runtime division and runtime-size copy operations.

Source: [ring3_contract_specialize_ab.c](../benchmarks/native-gen2/ring3_contract_specialize_ab.c)

Raw data: [ring3-contract-specialize-ab-2026-09-23.csv](../benchmarks/results/ring3-contract-specialize-ab-2026-09-23.csv)

| Processor | Median ns / batch |
| --- | ---: |
| Generic runtime-size path | 4.809 ns |
| **Specialized 32-byte path** | **4.738 ns** |

The specialized path improved this isolated processor operation by about **1.5%**. Generated assembly removes the runtime integer divide and runtime-size `memcpy`, replacing them with shift/mask arithmetic plus a fixed 256-bit load/store.

### Fixed-width carry copy

For a 32-byte contract, trailing carry is at most 31 valid bytes. With a workspace that includes at least one frame of guard capacity, the carry can be moved using one fixed 32-byte vector transfer while the separate carry count records how many bytes are valid.

Source: [ring3_carry32_ab.c](../benchmarks/native-gen2/ring3_carry32_ab.c)

Raw data: [ring3-carry32-ab-2026-09-23.csv](../benchmarks/results/ring3-carry32-ab-2026-09-23.csv)

| Carry path | Median ns / batch |
| --- | ---: |
| Generic variable-size `memmove` | 4.794 ns |
| **Conditional fixed 32-byte vector copy** | **1.739 ns** |
| Unconditional fixed 32-byte copy | 1.785 ns |

The conditional fixed-width path reduced the isolated carry housekeeping cost by about **63.7%**, saving roughly **3.05 ns per receive batch**. The conditional form is retained because it keeps the guard-memory assumption narrower while matching or beating the unconditional variant in the retained median.

These are CPU microbenchmarks, not end-to-end network throughput results. Their purpose is to decide what should be folded into the next integrated Ring-3 showcase implementation.


## Native showcase v3 — latest Ring-3 engine integrated — 2026-09-23

Showcase v3 moves the retained CPU optimizations into a complete native localhost transport/framing comparison.

Integrated DHMP/DHMPS fast path:

- 32-byte fixed negotiated contract;
- 12 KiB receive workspace;
- 256 KiB reusable sender batch;
- Ring-3 `FRONT / MIDDLE / BACK` ownership;
- one shared 32-bit atomic middle token;
- one atomic ownership exchange per publication;
- fixed 32-byte AVX publication copy;
- shift/mask fixed-frame arithmetic;
- fixed-width 32-byte carry handling;
- 10 µs zero-copy consumer hold.

All comparison paths use the same Latest publication/consumer engine. The framed stream paths still perform their framing checks. UDP uses Linux `sendmmsg/recvmmsg` batching.

Source: [showcase_v3.c](../benchmarks/native-showcase/showcase_v3.c)

Runner: [run_showcase_v3.py](../benchmarks/native-showcase/run_showcase_v3.py)

Raw data: [showcase-v3-32b-raw-3run-2026-09-23.csv](../benchmarks/results/showcase-v3-32b-raw-3run-2026-09-23.csv)

Summary: [showcase-v3-32b-summary-3run-2026-09-23.csv](../benchmarks/results/showcase-v3-32b-summary-3run-2026-09-23.csv)

Charts:

- [logical input rate](../benchmarks/results/charts/showcase-v3-32b-input-2026-09-23.svg)
- [useful publication rate](../benchmarks/results/charts/showcase-v3-32b-published-2026-09-23.svg)

Three-run medians:

| Path | Logical input | Useful publications | Payload GB/s | Receiver CPU / logical frame | Input range |
| --- | ---: | ---: | ---: | ---: | ---: |
| **DHMP Latest / Ring-3 v3** | **118.45 M/s** | **73.67k/s** | **3.79** | **2.736 ns** | 97.93–127.90 M/s |
| DHMPS / TLS 1.3 / Ring-3 | 59.66 M/s | 82.78k/s | 1.91 | 10.861 ns | 36.15–62.81 M/s |
| Raw TCP / fixed frame | 145.88 M/s | 80.54k/s | 4.67 | 2.489 ns | 109.14–147.72 M/s |
| TCP / 4-byte length | 71.22 M/s | 59.06k/s | 2.28 | 4.399 ns | 68.11–124.07 M/s |
| WebSocket / binary framing | 104.36 M/s | 69.51k/s | 3.34 | 3.925 ns | 83.36–125.89 M/s |
| HTTP/1.1 / chunk framing | 120.66 M/s | 83.42k/s | 3.86 | 4.202 ns | 43.54–122.05 M/s |
| UDP / batched datagrams | 0.574 M/s | 81.56k/s | 0.018 | 690.13 ns | 0.456–0.673 M/s |

All retained v3 runs had zero payload-validation and framing-validation errors.

The wide ranges show that this shared/containerized localhost environment still has material scheduling and loopback variability. The v3 medians should be treated as current integrated architecture measurements, not universal ceilings.

The raw fixed-TCP baseline is intentionally minimal. It is expected to remain extremely competitive because DHMP is built on top of the same transport. DHMP's differentiator is the fixed-contract model and explicit Latest semantics while keeping processor overhead near the lean stream floor.

The HTTP and WebSocket cases are framing/parser microbenchmarks, not complete ASP.NET Core or browser stacks. Logical payload GB/s is not physical NIC throughput.

Showcase v3 also changes the harness relative to v2 (including the sender batch and publication engine), so v2-to-v3 absolute numbers are not a controlled optimization A/B. The separate Ring-3 CPU experiments are the controlled evidence for the single-atomic, specialization, and carry-path gains.


## Processor output-slab batching — 2026-09-23

A CPU-side experiment tested the idea of writing processed results into a fixed contiguous output array and publishing the array once, rather than performing a shared ownership transfer for every processed result.

Configuration:

- 32-byte input and 32-byte output result;
- 384 results per output slab (12 KiB);
- three permanent output slabs using the same `FRONT / MIDDLE / BACK` ownership exchange;
- 10 µs zero-copy consumer hold;
- producer and consumer pinned to separate CPUs;
- deterministic per-frame transform;
- consumer validates every frame in every acquired output slab for coherent batch identity;
- seven alternating runs;
- zero validation errors in all retained runs.

Source: [processor_output_slab_ab.c](../benchmarks/native-gen2/processor_output_slab_ab.c)

Raw data: [processor-output-slab-ab-2026-09-23.csv](../benchmarks/results/processor-output-slab-ab-2026-09-23.csv)

| Processor handoff | Median producer CPU / result | Median CPU result rate |
| --- | ---: | ---: |
| Per-result triple exchange | 2.251 ns | 444.3 M results/s |
| **384-result fixed output slab + one exchange** | **0.887 ns** | **1.127 B results/s** |

The output-slab path reduced producer/handoff CPU cost by about **60.6%** and increased the measured CPU-side result rate by about **2.54×** for this transform.

This does not mean every DHMP mode should retain every processed result. For `Latest`, processing or storing obsolete results can be wasted work; the strongest path remains to conflate before expensive processing whenever semantics allow. The fixed output-slab model is most useful when a processing stage genuinely needs to emit many results, or when a downstream stage can consume results efficiently in contiguous batches.

For strict `Every` semantics, an overwriteable three-slab exchange is not sufficient by itself because an overloaded consumer could miss whole output slabs. `Every` requires bounded backpressure or a non-overwriting fixed queue of output slabs. The benchmark here measures the batching/handoff cost rather than defining the final `Every` queue policy.


## Native showcase v4 — zero-hold maximum-speed suite — 2026-09-23

The v4 headline suite removes the previously simulated 10 µs application hold entirely. The consumer validates the acquired Latest state and releases it immediately; no clock-based delay exists in the hot consumer loop.

Profile: 32-byte logical payload, 12 KiB receive workspace, 256 KiB sender batch, Ring-3 FRONT/MIDDLE/BACK publication, 500 ms warmup, 1.2 s measured interval, five rotated runs per path, CPU pinning when available, and full retained payload/framing validation.

DHMP is compared with ten established transport/framing baselines: raw fixed TCP, one-byte-varint TCP framing, 4-byte-length TCP framing, WebSocket binary framing, HTTP/1.1 chunk framing, HTTP/2 DATA framing, gRPC/HTTP2 framing, MQTT QoS 0 PUBLISH framing, NATS PUB framing, and batched UDP datagrams.

| Path | Median logical input | Median zero-hold publications | Median receiver CPU / logical frame |
| --- | ---: | ---: | ---: |
| DHMP Latest / Ring-3 v4 | 130.62 M/s | 359.36k/s | 2.704 ns |
| Raw TCP / fixed frame | 111.73 M/s | 297.88k/s | 3.120 ns |
| TCP / varint length | 132.30 M/s | 364.60k/s | 3.676 ns |
| TCP / 4-byte length | 108.08 M/s | 327.63k/s | 3.969 ns |
| WebSocket / binary framing | 119.32 M/s | 341.47k/s | 3.474 ns |
| HTTP/1.1 / chunk framing | 108.03 M/s | 356.19k/s | 4.527 ns |
| HTTP/2 / DATA framing | 105.57 M/s | 356.20k/s | 5.608 ns |
| gRPC / HTTP/2 framing | 98.92 M/s | 382.60k/s | 6.498 ns |
| MQTT QoS 0 / PUBLISH | 91.79 M/s | 298.73k/s | 4.542 ns |
| NATS / PUB framing | 96.38 M/s | 341.67k/s | 5.153 ns |
| UDP / batched datagrams | 0.466 M/s | 107.64k/s | 910.50 ns |

All retained v4 runs reported zero payload-validation and framing-validation errors.

The localhost environment remains noisy. DHMP itself ranged from 51.48 M/s to 154.97 M/s across the five rotated passes, so the medians should not be treated as precise universal rankings. Raw fixed TCP and DHMP have essentially the same fixed-record wire work in this harness; small differences are measurement variance. The framing rows are hot-path framing/parser comparisons rather than complete production application stacks.

Removing the artificial 10 µs hold raised DHMP's visible useful-publication median from the old ~74k/s stress-test range to 359k/s. The old hold suite remains useful only as a slow-consumer / conflation stress test.

Methodology: [V4_ZERO_HOLD.md](../benchmarks/native-showcase/V4_ZERO_HOLD.md)

Raw data: [showcase-v4-zero-hold-32b-raw-5run-2026-09-23.csv](../benchmarks/results/showcase-v4-zero-hold-32b-raw-5run-2026-09-23.csv)

Summary: [showcase-v4-zero-hold-32b-summary-5run-2026-09-23.csv](../benchmarks/results/showcase-v4-zero-hold-32b-summary-5run-2026-09-23.csv)


## Ring-3 consumer CAS vs atomic exchange — 2026-09-23

A proposed simplification replaced the consumer's dirty-token compare-and-swap with a single atomic exchange after the consumer observes `DIRTY`.

The ownership argument is valid for this SPSC triple-buffer state machine: once the single consumer has observed a dirty `MIDDLE`, producer publications can only replace dirty-with-dirty until that consumer returns its old `FRONT` as a clean middle token. Therefore an atomic exchange can safely acquire whichever dirty slot is newest at the RMW linearization point.

Source: [ring3_consumer_cas_vs_exchange.c](../benchmarks/native-gen2/ring3_consumer_cas_vs_exchange.c)

Raw data: [ring3-consumer-cas-vs-exchange-2026-09-23.csv](../benchmarks/results/ring3-consumer-cas-vs-exchange-2026-09-23.csv)

A fixed-count high-contention test used 10 million producer publications per run and 12 alternating runs.

| Consumer acquisition | Median producer CPU / pub | Median consumer acquisitions | Median consumer CPU / acquisition | Median CAS retries | Validation errors |
| --- | ---: | ---: | ---: | ---: | ---: |
| **CAS** | **11.790 ns** | 1.465 M | **74.482 ns** | 120 | 0 |
| Atomic exchange | 12.412 ns | 1.347 M | 83.661 ns | **0** | 0 |

The exchange variant completely removes consumer retries, but it did **not** produce a reliable CPU win. Separate medians show about **+5.3% producer CPU cost** and **+12.3% consumer CPU per acquisition** for exchange. The shared-host runs remain noisy, so these percentages are directional rather than universal.

The integrated zero-hold v4 network A/B also showed zero validation errors and zero exchange retries, but paired throughput differences were small and inconsistent. The current CAS path already retries extremely rarely under the real receive-batch publication rate, so removing those retries has little opportunity to help.

Architecturally, the likely trade-off is cache-line ownership: an exchange always completes the ownership transfer immediately, while a failed CAS can defer the consumer transfer when the producer wins the race. Under heavy producer/consumer overlap, the unconditional exchange can create more cache-line bouncing even though it removes the retry branch.

**Decision:** retain the consumer CAS for the current default. The exchange form is correct and remains a useful alternative, but it is not promoted as a performance optimization on the retained x86-64 results.


## Ring-3 CPU topology / cache placement — 2026-09-23

Thread placement was tested because the Ring-3 producer and consumer repeatedly transfer ownership through the shared `MIDDLE` token.

The available host reported five logical CPUs. All were on NUMA node 0 and all shared one L3/LLC. CPU 0-1 and CPU 2-3 were additionally reported as shared lower-cache groups; CPU 4 had its own lower-cache group. All five CPUs reported distinct core IDs and no SMT siblings. Because this is a virtualized/shared benchmark environment, the topology should be treated as host-reported topology rather than proof of physical silicon layout.

The existing showcase placement `sender=0 / receiver=1 / consumer=2` already satisfies the broad target of distinct reported cores in one NUMA node and one shared LLC.

### Isolated Ring-3 handoff

A fixed 20-million-publication test compared the current LLC-only producer/consumer relationship with the two reported shared lower-cache groups. Fifteen rotated runs were retained.

| Producer / consumer placement | Median producer CPU / publication | Median consumer CPU / acquisition | Median useful acquisitions | Validation errors |
| --- | ---: | ---: | ---: | ---: |
| CPU 1 → 2, shared LLC only | 12.081 ns | 77.331 ns | 2.970 M | 0 |
| CPU 0 → 1, reported shared lower caches | 12.512 ns | 74.726 ns | 3.229 M | 0 |
| **CPU 2 → 3, reported shared lower caches** | **11.770 ns** | **71.443 ns** | **3.439 M** | **0** |

Relative to the LLC-only median, the CPU 2→3 placement reduced producer cost by about **2.6%**, reduced consumer CPU per acquisition by about **7.6%**, and increased useful acquisitions by about **15.8%**. The CPU 0→1 pair was mixed: consumer acquisition cost improved, but producer cost did not.

### End-to-end loopback placement A/B

A separate seven-run TCP loopback test used the same 32-byte Ring-3 Latest receive path with zero artificial consumer hold.

| Placement | Median logical input |
| --- | ---: |
| **Current: sender 0 / receiver 1 / consumer 2** | **130.77 M/s** |
| Sender 4 / receiver 0 / consumer 1 | 126.68 M/s |
| Sender 4 / receiver 1 / consumer 2 | 116.49 M/s |
| Sender 4 / receiver 2 / consumer 3 | 109.64 M/s |

The network runs were highly variable and did **not** confirm an end-to-end speedup from placing receiver and consumer in the same reported lower-cache group. The current placement retained the highest median on this host.

**Decision:** do not hard-code a new placement based on this machine. Keep topology-aware placement as a runtime/AutoTune optimization candidate and retest on physical multi-core/NUMA hardware. The isolated handoff result says cache proximity can matter; the end-to-end result says the best placement depends on the whole sender/receiver/consumer topology and host scheduling.

Source: [ring3_cpu_topology_ab.c](../benchmarks/native-gen2/ring3_cpu_topology_ab.c)

Isolated results: [ring3-cpu-topology-ab-2026-09-23.csv](../benchmarks/results/ring3-cpu-topology-ab-2026-09-23.csv)

Network placement results: [ring3-cpu-topology-network-ab-2026-09-23.csv](../benchmarks/results/ring3-cpu-topology-network-ab-2026-09-23.csv)


## Every output-slab integration — 2026-09-23

The earlier isolated output-slab experiment was integrated into a real TCP loopback `Every` processing pipeline.

The test preserves every processed result. It does not use Latest-style overwriting.

Configuration:

- fixed 32-byte input and 32-byte transformed output;
- 12 KiB receive workspace, up to 384 input frames per receive batch;
- 30 million logical frames per run;
- sender / receiver-processor / consumer pinned to CPUs 0 / 1 / 2;
- zero artificial consumer hold;
- fixed-width 32-byte carry path;
- full ordered output validation;
- nine alternating runs;
- zero errors in all retained runs.

The two paths have equal bounded output payload capacity:

- per-result path: 3,072 × 32-byte output entries = **96 KiB**;
- batched path: 8 × 12 KiB output slabs = **96 KiB**.

The batched processor claims the next free output slab, writes transformed results directly into it, stores the actual populated result count, and publishes it immediately after that receive batch. It does **not** wait to fill the slab. If all output slabs are still unread, the producer waits; unread `Every` results are never overwritten.

| Every output handoff | Median end-to-end result rate | Receiver/processor CPU / result | Consumer CPU / result | Median output publications | Results / publication |
| --- | ---: | ---: | ---: | ---: | ---: |
| Per-result bounded ring | 33.35 M/s | 29.933 ns | 29.978 ns | 30.0 M | 1.0 |
| **Bounded output slabs** | **129.80 M/s** | **6.988 ns** | **7.699 ns** | **80.6k** | **372.3** |

The output-slab path measured about **3.89× higher median end-to-end result throughput**, about **76.7% lower receiver/processor CPU per result**, and about **74.3% lower consumer CPU per result**. Atomic/publication frequency fell by roughly **372×** at the retained median.

The shared localhost host was noisy: per-result end-to-end runs ranged from 9.82–49.87 M/s and output-slab runs ranged from 47.15–147.71 M/s. The direction and size of the CPU-cost reduction are nevertheless much stronger than the earlier isolated handoff-only result, but this remains a native architecture benchmark rather than a production-stack claim.

This optimization is specific to `Every`-style processing where all results matter. `Latest` should continue conflating obsolete states and publishing only the newest useful state instead of materializing every output.

Source: [every_output_slab_v1.c](../benchmarks/native-showcase/every_output_slab_v1.c)

Raw results: [every-output-slab-e2e-32b-raw-9run-2026-09-23.csv](../benchmarks/results/every-output-slab-e2e-32b-raw-9run-2026-09-23.csv)

Summary: [every-output-slab-e2e-32b-summary-9run-2026-09-23.csv](../benchmarks/results/every-output-slab-e2e-32b-summary-9run-2026-09-23.csv)


## Direct output-slab forwarding — 2026-09-23

A forwarding-path experiment extends the bounded `Every` output-slab model across the next service boundary.

Pipeline:

```text
ingress TCP
    ↓
processor writes negotiated wire-ready results
    ↓
bounded 8 × 12 KiB output slabs
    ↓
forward sender
    ↓
egress TCP
    ↓
validating sink
```

Two variants were compared:

- **copy:** processor publishes a populated output slab; the forward sender copies the populated bytes into a separate 12 KiB sender scratch buffer, then sends that buffer;
- **direct:** the published output slab itself becomes the sender buffer. The sender retains ownership until all bytes from that population have been accepted by the blocking socket, then returns the slab to the processor.

Both paths process, forward, receive, and validate 12 million 32-byte results per run. The processor writes the final fixed wire layout directly, so this A/B isolates the intermediate application-level copy. It does not include a second serialization pass in the copy baseline.

Twelve alternating runs were retained:

| Forwarding path | Median end-to-end results/s | Processor CPU/result | Forward-sender CPU/result | Sink CPU/result |
| --- | ---: | ---: | ---: | ---: |
| Copy slab → sender scratch | 41.93 M/s | 23.238 ns | 23.798 ns | 7.216 ns |
| **Send published slab directly** | **43.98 M/s** | **21.111 ns** | **22.689 ns** | **6.805 ns** |

The direct-slab path measured about **+4.9% median end-to-end throughput**, **-9.2% processor CPU/result**, and **-4.7% forward-sender CPU/result** on this shared localhost host. All retained runs completed with zero validation errors and exact processed/forwarded/received counts.

The run-to-run variance remained substantial: copy ranged from 22.35–61.47 M results/s and direct ranged from 39.49–63.51 M/s. Treat the gain as promising architectural evidence rather than a universal percentage.

The sender implementation maintains a byte offset and does not return slab ownership until the entire populated region has been sent. In the retained blocking-loopback runs, Linux accepted each 12 KiB slab send without a short successful send, so the partial-send branch was not exercised by the measured dataset. For asynchronous or kernel zero-copy APIs, ownership must instead remain with the sender until the API's completion event guarantees the transport no longer references the user buffer.

This benchmark does **not** claim elimination of kernel copies or TLS copies. It removes only the application-level output-slab → sender-buffer copy. If a future implementation already sends directly from the processor's output slab, this optimization has no additional benefit.

Source: [every_forward_direct_slab_ab.c](../benchmarks/native-showcase/every_forward_direct_slab_ab.c)

Raw results: [every-forward-direct-slab-ab-raw-12run-2026-09-23.csv](../benchmarks/results/every-forward-direct-slab-ab-raw-12run-2026-09-23.csv)

Summary: [every-forward-direct-slab-ab-summary-12run-2026-09-23.csv](../benchmarks/results/every-forward-direct-slab-ab-summary-12run-2026-09-23.csv)


## Dedicated batch delivery worker — 2026-09-23

A native `Every` pipeline experiment tested moving actual decode/conversion and batch-adapter work off the protocol receiver onto a dedicated delivery worker.

Both variants use the same bounded **8 × 12 KiB slab pool** and the same batch-oriented adapter. The protocol stage receives complete 32-byte records and places them into a reusable slab. The difference is where delivery preparation runs:

- **inline:** the protocol thread performs conversion and one adapter call for the whole slab before continuing to receive;
- **worker:** the protocol thread publishes the populated slab and immediately returns to receive work; a dedicated delivery worker acquires the slab, performs the identical conversion, calls the identical batch adapter once, then returns the slab to the pool.

The adapter therefore crosses the delivery boundary once per roughly **384 records**, not once per message.

A synthetic conversion-weight sweep used 12 million ordered records per run, seven alternating runs per weight, zero artificial consumer delay, sender CPU 4, protocol CPU 2, and delivery-worker CPU 3. CPUs 2/3 were the cache-close pair identified by the earlier topology experiment.

| Conversion work | Inline median | Worker median | Worker throughput change | Inline CPU/result | Worker protocol CPU/result | Worker delivery CPU/result |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 round | 89.60 M/s | **133.09 M/s** | **+48.5%** | 11.116 ns | 7.041 ns | 7.511 ns |
| 2 rounds | 83.71 M/s | **112.19 M/s** | **+34.0%** | 11.640 ns | 8.557 ns | 8.909 ns |
| 4 rounds | 55.36 M/s | **67.58 M/s** | **+22.1%** | 18.035 ns | 14.738 ns | 14.791 ns |
| 8 rounds | 35.56 M/s | **43.39 M/s** | **+22.0%** | 28.099 ns | 22.979 ns | 23.047 ns |
| 16 rounds | 21.53 M/s | **25.55 M/s** | **+18.7%** | 46.397 ns | 39.056 ns | 39.127 ns |

All retained runs completed with zero validation errors and exact delivered-frame counts.

The worker improves wall-clock throughput by overlapping protocol receive work with delivery preparation, but it does **not** reduce total CPU usage. For example, at one synthetic conversion round the inline path used about 11.116 ns of protocol-thread CPU per result, while the worker path used about 7.041 ns on the protocol thread plus 7.511 ns on the worker. The higher throughput therefore comes from parallel execution across cores, not from less aggregate work.

A preliminary run using the older sender/protocol/consumer CPU placement showed the worker can regress when cache placement is poor. The positive sweep above used the cache-close protocol/worker pair found in the topology experiment. This makes delivery-worker enablement a strong candidate for runtime/AutoTune selection rather than a universal always-on rule.

The conversion loop is synthetic and is intended to explore the break-even behavior of the architecture. It is not a claim about the cost of a specific .NET, Unity, Python, or other language adapter.

**Implementation direction:** keep the high-performance public surface batch-oriented. Per-message convenience APIs can be layered above it, but should not force the protocol core to perform one cross-thread handoff or callback per logical message.

Source: [every_delivery_worker_ab.c](../benchmarks/native-showcase/every_delivery_worker_ab.c)

Raw results: [every-delivery-worker-ab-raw-7run-2026-09-23.csv](../benchmarks/results/every-delivery-worker-ab-raw-7run-2026-09-23.csv)

Summary: [every-delivery-worker-ab-summary-7run-2026-09-23.csv](../benchmarks/results/every-delivery-worker-ab-summary-7run-2026-09-23.csv)
