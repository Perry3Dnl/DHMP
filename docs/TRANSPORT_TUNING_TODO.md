# Transport tuning follow-up

This is a short implementation note for a later optimization round. It does **not** change the DHMP wire protocol.

## Goal

Benchmark and package sensible transport/runtime defaults around DHMP so the reference implementation can reduce kernel/socket overhead without requiring users to hand-tune their system.

## First settings to sweep

- receive workspace size: 4 / 8 / 12 / 16 / 32 / 64 / 128 KiB
- sender batch size: 4 KiB through 1 MiB
- `SO_RCVBUF`
- `SO_SNDBUF`
- `TCP_NODELAY` vs normal coalescing / `TCP_CORK` where applicable
- spin / yield / adaptive waiting
- CPU affinity and cache topology
- TLS record/buffer sizing for DHMPS

## Later platform-specific paths

- `io_uring`
- registered buffers
- multishot receive
- busy polling
- kernel TLS
- zero-copy send
- NIC/RSS affinity diagnostics

## Benchmark rule

Keep two categories separate:

1. **Protocol comparison:** same transport settings for DHMP and baselines.
2. **Maximum DHMP implementation:** use the best validated DHMP-specific runtime settings.

Measure logical frames/s, payload throughput, recv/send syscalls, bytes per syscall, receiver/sender CPU per frame, publications/s, validation errors, latency, context switches, and cache misses where available.

## Packaging direction

These belong in the implementation/runtime, not the protocol specification.

Potential future API shape:

```text
Default
LowLatency
Throughput
HighPerformance
AutoTune
```

## Revisit

Revisit after the RX -> processor -> output-slab pipeline is integrated and benchmarked, so transport tuning is measured against the current fastest processor architecture rather than an obsolete path.
