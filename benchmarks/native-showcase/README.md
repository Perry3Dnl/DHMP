# DHMP native showcase / Adaptive architecture labs

This directory contains the native Linux/C integration benchmarks used to validate the **current DHMP Adaptive Fixed-Contract architecture** before selected paths are ported into .NET.

The retained model is no longer one showcase version. The current work is split by semantics and workload:

- **Latest:** compact Ring-3 newest-state path;
- **Every / records:** bounded reusable slab ownership;
- **Every / ComputeBlock:** optional computation-ready field-major blocks for numerical workloads.

Historical v2/v3/v4 showcase files remain in the repository for reproducibility, but they are not the current architecture target.

## Current retained labs

| Lab | Purpose | Retained result |
| --- | --- | --- |
| `every_output_slab_v1.c` | Batch Every ownership instead of per-result publication | 33.35 → **129.80 M results/s** |
| `every_receive_slab_lease_ab.c` | Receive directly into a public leased slab | 119.63 → **126.51 M/s** |
| `every_forward_direct_slab_ab.c` | Send directly from the owned output slab | 41.93 → **43.98 M/s** |
| `every_delivery_worker_ab.c` | Move real batch conversion/delivery work to another core | **+18.7% to +48.5%** in tested workloads |
| `every_fused_vector_processing_ab.c` | Fuse developer-facing processing across whole batches | 30.82 → **35.92 M/s** |
| `negotiated_block_layout_ab.c` | Compare AoS record blocks with SoA computation-ready wire blocks | integrated gain modest; processor gain large |
| `showcase_v4.c` / v4 material | Historical Latest cross-protocol zero-hold comparison | still valid for Ring-3 v4 only |

## Adaptive Every: bounded output slabs

The largest integrated Every gain came from publishing one **populated output slab** rather than one ownership update per processed result.

Both A/B paths retained **96 KiB of output payload capacity**.

| Path | End-to-end results/s | Receiver/processor CPU/result | Consumer CPU/result | Results/publication |
| --- | ---: | ---: | ---: | ---: |
| Per-result publication | 33.35 M/s | 29.933 ns | 29.978 ns | 1.0 |
| **Output-slab publication** | **129.80 M/s** | **6.988 ns** | **7.699 ns** | **372.3** |

All retained runs processed and consumed all 30 million results with zero validation errors.

Results:

- [raw nine-run CSV](../results/every-output-slab-e2e-32b-raw-9run-2026-09-23.csv)
- [summary](../results/every-output-slab-e2e-32b-summary-9run-2026-09-23.csv)

## Borrowed receive slabs

When the received wire representation is already directly usable, the receive slab itself can become the public Every batch.

| Path | End-to-end frames/s | Logical payload GB/s | Receiver CPU/frame |
| --- | ---: | ---: | ---: |
| Receive → copy → public slab | 119.63 M/s | 3.83 | 6.305 ns |
| **Borrow receive slab directly** | **126.51 M/s** | **4.05** | **6.184 ns** |

Only split-frame boundary fragments are copied. Complete frames remain in the leased slab until downstream code releases it.

Results:

- [raw twelve-run CSV](../results/every-receive-slab-lease-ab-raw-12run-2026-09-23.csv)
- [summary](../results/every-receive-slab-lease-ab-summary-12run-2026-09-23.csv)

## Direct slab-to-send forwarding

A service that produces wire-ready Every results can transfer ownership of the populated output slab directly to the sender instead of copying it into another sender batch.

| Path | End-to-end results/s | Processor CPU/result | Forward-sender CPU/result |
| --- | ---: | ---: | ---: |
| Slab → copy → sender scratch | 41.93 M/s | 23.238 ns | 23.798 ns |
| **Slab → send directly** | **43.98 M/s** | **21.111 ns** | **22.689 ns** |

The sender retains slab ownership until the entire populated byte range has been accepted. Async/zero-copy APIs would require retaining ownership until their completion notification.

Results:

- [raw twelve-run CSV](../results/every-forward-direct-slab-ab-raw-12run-2026-09-23.csv)
- [summary](../results/every-forward-direct-slab-ab-summary-12run-2026-09-23.csv)

## Dedicated batch delivery worker

The protocol processor can publish a whole slab and return to receive work while a dedicated worker performs conversion and one batch adapter call.

