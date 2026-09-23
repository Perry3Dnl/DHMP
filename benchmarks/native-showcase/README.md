# DHMP native showcase benchmark

This directory contains the native Linux/C comparison harness used to put DHMP receive strategies and familiar framing/transport baselines under one local workload.

## Current showcase v2

Ring-3 Fixed-Slab Latest is now a first-class path in the same harness as the other benchmark rows.

Compared paths:

- **DHMP Ring-3 Fixed-Slab Latest**
- DHMP Ring8
- DHMP Slab6
- DHMPS / TLS 1.3 / Ring-3
- Raw TCP / fixed-size frame
- TCP / 4-byte length prefix
- UDP / datagram
- WebSocket binary framing
- HTTP/1.1 chunk framing

The WebSocket and HTTP rows are framing/parser microbenchmarks rather than complete application-server stacks. The UDP v2 row is an unbatched datagram path.

## v2 test profile

- 32-byte logical payloads
- fixed 12 KiB receive workspace
- 3 retained slots for Ring-3 (96 B at 32 B/frame)
- 8 retained slots for Ring8
- 6 retained slots for Slab6
- 500 ms warmup
- 1.5 second measured interval
- 3 runs per path
- 10 µs simulated consumer work
- loopback networking
- sender, receiver, and consumer CPU pinning when available
- TLS 1.3 for DHMPS
- zero validation errors in all retained v2 runs

The v2 source archive is [DHMP-native-showcase-source.zip](DHMP-native-showcase-source.zip). It contains the integrated `showcase_v2.c` harness and build files.

## v2 results

- [raw three-run CSV](../results/showcase-v2-32b-raw-3run-2026-09-23.csv)
- [three-run median summary](../results/showcase-v2-32b-summary-3run-2026-09-23.csv)
- [logical input-rate chart](../results/charts/showcase-v2-32b-input-2026-09-23.svg)
- [useful-publication chart](../results/charts/showcase-v2-32b-published-2026-09-23.svg)

At 32 B the retained medians are:

| Path | Input frames/s | Useful publications/s | Retained state |
| --- | ---: | ---: | ---: |
| DHMP Slab6 | 179.2 M | 78.2k | 192 B |
| **DHMP Ring-3 Fixed-Slab** | **174.2 M** | **74.1k** | **96 B** |
| DHMP Ring8 | 150.0 M | 72.8k | 256 B |
| DHMPS / TLS / Ring-3 | 65.1 M | 71.6k | 96 B |

The important Ring-3 result is not that it wins every metric. Slab6 is slightly higher in this run. Ring-3 retains half as much application-state payload while remaining in essentially the same high-throughput class.

These are native localhost architecture results, not physical-network throughput claims and not direct measurements of the .NET implementation.
