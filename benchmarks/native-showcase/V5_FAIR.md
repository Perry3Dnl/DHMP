# Showcase v5 — fair fixed-record framing benchmark

This benchmark replaces the older v4 README comparison.

Its purpose is narrow: compare steady-state framing/parser paths for the same stream of **32-byte logical application records** while allowing each framing shape to use a reasonable batching strategy.

It is **not** a full-stack comparison of Kestrel, grpc-dotnet, a WebSocket framework, MQTT broker, NATS server, or other production frameworks.

## Correctness audit before timing

The exact retained v5 source was checked before the result set was accepted:

- compiled with `-O3 -march=native -std=c11 -Wall -Wextra -Wpedantic -Werror -pthread`;
- each stream parser self-tested with fragmented input chunk sizes `1, 2, 3, 7, 31, 32, 33, 127, 511, 4096, 8191, 12288` bytes;
- every stream path detects deliberate payload corruption;
- every framed stream path rejects/detects deliberate framing corruption;
- AddressSanitizer + UndefinedBehaviorSanitizer self-tests passed;
- sanitized short smoke runs passed for every path;
- every retained timed run had:
  - zero framing errors;
  - zero payload-sequence errors;
  - zero consumer validation errors;
  - zero UDP payload errors;
  - zero configuration/affinity errors.

The CSV summaries were independently recomputed from the raw CSVs and matched exactly.

## Common loopback setup

- 32-byte logical application record;
- Linux native C on localhost;
- sender CPU 0, receiver CPU 1, consumer CPU 2;
- 12 KiB receive read target;
- roughly 256 KiB prebuilt sender cycle;
- `TCP_NODELAY` on all TCP stream paths;
- 4 MiB socket buffer requested; Linux reported 8 MiB effective send/receive buffers;
- 300 ms warmup;
- 1.0 s measured interval;
- five rotated path orders;
- Ring-3 Latest publication after parsing;
- all logical input records are sequence-validated before Latest conflation.

The loopback wall-rate numbers are noisy on this shared KVM host, so medians are always published with the five-run min–max range.

## Framing definitions

The benchmark deliberately does **not** charge WebSocket/HTTP framing once per 32-byte application record where the protocol can amortize framing across a larger payload.

| Path | v5 steady-state framing |
| --- | --- |
| DHMP | fixed 32-byte records, no repeated DHMP per-record header |
| Raw TCP | fixed 32-byte records |
| TCP varint | 1-byte length + 32-byte record |
| TCP len4 | 4-byte big-endian length + 32-byte record |
| WebSocket | one unmasked server→client 8 KiB binary frame containing 256 fixed32 application records |
| HTTP/1.1 chunked | one 8 KiB chunk containing 256 fixed32 application records; initial HTTP headers treated as setup |
| HTTP/2 DATA | one 8 KiB DATA frame containing 256 fixed32 application records |
| gRPC/H2 framing | 221 gRPC 5-byte envelopes + 32-byte messages packed inside one HTTP/2 DATA frame |
| MQTT QoS0 | one PUBLISH per 32-byte logical record using a fixed one-byte topic |
| NATS PUB | one PUB command per 32-byte logical record using a fixed subject |
| UDP | one 32-byte datagram per record, batched with sendmmsg/recvmmsg; non-equivalent unreliable reference |

Static wire bytes per logical record in this harness:

- DHMP/raw: 32.000000 B;
- varint: 33.000000 B;
- len4: 36.000000 B;
- WebSocket batching: 32.015625 B;
- HTTP/1.1 8 KiB chunks: 32.031250 B;
- HTTP/2 8 KiB DATA: 32.035156 B;
- gRPC packed H2: 37.040724 B;
- MQTT: 37.000000 B;
- NATS: 44.000000 B.

## Important HTTP/2 / gRPC limitation

The HTTP/2 and gRPC rows are **framing kernels**.

They do not implement the complete HTTP/2 connection machinery: SETTINGS exchange, HPACK/header blocks, stream lifecycle, flow-control state transitions, prioritization, or a production framework/server.

The gRPC row similarly measures gRPC message envelopes packed in an H2 DATA framing shape, not grpc-dotnet.

A real full-stack comparison remains a separate pending benchmark.

## Processor-only parser benchmark

To separate parser/layout CPU cost from noisy localhost scheduling, v5 also runs a processor-only benchmark:

- seven rotated runs per stream path;
- about 100 million logical records per run;
- same 12 KiB chunking model;
- same framing parsers;
- parser selected once per contract/path;
- same payload sequence validation;
- zero framing/payload errors.

This processor-only result is the cleaner measurement of framing/parser CPU. Its logical GB/s value is a CPU processing rate and **not network throughput**.

## Interpretation rules

- Do not rank close loopback medians when their ranges overlap heavily.
- DHMP and raw fixed TCP have essentially the same fixed32 wire work; small differences are noise.
- The v5 wall-rate result is allowed to show another framing kernel above DHMP. It is not normalized to make DHMP win.
- Processor-only rates must not be presented as NIC/network throughput.
- Full-stack claims require real full-stack implementations.
- If the common harness changes again, every row must be rerun before a replacement README chart is published.

## Retained result files

- `showcase-v5-fair-32b-raw-5run-2026-09-23.csv`
- `showcase-v5-fair-32b-summary-5run-2026-09-23.csv`
- `showcase-v5-parser-micro-raw-7run-2026-09-23.csv`
- `showcase-v5-parser-micro-summary-7run-2026-09-23.csv`

Graphs:

- `showcase-v5-fair-loopback-throughput-2026-09-23.svg`
- `showcase-v5-fair-parser-cpu-2026-09-23.svg`
- `showcase-v5-fair-parser-payload-2026-09-23.svg`
