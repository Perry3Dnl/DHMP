# Direct-IP benchmark evidence

The active benchmark set is restricted to direct packet research. No historical compatibility comparison chart is used as the current DHMP performance headline.

| Project | Measurement scope |
| --- | --- |
| mock-ip-ceiling | In-memory batch construction and newest-record observation |
| mock-ipv4-framing | In-memory IPv4 header construction/parsing |
| mock-ipv6-framing | In-memory IPv6 header construction/parsing |
| raw-ipv6-kernel | Experimental raw IPv6 kernel loopback; production backend assumptions still need validation |

The mock benchmark contracts are retained as historical evidence for their exact scopes. They are not production network implementations.

A raw IP socket is a kernel packet-I/O boundary, not an encapsulation in another application transport.

## V1 protocol context

The active [DHMP wire contract V1](WIRE_CONTRACT_V1.md) defines a headerless DHMP data payload containing complete fixed-size records only.

The raw IPv6 research path uses Next Header `253` as the current experimental binding. That value is documented as experimental and is not a permanent IANA assignment.

Benchmark harnesses must not silently add a different DHMP data header and still label the result as V1.


## 2026-09-29 standalone sanity benchmark

These runs were made after the standalone direct-IP architecture, split wire/local policy model and Control V1 work.

They are deliberately separated by scope. GitHub-hosted runners are not stable benchmark hardware, so cross-run differences versus older checkpoints are not treated as regressions or improvements by themselves.

### Current protocol core

Run: https://github.com/Perry3Dnl/DHMP/actions/runs/36539462911

Hardware reported by the runner: 4 vCPU, Intel Xeon Platinum 8573C.

This benchmark calls the actual `DhmpWireContract`, `DhmpReceivePolicy` and `DhmpPacketProcessor` in Latest mode. It performs no socket, kernel or NIC I/O. Logical GB/s is therefore an equivalent fixed-record processing rate, not memory-copy or network bandwidth.

| Batch | Payload | Median logical GB/s | Median ns/packet |
| ---: | ---: | ---: | ---: |
| 1 | 32 B | 7.317 | 4.373 |
| 4 | 128 B | 34.586 | 3.701 |
| 8 | 256 B | 68.755 | 3.723 |
| 16 | 512 B | 135.557 | 3.777 |
| 32 | 1024 B | 266.057 | 3.849 |
| 44 | 1408 B | 373.643 | 3.768 |

Batch 44 five-run range: 287.281–377.729 logical GB/s. One run was materially slower than the other four, so the full range is retained rather than hidden.

### Mock packet ceiling

Run: https://github.com/Perry3Dnl/DHMP/actions/runs/36539161827

Hardware reported by the runner: 4 vCPU, AMD EPYC 7763.

At batch 44 the five logical payload results were 44.217, 44.567, 44.654, 42.477 and 44.691 GB/s; median **44.567 GB/s**.

This benchmark is an in-memory packet-boundary ceiling and does not call the production packet processor.

### Mock IPv6 framing

Run: https://github.com/Perry3Dnl/DHMP/actions/runs/36539167448

Hardware reported by the runner: 4 vCPU, AMD EPYC 9V74.

At batch 44 the five logical payload results were 36.552, 37.369, 37.342, 36.907 and 37.364 GB/s; median **37.342 GB/s**.

This includes construction/parsing of a 40-byte IPv6 header in memory. It does not include socket, kernel or NIC work.

### Raw IPv6 kernel attempt

Run: https://github.com/Perry3Dnl/DHMP/actions/runs/36539172049

The project built with zero warnings and zero errors, but the GitHub-hosted runner reported `CapEff: 0` and `Socket(AF_INET6, SOCK_RAW, 253)` failed with `Protocol not supported`.

No raw-kernel throughput number is therefore reported from that run. This is an environment limitation, not evidence of a DHMP throughput result either way. The workflow now records unsupported hosted runners as an explicit skip rather than a failed benchmark.


## Prior direct-IP checkpoints

The earlier direction work recorded approximately 50.98 GB/s logical offered payload for mock IP at batch 44, 28.21 GB/s for mock IPv4 framing and 28.95 GB/s for mock IPv6 framing.

These are historical software-ceiling observations, not fresh measurements from the standalone-protocol migration and not physical network throughput.

Consult the [pre-migration snapshot](https://github.com/Perry3Dnl/DHMP/tree/7b85bd961b12d433ed8fd3ea3b5f623dc47a20e7/docs/DIRECT_TRANSPORT_DIRECTION.md) for their original context.

The raw IPv6 experiment's counters remain preliminary. Packet/header interpretation, receive truncation, duplicate accounting and loss measurement must be validated before using them as a production network claim.

## Historical evidence

Earlier benchmark source, CSV files, charts and stream/carry experiments are retained in [git history](https://github.com/Perry3Dnl/DHMP/tree/7b85bd961b12d433ed8fd3ea3b5f623dc47a20e7/benchmarks).

They were removed from the active tree to prevent an old implementation from being mistaken for the current architecture.

The earlier single-carry A/B reported about 14% lower stream-framing time. That result remains historical and does not transfer to a direct-IP packet processor with no carry.

See [BENCHMARK_FAIRNESS.md](BENCHMARK_FAIRNESS.md) before publishing a result.
