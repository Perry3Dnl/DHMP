# DHMP native showcase benchmark

This directory contains the native Linux/C showcase used by the project README to compare two retained DHMP `Latest` processor paths, DHMPS/TLS, and five familiar framing/transport baselines under the same local test harness.

## Compared paths

- DHMP Ring8 Spin
- DHMP Slab6 Hybrid
- DHMPS / TLS 1.3 / Slab6 Hybrid
- Raw TCP / fixed-size frame
- TCP / 4-byte length prefix
- UDP / batched datagrams (`sendmmsg` / `recvmmsg`)
- WebSocket binary framing
- HTTP/1.1 chunk framing

The WebSocket and HTTP rows are framing/parsing microbenchmarks, not complete application-server stacks.

## Published test profile

- 32-byte logical payloads
- 10 µs simulated consumer work
- adaptive slab target: `clamp(frameSize * 512, 16 KiB, 1 MiB)`
- 500 ms warmup
- 1.5 second measured interval
- 2 runs per path
- loopback networking
- sender, receiver, and consumer pinned to separate CPUs when available
- TLS 1.3 for DHMPS

All retained published runs completed with zero validation errors.

The source archive in this directory contains `showcase.c`, the two-run Python runner, Makefile, and this README.
