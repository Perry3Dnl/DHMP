# DHMP native showcase benchmark

This directory contains the native Linux/C comparison harness used to put DHMP receive strategies and familiar framing/transport baselines under one local workload.

## Current showcase v3

Showcase v3 integrates the latest retained Ring-3 CPU architecture directly into the transport benchmark:

- fixed 32-byte negotiated contract;
- 12 KiB reusable receive workspace;
- 256 KiB reusable sender batch;
- three permanent 32-byte payload buffers;
- SPSC `FRONT / MIDDLE / BACK` ownership;
- one shared 32-bit atomic `MIDDLE` token;
- fixed 32-byte vector publication copy;
- fixed-width carry handling for partial frames;
- 10 µs zero-copy consumer hold;
- TLS 1.3 for DHMPS;
- Linux `sendmmsg/recvmmsg` batching for UDP.

Compared paths:

- **DHMP Latest / Ring-3 v3**
- **DHMPS / TLS 1.3 / Ring-3**
- raw TCP / fixed frame
- TCP / 4-byte length prefix
- WebSocket binary framing
- HTTP/1.1 chunk framing
- UDP / batched datagrams

The framing baselines use the same Latest publication/consumer model. WebSocket and HTTP remain framing/parser microbenchmarks rather than complete server-stack tests.

### Build

```bash
gcc -O3 -march=native -pthread -Wall -Wextra -Wpedantic \
    showcase_v3.c -o showcase_v3 -lssl -lcrypto
```

Generate a local test certificate for the DHMPS row:

```bash
openssl req -x509 -newkey rsa:2048 \
    -keyout key.pem -out cert.pem -sha256 -days 1 -nodes \
    -subj '/CN=localhost'
```

Then run the retained three-pass suite:

```bash
python run_showcase_v3.py
```

### v3 retained medians

| Path | Logical input | Useful publications | Receiver CPU / logical frame |
| --- | ---: | ---: | ---: |
| **DHMP Latest / Ring-3 v3** | **118.45 M/s** | **73.67k/s** | **2.736 ns** |
| DHMPS / TLS 1.3 / Ring-3 | 59.66 M/s | 82.78k/s | 10.861 ns |
| Raw TCP / fixed frame | 145.88 M/s | 80.54k/s | 2.489 ns |
| TCP / 4-byte length | 71.22 M/s | 59.06k/s | 4.399 ns |
| WebSocket / binary framing | 104.36 M/s | 69.51k/s | 3.925 ns |
| HTTP/1.1 / chunk framing | 120.66 M/s | 83.42k/s | 4.202 ns |
| UDP / batched datagrams | 0.574 M/s | 81.56k/s | 690.13 ns |

All retained runs reported zero payload-validation and framing-validation errors.

Results:

- [raw three-run CSV](../results/showcase-v3-32b-raw-3run-2026-09-23.csv)
- [three-run median summary](../results/showcase-v3-32b-summary-3run-2026-09-23.csv)
- [logical input-rate chart](../results/charts/showcase-v3-32b-input-2026-09-23.svg)
- [useful-publication chart](../results/charts/showcase-v3-32b-published-2026-09-23.svg)

The localhost run-to-run ranges are intentionally retained in the summary CSV. These rates are architecture-lab measurements, not physical-network throughput claims.

## Historical showcase v2

The older v2 source archive remains at [DHMP-native-showcase-source.zip](DHMP-native-showcase-source.zip). v2 predates the single-atomic triple exchange, the current fixed-width carry path, and the current v3 sender/publication model. Its absolute rates should not be treated as directly comparable to v3 because the harness changed.


## Every output-slab pipeline

`every_output_slab_v1.c` integrates batched result publication into a bounded `Every` TCP processing path.

Both A/B variants retain 96 KiB of bounded output payload capacity. The per-result variant publishes every transformed 32-byte result individually. The output-slab variant owns eight reusable 12 KiB result slabs, writes transformed results directly into the next free slab, publishes the actual populated count immediately after each receive batch, and applies backpressure rather than overwriting unread results.

Nine-run medians:

| Path | End-to-end results/s | Receiver/processor CPU/result | Consumer CPU/result | Results/publication |
| --- | ---: | ---: | ---: | ---: |
| Per-result | 33.35 M/s | 29.933 ns | 29.978 ns | 1.0 |
| **Output slab** | **129.80 M/s** | **6.988 ns** | **7.699 ns** | **372.3** |

All retained runs processed and consumed all 30 million results with zero validation errors.

Results:

- [raw nine-run CSV](../results/every-output-slab-e2e-32b-raw-9run-2026-09-23.csv)
- [nine-run median summary](../results/every-output-slab-e2e-32b-summary-9run-2026-09-23.csv)


## Direct output-slab forwarding

`every_forward_direct_slab_ab.c` extends the bounded `Every` output-slab pipeline into an egress TCP sender.

The processor emits fixed wire-ready 32-byte results into one of eight reusable 12 KiB slabs. The A/B compares copying each populated slab into a separate sender scratch buffer against sending directly from the owned slab and releasing that slab only after the entire populated byte range has been accepted.

Twelve-run medians:

