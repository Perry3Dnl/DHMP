# Native showcase benchmark

This benchmark compares two retained DHMP Latest processor paths, a TLS-wrapped DHMPS path, and five familiar wire/framing baselines under the same latest-state workload.

Compared paths:
- DHMP Latest / Ring8 Spin
- DHMP Latest / Slab6 Hybrid
- DHMPS / TLS / Slab6 Hybrid
- Raw TCP / fixed-size
- TCP / 4-byte length prefix
- UDP / batched datagrams
- WebSocket binary framing
- HTTP/1.1 chunk framing on one persistent body stream

Workload:
- 32 B and 256 B payloads
- two measured runs per point
- 1.5 s per run
- 10 µs simulated consumer work
- persistent localhost transport
- handshake/setup outside timing
- no compression
- Latest/conflating consumer semantics in every case
- adaptive DHMP slab target: `clamp(frameSize * 512, 16 KiB, 1 MiB)`

The WebSocket and HTTP cases measure framing/parsing paths in this native lab, not complete application frameworks, routing stacks, or request handlers. They should not be interpreted as full end-to-end framework benchmarks.

The source archive contains the C benchmark, runner, Makefile, and local TLS test-certificate generation.