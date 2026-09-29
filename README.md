<p align="center">
  <img src="assets/dhmp-logo.webp" alt="DHMP logo" width="512">
</p>

# DHMP — Direct Headerless Message Protocol

**DHMP is a standalone message protocol being built directly over IP. IPv6 is the current implementation target.**

The active project has no TCP/UDP compatibility data path. Earlier stream and compatibility implementations remain in git history only.

## V1 data-plane rule

DHMP V1 keeps the hot data path deliberately simple:

```text
IPv6 packet
  -> DHMP headerless payload
     -> fixed record
     -> fixed record
     -> fixed record
```

There are **zero DHMP header bytes** before the first record, between records or after the last record.

The current raw IPv6 research path uses IPv6 Next Header value `253` as an experimental protocol binding. That value is not presented as a permanent IANA assignment for DHMP.

Before packets are exchanged, both endpoints must already agree on one `DhmpSessionContract`: protocol version, fixed record size, publication mode, Pmax and maximum packet payload. Session discovery/negotiation still needs to be built. The first real .NET network backend is now `DHMP.RawIpv6`, currently Linux-only.

## Active architecture

IP packet I/O -> complete headerless DHMP payload -> fixed-contract validation ->
Latest or Sequential publication -> typed/application boundary.

- One message is one fixed-size record.
- One IP packet carries one or more whole records.
- A record never continues in another packet.
- Invalid or incomplete packets are rejected as a whole.
- Sequential publishes complete records in receive order.
- Latest publishes the final record of the received packet.
- V1 has no cross-packet sequence/freshness field.
- DHMP adds no delivery ACK, retransmission, replay history or hidden reliable queue.
- Buffer ownership must be explicit across asynchronous boundaries.
- Pmax is a configured local send budget, not a capacity guarantee or congestion controller.

## What exists today

| Component | Status |
| --- | --- |
| Headerless fixed-record packet processor | Implemented |
| Explicit `DhmpSessionContract` | Implemented |
| Client packet facade | Uses an explicitly supplied direct-IP sender |
| Server packet facade | Processes already-received complete IP payloads |
| Licensing and ASP.NET host integration | Offline validation at host startup |
| Raw IPv6 kernel experiment | Experimental loopback harness |
| `DHMP.RawIpv6` Linux backend | Implemented first direct-IP sender/receiver; not production-hardened |
| Session discovery/negotiation | Still to build |
| Reordering/freshness, congestion policy and secure direct-IP profile | Still to specify/build |

Removing the old transports does **not** mean a production-ready network stack already exists. The repository now intentionally favors a clean protocol boundary over temporary compatibility.

## .NET packages

All active projects target .NET 10 and use the canonical `DHMP.*` spelling.

| Project | Responsibility |
| --- | --- |
| DHMP.Protocol | Session/fixed contracts, packet processor, send budget and direct-IP sender boundary |
| DHMP.Client | Per-session sending facade |
| DHMP.Server | Per-session receiving facade and typed/buffer ownership building blocks |
| DHMP.Licensing | Offline key verification |
| DHMP.AspNetCore | Dependency injection and license/startup gating |
| DHMP.RawIpv6 | Linux raw-IPv6 sender/receiver for one explicit peer/session |

`AddDHMP(applicationId, licenseKey, publicVerificationKey)` configures the license gate. It does not bind an endpoint or create a hidden transport.

A `DhmpSessionContract` and an `IDhmpPacketSender` implementation are required before a client can send data. `DhmpRawIpv6PacketSender` is the first concrete implementation. Creating raw IPv6 sockets on Linux requires appropriate raw-socket privileges/capabilities.

## Validation

```sh
python3 tools/check_architecture.py
dotnet test tests/DHMP.Protocol.Tests -c Release
dotnet test tests/DHMP.AspNetCore.Tests -c Release
dotnet test tests/DHMP.Licensing.Tests -c Release
dotnet test tests/DHMP.RawIpv6.Tests -c Release
```

CI guards the architecture, unit/integration tests and compilation of the direct-IP backend. Physical two-host measurements and privileged raw-socket validation remain separate.

## Measurements and history

[Benchmark scope and retained evidence](docs/BENCHMARKS.md) separates memory experiments, kernel experiments and physical network results. There is no published production direct-IP speedup claim.

Earlier stream-framing and compatibility results remain in [git history](https://github.com/Perry3Dnl/DHMP/tree/7b85bd961b12d433ed8fd3ea3b5f623dc47a20e7). They are historical evidence, not the active runtime architecture.

## Documentation

- [DHMP wire contract V1](docs/WIRE_CONTRACT_V1.md)
- [Authoritative direct-IP direction](docs/DIRECT_TRANSPORT_DIRECTION.md)
- [Current implementation status](docs/CURRENT_STATUS.md)
- [Protocol design notes](docs/PROTOCOL_DRAFT.md)
- [Architecture and ownership](docs/ARCHITECTURE_COMPARISON.md)
- [.NET API design](docs/DOTNET10_PACKAGE_DESIGN.md)
- [Conformance](docs/CONFORMANCE.md)
- [Licensing](docs/LICENSING_DESIGN.md)
- [Direct-IP implementation plan](docs/TRANSPORT_TUNING_TODO.md)
