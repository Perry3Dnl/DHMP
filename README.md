<p align="center">
  <img src="assets/dhmp-logo.webp" alt="DHMP logo" width="512">
</p>

# DHMP — Direct Headerless Message Protocol

**DHMP is being built as its own packet layer directly over IP. IPv6 is the current
research target.** The active implementation has no TCP/UDP compatibility path.

At setup, endpoints must agree on a fixed message contract. During operation, each
IP payload carries a batch of complete fixed-size messages. DHMP manages its own
packet processing, bounded buffers, ownership, publication policy and send budget.

## Active architecture

IP packet I/O → complete DHMP packet payload → fixed-contract validation →
Latest or Sequential batch publication → typed boundary → application.

- A message fits entirely inside one packet. Incomplete packets are rejected; bytes
  from separate packets are never combined into a message.
- Sequential publishes complete received records in arrival order. It is not reliable delivery.
- Latest selects the last record in an incoming batch. Cross-packet freshness/reordering
  handling remains an explicit open design item.
- No DHMP delivery ACK, retransmission, replay history or unbounded queue.
- Buffer ownership must be explicit across asynchronous boundaries.
- Pmax is a configured per-session send budget, not a capacity guarantee or congestion controller.

## What exists today

| Component | Status |
| --- | --- |
| Fixed-contract packet processor | Implemented; borrowed batch publication without carry storage |
| Client packet facade | Validates/budgets packets and calls an explicitly supplied packet sender |
| Server packet facade | Processes complete packet payloads; does not open a listener |
| Licensing and ASP.NET host integration | Offline validation at host startup |
| IPv4/IPv6 framing experiments | In-memory, separately classified benchmarks |
| Raw IPv6 kernel experiment | Experimental loopback harness; not a production backend |
| Production direct-IP backend and session negotiation | Still to build |
| Reordering/freshness, congestion policy and secure direct-IP profile | Still to specify and validate |

Removing the earlier transport implementations does **not** mean a production-ready
network stack has already been implemented.

## .NET packages

All active projects target .NET 10 and use the canonical `DHMP.*` spelling.

| Project | Responsibility |
| --- | --- |
| DHMP.Protocol | Fixed packet contract, batch processor, send budget and packet-sender boundary |
| DHMP.Client | Per-session sending facade using an explicit direct-IP backend |
| DHMP.Server | Packet receiving facade and typed/buffer ownership building blocks |
| DHMP.Licensing | Offline key verification |
| DHMP.AspNetCore | Dependency injection and license/startup gating |

`AddDHMP(applicationId, licenseKey, publicVerificationKey)` configures the license
gate. It does not bind an endpoint or choose a transport. A session contract and
an `IDhmpPacketSender` implementation are required to construct a client.

## Validation

```sh
python3 tools/check_architecture.py
dotnet test tests/DHMP.Protocol.Tests -c Release
dotnet test tests/DHMP.AspNetCore.Tests -c Release
dotnet test tests/DHMP.Licensing.Tests -c Release
```

CI checks architecture, builds the packet experiments and runs the tests on Linux and
Windows. Physical network measurements require the corresponding backend and hardware.

## Measurements and history

[Benchmark scope and retained evidence](docs/BENCHMARKS.md) separates memory experiments,
kernel experiments and physical network results. There is no published production
direct-IP speedup claim.

Earlier stream-framing and compatibility results, including the single-carry A/B,
remain in [git history](https://github.com/Perry3Dnl/DHMP/tree/7b85bd961b12d433ed8fd3ea3b5f623dc47a20e7). They are not the active runtime or direct-IP evidence.

## Documentation

- [Authoritative direct-IP direction](docs/DIRECT_TRANSPORT_DIRECTION.md)
- [Current implementation status](docs/CURRENT_STATUS.md)
- [Packet protocol draft](docs/PROTOCOL_DRAFT.md)
- [Architecture and ownership](docs/ARCHITECTURE_COMPARISON.md)
- [.NET API design](docs/DOTNET10_PACKAGE_DESIGN.md)
- [Conformance](docs/CONFORMANCE.md)
- [Licensing](docs/LICENSING_DESIGN.md)
- [Direct-IP implementation plan](docs/TRANSPORT_TUNING_TODO.md)
