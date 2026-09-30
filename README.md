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

The current raw IPv6 research path uses experimental IPv6 protocol / Next Header `253` for headerless DHMP data and `254` for DHMP control/handshake packets. Neither value is a permanent DHMP assignment.

Before data packets are exchanged, endpoints can run the implemented Control V1 HELLO/ACCEPT/REJECT handshake for an already configured IPv6 peer. It verifies wire version, fixed record size and schema identity, exchanges receive capability and clamps the local send ceiling. Pmax and Latest/Sequential remain local. Peer discovery is still future work; optional PSK possession authentication is provided by the separate experimental security profile.

## Active architecture

IP packet I/O -> complete headerless DHMP payload -> fixed-contract validation ->
Latest or Sequential publication -> typed/application boundary.

- One message is one fixed-size record.
- One IP packet carries one or more whole records.
- A record never continues in another packet.
- Invalid or incomplete packets are rejected as a whole.
- Sequential publishes complete records in receive order.
- Latest publishes the final record of the received packet.
- V1 has no protocol-owned cross-packet sequence field. Optional `DhmpLatestGenerationFilter` can use an application-owned 64-bit generation inside the record to drop stale/duplicate Latest state without adding DHMP bytes.
- DHMP adds no delivery ACK, retransmission, replay history or hidden reliable queue.
- Buffer ownership must be explicit across asynchronous boundaries.
- Pmax is local sender policy. `RejectWindow` preserves the hard fixed-window budget; `SmoothPacing` spaces packet submissions over time. With the PSK profile, authenticated receiver overload feedback and rolling secure-path loss telemetry can adapt SmoothPacing downward and recover gradually. RTT is measured but not yet used as an independent throttle signal; this is still not a complete network congestion-control algorithm.

## What exists today

| Component | Status |
| --- | --- |
| Headerless fixed-record packet processor | Implemented |
| `DhmpWireContract` + separate send/receive policies | Implemented |
| Client packet facade | Uses an explicitly supplied direct-IP sender |
| Server packet facade | Processes already-received complete IP payloads |
| Licensing and ASP.NET host integration | Offline validation at host startup |
| Raw IPv6 kernel experiment | Experimental loopback harness |
| `DHMP.RawIpv6` Linux backend | Direct data sender/receiver plus Control V1 handshake for a configured peer |
| Compatibility/capability negotiation | Implemented HELLO/ACCEPT/REJECT; discovery still pending |
| Multi-peer server routing | Source IPv6 routing with drained removal/replacement; one registered V1 session per address |
| Cross-packet Latest freshness | Implemented opt-in application-generation filter; no extra DHMP wire bytes |
| Smooth local pacing | Implemented opt-in Pmax pacing with experimental authenticated pressure adaptation |
| IPv6 path-MTU budgeting | Implemented known-PMTU budgeting + whole-record/security alignment; dynamic PMTUD still pending |
| Bounded async receive overload | Implemented: Latest replaces one pending batch; Sequential rejects when bounded queue is full |
| PSK secure packet profile | Experimental ChaCha20-Poly1305/HKDF/HMAC profile with V2 challenge/confirm setup; independent review pending |
| Authenticated receiver backpressure | Implemented experimental pressure feedback + bounded adaptive pacing |
| Secure path telemetry | Implemented rolling protected-packet loss window + authenticated RTT probe/echo |
| Full network congestion control and forward-secret/public-key security | Still to specify/build |

Removing the old transports does **not** mean a production-ready network stack already exists. The repository now intentionally favors a clean protocol boundary over temporary compatibility.

## .NET packages

All active projects target .NET 10 and use the canonical `DHMP.*` spelling.

| Project | Responsibility |
| --- | --- |
| DHMP.Protocol | Wire contract, local policies, packet processor, control codec/negotiation, budget and direct-IP sender boundary |
| DHMP.Client | Per-session sending facade |
| DHMP.Server | Per-session receiving facade and typed/buffer ownership building blocks |
| DHMP.Licensing | Offline key verification |
| DHMP.AspNetCore | Dependency injection and license/startup gating |
| DHMP.RawIpv6 | Linux raw-IPv6 data path, bounded source-address multi-peer routing, compatibility and PSK security handshakes |
| DHMP.Security | Experimental PSK packet protection, replay window and protected sender wrapper |

`AddDHMP(applicationId, licenseKey, publicVerificationKey)` configures the license gate. It does not bind an endpoint or create a hidden transport.

A `DhmpWireContract`, `DhmpSendPolicy` and an `IDhmpPacketSender` are required for a client. A server uses the same wire contract with its own `DhmpReceivePolicy`. `DhmpRawIpv6PacketSender` is the first concrete sender implementation. The optional `DHMP.Security` profile can wrap that sender and decode before the server; it adds 24 bytes per protected data packet. Creating raw IPv6 sockets on Linux requires appropriate raw-socket privileges/capabilities.

## Validation

```sh
python3 tools/check_architecture.py
dotnet test tests/DHMP.Protocol.Tests -c Release
dotnet test tests/DHMP.AspNetCore.Tests -c Release
dotnet test tests/DHMP.Licensing.Tests -c Release
dotnet test tests/DHMP.RawIpv6.Tests -c Release
dotnet test tests/DHMP.Security.Tests -c Release
```

CI guards the architecture, unit/integration tests and compilation of the direct-IP backend. Physical two-host measurements and privileged raw-socket validation remain separate.

## Measurements and history

[Benchmark scope and retained evidence](docs/BENCHMARKS.md) separates memory experiments, kernel experiments and physical network results. There is no published production direct-IP speedup claim.

Earlier stream-framing and compatibility results remain in [git history](https://github.com/Perry3Dnl/DHMP/tree/7b85bd961b12d433ed8fd3ea3b5f623dc47a20e7). They are historical evidence, not the active runtime architecture.

## Documentation

- [DHMP wire contract V1](docs/WIRE_CONTRACT_V1.md)
- [DHMP control plane V1](docs/CONTROL_PLANE_V1.md)
- [DHMP PSK security setup V2](docs/SECURITY_PSK_V2.md)
- [Bounded overload behavior](docs/OVERLOAD_BEHAVIOR.md)
- [Peer receive lifecycle and retirement](docs/PEER_LIFECYCLE.md)
- [Authenticated congestion feedback](docs/CONGESTION_FEEDBACK.md)
- [Test strategy](docs/TEST_STRATEGY.md)
- [Authoritative direct-IP direction](docs/DIRECT_TRANSPORT_DIRECTION.md)
- [Current implementation status](docs/CURRENT_STATUS.md)
- [Stable-base release acceptance plan](docs/STABLE_BASE_RELEASE.md)
- [Protocol design notes](docs/PROTOCOL_DRAFT.md)
- [Architecture and ownership](docs/ARCHITECTURE_COMPARISON.md)
- [.NET API design](docs/DOTNET10_PACKAGE_DESIGN.md)
- [Conformance](docs/CONFORMANCE.md)
- [Licensing](docs/LICENSING_DESIGN.md)
- [Direct-IP implementation plan](docs/TRANSPORT_TUNING_TODO.md)
