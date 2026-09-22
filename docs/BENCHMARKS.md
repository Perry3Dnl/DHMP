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

