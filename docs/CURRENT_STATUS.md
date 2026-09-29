# DHMP current status

Updated 2026-09-29.

Authoritative direction: [direct DHMP over IP](DIRECT_TRANSPORT_DIRECTION.md).
Active data-plane contract: [DHMP wire contract V1](WIRE_CONTRACT_V1.md).

## Active implementation

- One canonical `DHMP.*` .NET project tree.
- `DhmpProtocol` defines data wire version, control version, experimental data binding 253 and experimental control binding 254.
- `DhmpWireContract` contains only protocol version and fixed record size.
- `DhmpSendPolicy` contains local Pmax, `RejectWindow`/`SmoothPacing` behavior and outbound packet ceiling.
- `DhmpReceivePolicy` contains local Sequential/Latest mode and inbound packet ceiling.
- `DhmpPacketProcessor` consumes a headerless payload containing complete fixed records only.
- Sequential publishes the complete received batch.
- Latest publishes the final record from the received packet. Optional `DhmpLatestGenerationFilter` drops stale/duplicate Latest state across packets using an application-owned 64-bit generation field.
- No carry storage or cross-packet message reassembly exists.
- `DhmpClient` requires a wire contract, local send policy and explicit `IDhmpPacketSender`.
- `DhmpServer` uses the same wire contract with its own local receive policy.
- `DHMP.RawIpv6` provides the first concrete direct-IP .NET backend on Linux: data on experimental protocol 253 and control/handshake on 254. It includes bounded source-IPv6 multi-peer routing plus `DhmpIpv6PathBudget` for known-PMTU payload calculation.
- `DHMP.Security` provides an experimental PSK security profile using authenticated control setup, HKDF-derived directional keys, ChaCha20-Poly1305 packet protection and a bounded replay window.
- Offline licensing and ASP.NET startup integration remain separate from network transport.
- The architecture guard blocks restoration of legacy transport/stream implementation paths.

## V1 data plane

The V1 DHMP packet payload contains application records only.

There are no DHMP packet header bytes, per-record headers, separators or trailers.

Record boundaries are recovered from:

1. the session's fixed record size; and
2. the received IP payload length.

The current raw IPv6 research work uses experimental protocol / Next Header `253` for data and `254` for control. Neither is a permanent protocol assignment.

## Removed from the active project

Compatibility transport implementations, listener/connect APIs, stream reconstruction, legacy transport benchmark workflows, stale charts and contradictory active documentation are not part of the runtime project.

Historical versions remain available through git history.

## Remaining work

1. Harden the Linux raw-IPv6 backend and validate privileged two-host operation.
2. Add separately validated backend behavior for other operating systems rather than assuming raw-socket portability.
3. Peer discovery and multiplexing beyond the implemented one-session-per-source-IPv6 routing model. Compatibility negotiation and PSK authentication are implemented for explicitly configured peers.
4. Bounded asynchronous packet-buffer ownership beyond the first receive loop.
5. Broader loss/duplicate/reordering policy beyond the implemented optional Latest generation filter.
6. Dynamic IPv6 PMTU discovery/feedback and real congestion/backpressure policy. Known-PMTU budgeting and `MessageSize` surfacing are implemented.
7. Independent review/hardening of the implemented PSK security profile, plus a decision on forward-secret/public-key profiles.
8. Kernel, NIC and physical two-host validation.

The current architecture intentionally does not fake these missing layers with a fallback transport.
