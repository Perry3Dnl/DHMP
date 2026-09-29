# DHMP current status

Updated 2026-09-29.

Authoritative direction: [direct DHMP over IP](DIRECT_TRANSPORT_DIRECTION.md).
Active data-plane contract: [DHMP wire contract V1](WIRE_CONTRACT_V1.md).

## Active implementation

- One canonical `DHMP.*` .NET project tree.
- `DhmpProtocol` defines data wire version, control version, experimental data binding 253 and experimental control binding 254.
- `DhmpWireContract` contains only protocol version and fixed record size.
- `DhmpSendPolicy` contains local Pmax and outbound packet ceiling.
- `DhmpReceivePolicy` contains local Sequential/Latest mode and inbound packet ceiling.
- `DhmpPacketProcessor` consumes a headerless payload containing complete fixed records only.
- Sequential publishes the complete received batch.
- Latest publishes the final record from the received packet.
- No carry storage or cross-packet message reassembly exists.
- `DhmpClient` requires a wire contract, local send policy and explicit `IDhmpPacketSender`.
- `DhmpServer` uses the same wire contract with its own local receive policy.
- `DHMP.RawIpv6` provides the first concrete direct-IP .NET backend on Linux: data on experimental protocol 253 and control/handshake on 254 for one configured peer.
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
3. Peer discovery, authentication and a multiplexing strategy beyond the current one-peer/one-contract binding. Compatibility/capability negotiation for a configured peer is implemented.
4. Bounded asynchronous packet-buffer ownership beyond the first receive loop.
5. Loss/duplicate/reordering/freshness policy beyond V1 packet-local semantics.
6. MTU/path-MTU handling and pacing/congestion policy.
7. Reviewed direct-packet security profile.
8. Kernel, NIC and physical two-host validation.

The current architecture intentionally does not fake these missing layers with a fallback transport.
