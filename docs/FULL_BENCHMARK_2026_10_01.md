# Full DHMP benchmark — 1 October 2026

Measured source commit: `257bc3d4841d3a2220f48b6751aa389baabdd89a` (GitHub's tested merge commit). [Run](https://github.com/Perry3Dnl/DHMP/actions/runs/36832616901). Kernel: `Linux-6.17.0-1022-azure-x86_64-with-glibc2.39`. CPU: **AMD EPYC 7763 64-Core Processor**, 4 vCPU. The stored JSON includes the complete reported CPU description. Benchmark programs target .NET 10 in Release. Raw samples are retained under [benchmark-results/2026-10-01](benchmark-results/2026-10-01).

The README presents selected results; the tables below retain every measured configuration. Median and full min–max ranges come from five repetitions. No TCP/UDP comparison, independent security certification or physical two-host pass is claimed.

## Correctness coverage

[Test run](https://github.com/Perry3Dnl/DHMP/actions/runs/36832616933): 790 executions, all passed. Each case runs once per OS; Linux and Windows counts are repetitions across platforms, not distinct tests. Native Linux successes are exercised by the privileged labs; ordinary Windows tests do not establish a Windows raw-IP backend. Some platform guard tests return on their inapplicable OS rather than using xUnit skip markers.

| Suite | Linux cases passed | Windows cases passed |
| --- | ---: | ---: |
| Protocol | 74 | 74 |
| Server | 28 | 28 |
| Licensing | 23 | 23 |
| ASP.NET/client integration | 92 | 92 |
| Raw IPv6 | 111 | 111 |
| Security | 67 | 67 |

The 23 hosted network checks passed. Capture recorded 269,144 data packets (253), 12 setup packets (254), and zero inter-peer TCP/UDP packets. Capture snap length is 128 bytes, so packet contents are truncated; application content is verified separately in the receiver. Configs, PSKs and issuer private keys are excluded from evidence.

## Measurement contracts

- Component cases: 100 ms warm-up per case, five rotated repetitions of at least 500 ms. Core cases inspect every byte returned by the processor using a scalar byte-summing consumer. Latest consumes only the last 32-byte record; Sequential consumes the whole batch. The consumer dominates these timings; they are not the isolated framing-core ceiling.
- Protection cases: complete encrypt/decrypt and byte equality, fresh packet counters. The one-way and echo composition cases both use 1200-byte records with 1160 useful bytes. Echo counts useful bytes once and performs a protected return path. These are memory peers, not sockets. Approximate allocations use process-wide GC allocation deltas; small background/JIT allocations may be included.
- The generation filter reads its application generation and an observation byte; its reported time is an operation cost, not full-body processing throughput. Control/feedback cases encode, authenticate where applicable, decode and compare fields. Session construction includes HKDF/crypto setup/disposal and fresh Guid creation; it is not the network handshake latency.
- Historical-contract mocks: 2 million-message warm-up, 100 million offered 32-byte messages, five rotated process runs. They read the newest record's identity, not every offered byte, and exclude real sockets. Mock batches 128/512 exceed the real network packet budget and are memory-only. The old core sanity sink only observes one int32 from the newest record, explaining its large logical offered number.
- Kernel loopback: unprotected raw IPv6, batches 1/8/44, 200,000 offered records per run, five rotated repetitions. All bytes/identities are checked before unique records are counted; duplicates, loss and reordering are explicit. Rates use send-to-last-arrival active time and exclude final drain waiting. Increasing buffer requests is subject to Linux's actual socket buffer limits. This is one VM's kernel, with no physical NIC and no deliberate pacing.
- API: warm-up 5 calls per route, 5 × 40 calls, one request at a time. Local HTTP website driving plus the protected raw backend round trip, including JSON serialization, application handlers and both sides' configured 500-record/s pacing. It is not an unconstrained API saturation test.
- Raw DECO/1: 1200-byte records / 1160 useful bytes, protected in both directions, MTU 1280. Warm-up 64 confirmations; 5 rotated repetitions at concurrency 1/8/32, each at least 0.5 seconds and 32 groups. Every request content is verified; confirmation requires matching returned bytes. No intentional pacing below the client's effectively unlimited local RejectWindow budget. RTT is measured at send admission, including local queuing. p50 is a true sample median; p95 uses nearest rank. Tables show median per-sample percentiles, not pooled percentiles.

CPU and workload differences prevent comparison with older runs as proof of regression/improvement. A throughput ratio from the memory composition does not predict NIC throughput or API pacing cost. The kernel loss result is a measured overload symptom, not a precise diagnosis of which kernel queue drops packets.

## Component results

| Case | ns/op median (min–max) | Offered GB/s median | Consumed GB/s median | Approx. allocated bytes/op median |
| --- | ---: | ---: | ---: | ---: |
| core-Latest-batch-1 | 18.20 (16.29–23.40) | 1.758 | 1.758 | 0.00 |
| core-Latest-batch-8 | 18.06 (16.14–23.54) | 14.173 | 1.772 | 0.00 |
| core-Latest-batch-44 | 18.12 (16.16–23.40) | 77.699 | 1.766 | 0.00 |
| core-Sequential-batch-1 | 22.75 (15.57–23.02) | 1.406 | 1.406 | 0.00 |
| core-Sequential-batch-8 | 166.78 (95.88–168.18) | 1.535 | 1.535 | 0.00 |
| core-Sequential-batch-44 | 495.88 (490.74–884.55) | 2.839 | 2.839 | 0.00 |
| control-codec-roundtrip | 26.26 (25.97–27.69) | 1.219 | 1.219 | 0.00 |
| latest-generation-filter | 7.88 (7.18–8.64) | 4.059 | 4.059 | 0.00 |
| psk-protect-decode-32 | 2,831.46 (2,824.65–2,845.47) | 0.011 | 0.011 | 0.00 |
| psk-protect-decode-1200 | 4,332.61 (4,329.58–4,365.68) | 0.277 | 0.277 | 0.00 |
| psk-protect-decode-1408 | 4,133.49 (4,118.92–4,205.69) | 0.341 | 0.341 | 0.00 |
| authenticated-feedback-roundtrip | 3,401.53 (3,396.94–3,450.89) | 0.019 | 0.019 | 0.00 |
| psk-session-key-derivation | 22,979.10 (22,930.86–24,703.57) | 0.000 | 0.000 | 928.03 |
| protected-one-way-record-1200 | 4,412.11 (4,396.82–4,541.73) | 0.263 | 0.263 | 0.00 |
| protected-full-echo-record-1200 | 10,226.74 (10,147.52–11,997.29) | 0.113 | 0.113 | 3,472.00 |

## Historical-contract memory measurements

**Logical offered GB/s only. These are not end-to-end or physical-network bandwidth.**

| Harness/configuration | Logical offered GB/s median (min–max) | ns/packet median |
| --- | ---: | ---: |
| direct-ip-core-sanity-batch-1 | 6.403 (6.143–6.409) | 4.997 |
| direct-ip-core-sanity-batch-4 | 34.209 (33.951–34.239) | 3.742 |
| direct-ip-core-sanity-batch-8 | 68.391 (67.556–68.438) | 3.743 |
| direct-ip-core-sanity-batch-16 | 136.751 (135.946–137.029) | 3.744 |
| direct-ip-core-sanity-batch-32 | 273.224 (273.156–273.379) | 3.748 |
| direct-ip-core-sanity-batch-44 | 372.534 (346.784–375.469) | 3.780 |
| mock-ip-ceiling-batch-1 | 14.432 (14.373–14.442) | 2.217 |
| mock-ip-ceiling-batch-4 | 31.613 (31.600–31.632) | 4.049 |
| mock-ip-ceiling-batch-8 | 37.401 (37.165–37.410) | 6.845 |
| mock-ip-ceiling-batch-16 | 40.852 (40.746–41.473) | 12.533 |
| mock-ip-ceiling-batch-32 | 43.748 (43.686–43.848) | 23.407 |
| mock-ip-ceiling-batch-44 | 44.676 (44.324–44.725) | 31.516 |
| mock-ip-ceiling-batch-128 | 43.102 (41.794–43.179) | 95.030 |
| mock-ip-ceiling-batch-512 | 45.726 (45.651–45.827) | 358.308 |
| mock-ipv4-framing-batch-1 | 1.230 (1.215–1.231) | 26.016 |
| mock-ipv4-framing-batch-4 | 4.863 (4.856–4.871) | 26.321 |
| mock-ipv4-framing-batch-8 | 9.073 (8.625–9.111) | 28.214 |
| mock-ipv4-framing-batch-16 | 16.320 (15.956–16.381) | 31.372 |
| mock-ipv4-framing-batch-32 | 23.600 (23.232–24.030) | 43.391 |
| mock-ipv4-framing-batch-44 | 28.442 (28.374–28.696) | 49.504 |
| mock-ipv6-framing-batch-1 | 7.880 (7.801–7.894) | 4.061 |
| mock-ipv6-framing-batch-4 | 18.668 (18.643–18.677) | 6.857 |
| mock-ipv6-framing-batch-8 | 23.916 (23.298–23.960) | 10.704 |
| mock-ipv6-framing-batch-16 | 28.167 (28.046–28.321) | 18.178 |
| mock-ipv6-framing-batch-32 | 29.577 (29.323–31.037) | 34.621 |
| mock-ipv6-framing-batch-44 | 31.211 (20.921–31.828) | 45.112 |

## Kernel loopback

| Batch | Received MB/s median (min–max) | Loss % median (min–max) | Duplicates max | Reordered max | Invalid packets max |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 5.1 (4.7–6.8) | 0.00 (0.00–0.00) | 0 | 0 | 0 |
| 8 | 43.4 (37.8–44.4) | 0.00 (0.00–0.00) | 0 | 0 | 0 |
| 44 | 143.1 (141.7–144.3) | 0.00 (0.00–0.00) | 0 | 0 | 0 |

## Earlier hosted run: retain overload evidence

An initial run on **Intel Xeon Platinum 8573C, 4 vCPU** used the same kernel accounting harness. [Run](https://github.com/Perry3Dnl/DHMP/actions/runs/36831931127), source `5ecd5fb871b4ecae93f2967bdd1080907966c2a8`. Its component and kernel raw samples are retained in `initial-components-kernel.json`. Its preliminary very short network-echo timing is superseded by the longer primary run and is not used as a performance headline. Host/CPU differences prevent combining these distributions into one result.

Batch 44 uniquely received **164.8 MB/s median**, but lost **17.23% median (15.80–19.58%)** of offered records. Batches 1 and 8 had no observed loss. Duplicate, reordered and invalid counts were zero. This is important overload evidence even though the primary AMD-hosted run had no observed loss. The source change between runs increased echo sample duration and corrected API median calculation; it did not change kernel benchmark code.

## API and protected raw echo

| Case | Completions/s or useful MB/s median (min–max) | p50 ms median (min–max) | p95 ms median (min–max) |
| --- | ---: | ---: | ---: |
| api-get | 488.93 (474.53–499.04) requests/s | 2.323 (2.290–2.351) | 2.538 (2.385–2.641) |
| api-json-post | 13.61 (13.52–13.63) requests/s | 73.292 (73.176–73.884) | 74.653 (74.356–75.574) |
| raw-protected-echo-concurrency-1 | 12.78 (5.91–13.67) MB/s | 0.085 (0.081–0.177) | 0.112 (0.101–0.242) |
| raw-protected-echo-concurrency-8 | 24.68 (16.30–25.77) MB/s | 0.130 (0.112–0.229) | 0.283 (0.203–0.397) |
| raw-protected-echo-concurrency-32 | 28.85 (20.56–29.78) MB/s | 0.239 (0.189–0.376) | 0.813 (0.687–1.365) |

## Reproduce and remaining gaps

Run the `Full DHMP benchmark suite` workflow, or build the benchmark projects in Release and run `tools/run_full_benchmarks.py` followed by the privileged `tools/run_raw_api_lab.py --benchmark --echo-sample ... --capture-snaplen 128`. Supply actual built DLL paths and appropriate namespace/raw-socket privileges. `tools/render_benchmark_report.py` recreates these tables/figures from the retained measurement JSON (requires matplotlib).

All current test suites were executed. Performance measurements cover the core, security/control costs, mocks, verified kernel I/O, API path and echo option. Licensing, multi-peer lifecycle, bounded dispatch/overload, cancellation, rate policies, replay and limits have correctness evidence, but this run does not provide a dedicated performance number for every feature combination. Long-duration soak, physical NICs/two hosts, dynamic PMTUD, competing-flow fairness, public internet paths, independent security review and production release readiness remain open.
