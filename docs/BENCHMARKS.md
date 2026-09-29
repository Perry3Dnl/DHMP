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