| Path | End-to-end results/s | Processor CPU/result | Forward-sender CPU/result |
| --- | ---: | ---: | ---: |
| Slab → copy → send | 41.93 M/s | 23.238 ns | 23.798 ns |
| **Slab → send directly** | **43.98 M/s** | **21.111 ns** | **22.689 ns** |

The direct path was about **4.9% faster** at the median on this noisy localhost host. All retained runs processed, forwarded, received, and validated all 12 million results with zero errors.

Results:

- [raw twelve-run CSV](../results/every-forward-direct-slab-ab-raw-12run-2026-09-23.csv)
- [twelve-run median summary](../results/every-forward-direct-slab-ab-summary-12run-2026-09-23.csv)


## Dedicated batch delivery worker

`every_delivery_worker_ab.c` compares inline conversion/delivery with a dedicated worker that receives ownership of the same bounded output slabs.

The protocol thread publishes one populated slab and immediately returns to receive processing. The delivery worker performs the actual conversion in-place, invokes a batch adapter once per slab, and returns ownership after the batch has been consumed.

With sender/protocol/worker pinned to CPUs 4/2/3, seven alternating runs per synthetic conversion weight showed a median end-to-end throughput improvement ranging from **+18.7% to +48.5%** for the worker configuration. Adapter calls remained at roughly one per 384 logical records and all runs validated with zero errors.

The worker uses more aggregate CPU because it parallelizes work across cores; the improvement is wall-clock throughput. Placement matters, so this is a candidate for runtime selection/AutoTune rather than an unconditional default.

Results:

- [raw seven-run sweep](../results/every-delivery-worker-ab-raw-7run-2026-09-23.csv)
- [sweep summary](../results/every-delivery-worker-ab-summary-7run-2026-09-23.csv)


## Borrowed receive-slab Every delivery

`every_receive_slab_lease_ab.c` tests removing the full complete-frame copy between the receive workspace and the public `Every` output slab.

The direct path receives into one of eight reusable 12 KiB public slabs, publishes an `offset/count` view of the complete frames, and returns that slab to the pool only after the consumer releases it. TCP split-frame tails are repaired through a 32-byte carry area, so only boundary fragments move.

Twelve-run medians:

| Path | End-to-end frames/s | Logical payload GB/s | Receiver CPU/frame |
| --- | ---: | ---: | ---: |
| Receive → copy → public slab | 119.63 M/s | 3.83 | 6.305 ns |
| **Receive slab becomes public slab** | **126.51 M/s** | **4.05** | **6.184 ns** |

All retained runs delivered all 30 million frames with zero validation errors.

Results:

- [raw twelve-run CSV](../results/every-receive-slab-lease-ab-raw-12run-2026-09-23.csv)
- [twelve-run summary](../results/every-receive-slab-lease-ab-summary-12run-2026-09-23.csv)


## Fused batch processing / SIMD

`every_fused_vector_processing_ab.c` tests batching the actual developer-facing conversion/calculation work inside the existing bounded `Every` slab pipeline.

Twelve-run medians:

| Routine | End-to-end results/s | Processor CPU/result |
| --- | ---: | ---: |
| Per-message scalar | 30.82 M/s | 32.451 ns |
| **Fused scalar batch** | **35.92 M/s** | **27.771 ns** |
| Fused AVX2 (8 records) | 35.81 M/s | 27.872 ns |
| Fused AVX-512 (16 records) | 34.34 M/s | 29.061 ns |

Batch fusion itself produced the strongest retained improvement: about **+16.5% throughput** and **-14.4% processor CPU/result**. Wider SIMD did not automatically improve this 32-byte AoS schema, so the runtime should select a contract-specific routine rather than enabling the widest available ISA unconditionally.

Results:

- [raw twelve-run CSV](../results/every-fused-vector-processing-ab-raw-12run-2026-09-23.csv)
- [twelve-run summary](../results/every-fused-vector-processing-ab-summary-12run-2026-09-23.csv)


## Negotiated computation-ready blocks

`negotiated_block_layout_ab.c` compares an ordinary 12 KiB AoS record block with an equal-size SoA field-major block that travels over the wire in computation-ready form.

Integrated nine-run medians:

| Path | End-to-end | Payload |
| --- | ---: | ---: |
| AoS AVX2 | 35.25 M/s | 1.128 GB/s |
| **SoA AVX2** | **35.85 M/s** | **1.147 GB/s** |

The integrated gain is modest (~1.7%) because other stages dominate. The processor-only microbenchmark exposes the layout effect directly:

| Path | Processor CPU/result | CPU result rate |
| --- | ---: | ---: |
| AoS AVX2 | 1.080 ns | 925.9 M/s |
| **SoA AVX2** | **0.621 ns** | **1.610 B/s** |

That is about **42.5% lower isolated compute cost** for the field-major negotiated block.

This is an optional contract specialization for numerical `Every` workloads, not a replacement for ordinary fixed records.

Results:

- [wire raw](../results/block-layout-wire-ab-raw-9run-2026-09-23.csv)
- [wire summary](../results/block-layout-wire-ab-summary-9run-2026-09-23.csv)
- [processor raw](../results/block-layout-processor-micro-raw-7run-2026-09-23.csv)
- [processor summary](../results/block-layout-processor-micro-summary-7run-2026-09-23.csv)