With cache-aware sender/protocol/worker placement, the worker improved median wall-clock throughput by **+18.7% to +48.5%** across the tested synthetic conversion weights.

This is a throughput optimization, not an aggregate-CPU reduction. The extra core and cross-core handoff can make a very light worker worse on unsuitable topology, so worker selection belongs in runtime configuration/AutoTune.

Results:

- [raw seven-run sweep](../results/every-delivery-worker-ab-raw-7run-2026-09-23.csv)
- [summary](../results/every-delivery-worker-ab-summary-7run-2026-09-23.csv)

## Fused batch processing

The processing path now batches the actual computation, not just storage and handoff.

| Routine | End-to-end results/s | Processor CPU/result |
| --- | ---: | ---: |
| Per-message scalar | 30.82 M/s | 32.451 ns |
| **Fused scalar batch** | **35.92 M/s** | **27.771 ns** |
| Fused AVX2 | 35.81 M/s | 27.872 ns |
| Fused AVX-512 gather/scatter | 34.34 M/s | 29.061 ns |

The retained rule is **fuse first, then choose SIMD by negotiated layout and CPU**. The widest available vector ISA is not automatically the fastest implementation.

Results:

- [raw twelve-run CSV](../results/every-fused-vector-processing-ab-raw-12run-2026-09-23.csv)
- [summary](../results/every-fused-vector-processing-ab-summary-12run-2026-09-23.csv)

## Negotiated computation-ready blocks

`negotiated_block_layout_ab.c` compares two equal-size 12 KiB wire blocks:

- ordinary **AoS**: 384 × 32-byte records;
- computation-ready **SoA**: eight contiguous arrays of 384 floats.

The integrated pipeline gain is small because transport/handoff dominate, but the processor-only result isolates the layout effect:

| Path | Processor CPU/result | CPU-side result rate |
| --- | ---: | ---: |
| AoS + AVX2 transpose | 1.080 ns | 925.9 M/s |
| **SoA + AVX2 contiguous fields** | **0.621 ns** | **1.610 B/s** |

This is about **42.5% lower isolated compute cost** and **1.74× processor throughput**.

ComputeBlock is therefore retained as an **optional negotiated contract** for array-native numerical workloads rather than the default record layout.

Results:

- [wire raw](../results/block-layout-wire-ab-raw-9run-2026-09-23.csv)
- [wire summary](../results/block-layout-wire-ab-summary-9run-2026-09-23.csv)
- [processor raw](../results/block-layout-processor-micro-raw-7run-2026-09-23.csv)
- [processor summary](../results/block-layout-processor-micro-summary-7run-2026-09-23.csv)

## Latest / Ring-3

Latest remains a separate retained path because its correctness semantics permit obsolete states to be conflated.

The current design uses:

- 12 KiB reusable receive workspace in the 32-byte reference path;
- three permanent 32-byte retained state slots;
- one shared 32-bit MIDDLE token;
- producer atomic exchange;
- consumer CAS acquisition;
- contract-specialized framing;
- fixed-width carry handling;
- early conflation before expensive work.

The old v4 zero-hold cross-protocol comparison is retained only as the latest **controlled cross-protocol benchmark for the Ring-3 Latest path**. It does not measure the newer Every/ComputeBlock architecture and should not be relabeled as Adaptive.

See:

- [V4_ZERO_HOLD.md](V4_ZERO_HOLD.md)
- [v4 summary](../results/showcase-v4-zero-hold-32b-summary-5run-2026-09-23.csv)
- [full benchmark history](../../docs/BENCHMARKS.md)

## Benchmark rules

- Do not add percentage gains from different A/B experiments.
- Do not compare processor-only rates directly with socket end-to-end rates.
- Logical localhost GB/s is not physical NIC throughput.
- Publish raw data and validation counts alongside summaries.
- Keep Every and Latest correctness semantics explicit.
- New headline protocol-comparison graphs must be generated from a fresh common harness using the current retained code.

## Current next step

The native architecture is mature enough that the highest-value work is now:

1. port the retained Adaptive paths into .NET;
2. build one fresh integrated Adaptive comparison harness;
3. rerun cross-protocol comparisons with the current code rather than old labels;
4. tune transport/runtime settings;
5. repeat on physical LAN hardware.
