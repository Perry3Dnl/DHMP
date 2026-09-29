# Direct-IP benchmark evidence

The active benchmark set is restricted to the packet direction. No historical comparison
chart is used as the current DHMP performance headline.

| Project | Measurement scope |
| --- | --- |
| mock-ip-ceiling | In-memory batch construction and newest-record observation |
| mock-ipv4-framing | In-memory IPv4 header construction/parsing |
| mock-ipv6-framing | In-memory IPv6 header construction/parsing |
| raw-ipv6-kernel | Experimental raw IPv6 kernel loopback; backend assumptions still need validation |

The mock V1 contracts are retained unchanged. They are not production network implementations.
A raw IP socket is a kernel packet-I/O boundary, not an encapsulation in another transport.

## Prior direct-IP checkpoints

The earlier direction document recorded approximately 50.98 GB/s logical offered payload
for mock IP at batch 44, 28.21 GB/s for mock IPv4 framing and 28.95 GB/s for mock IPv6 framing.
These are historical software-ceiling observations, not fresh measurements from this migration
and not network throughput. Consult the [pre-migration snapshot](https://github.com/Perry3Dnl/DHMP/tree/7b85bd961b12d433ed8fd3ea3b5f623dc47a20e7/docs/DIRECT_TRANSPORT_DIRECTION.md)
for their original context.

The raw IPv6 experiment's counters are preliminary: packet/header interpretation, receive
truncation, duplicate accounting and loss measurement must be validated before using them
as a production network claim. The experimental next-header value is a lab choice, not a
frozen interoperable wire specification.

## Historical evidence

Earlier benchmark source, CSV files, charts and stream/carry experiments are retained in
[git history](https://github.com/Perry3Dnl/DHMP/tree/7b85bd961b12d433ed8fd3ea3b5f623dc47a20e7/benchmarks). They were removed from the active tree to prevent
an old implementation from being mistaken for the current direction.

The earlier single-carry A/B reported about 14% lower stream-framing time.
That result remains historical and does not transfer to a packet processor with no carry.

See [BENCHMARK_FAIRNESS.md](BENCHMARK_FAIRNESS.md) before publishing a result.
