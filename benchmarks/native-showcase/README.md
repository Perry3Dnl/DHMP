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
