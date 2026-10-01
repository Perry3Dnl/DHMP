# DHMP current status

Updated 2026-09-30.

Authoritative direction: [direct DHMP over IP](DIRECT_TRANSPORT_DIRECTION.md).
Active data-plane contract: [DHMP wire contract V1](WIRE_CONTRACT_V1.md).

Release objective: [finish the existing feature set into a stable base](STABLE_BASE_RELEASE.md) before performance fine-tuning. The acceptance matrix tracks remaining evidence; implementation status alone is not release qualification.

## Active implementation

- One canonical `DHMP.*` .NET project tree.
- `DhmpProtocol` defines data wire version, control version, experimental data binding 253 and experimental control binding 254.
- `DhmpWireContract` contains only protocol version and fixed record size.
- `DhmpSendPolicy` contains local Pmax, `RejectWindow`/`SmoothPacing` behavior and outbound packet ceiling. `DhmpAdaptiveRateController` can now reduce/recover SmoothPacing from authenticated receiver pressure without exceeding local Pmax.
- `DhmpReceivePolicy` contains local Sequential/Latest mode and inbound packet ceiling.
- `DhmpPacketProcessor` consumes a headerless payload containing complete fixed records only.
- Sequential publishes the complete received batch.
- Latest publishes the final record from the received packet. Optional `DhmpLatestGenerationFilter` drops stale/duplicate Latest state across packets using an application-owned 64-bit generation field.
- The V1 framing core has no carry storage or cross-packet record reassembly. The explicit DAPI/1 application schema may assemble bounded application messages from complete fixed records above that core.
- `DhmpClient` requires a wire contract, local send policy and explicit `IDhmpPacketSender`.
- `DhmpServer` uses the same wire contract with its own local receive policy.
- `DHMP.RawIpv6` provides the first concrete direct-IP .NET backend on Linux: data on experimental protocol 253 and control/handshake on 254. It includes bounded source-IPv6 multi-peer routing plus `DhmpIpv6PathBudget` for known-PMTU payload calculation.
- `DHMP.Security` provides an experimental PSK security profile using authenticated control setup, HKDF-derived directional keys, ChaCha20-Poly1305 packet protection, bounded replay, rolling protected-packet loss telemetry and authenticated RTT probes.
- `DhmpBoundedReceiveDispatcher` provides explicit async ownership/overload behavior: one-slot replacement for Latest and fixed-capacity reject-on-saturation for Sequential, with immutable pressure snapshots for metrics. `DhmpCongestionAdvisor` converts snapshot deltas into bounded receiver pressure recommendations.
- Offline licensing remains separate from peer authentication. The original three-argument ASP.NET registration remains host/license-only. The opt-in one-argument registration now adds the experimental [automatic API application integration](ASP_NET_API_INTEGRATION.md), mapping factory-created client calls and the existing backend pipeline over authenticated raw IPv6.
- Compatibility and PSK one-shot handshakes have a configurable total deadline (ten seconds by default), caller cancellation before socket creation and privilege-free lifecycle/composition tests. PSK setup now uses explicitly versioned [V2 challenge/confirm](SECURITY_PSK_V2.md): fresh responder challenges prevent a captured OFFER/CONFIRM from recreating old keys and resetting counters. No automatic V1 fallback, retransmission or persistent session manager exists. Independent review and operational lifetime gates remain open.
- The architecture guard blocks restoration of legacy transport/stream implementation paths.
- Peer routing has per-registration receive leases, drained `RemoveAsync` and atomic `ReplaceAsync`; retired receive resources remain caller-owned. Single/multi-peer paths share whole-scratch cleanup even on decoder exceptions. See [peer lifecycle](PEER_LIFECYCLE.md); send/control joining and unprotected cross-session freshness remain application/profile concerns.

- Security operations now serialize counter/cipher/replay ownership and join synchronous crypto during disposal. The protected sender has awaitable send retirement; backend/session ownership stays with the caller. See [session shutdown](SESSION_SHUTDOWN.md). A [two-host smoke runner](TWO_HOST_SMOKE.md) is prepared; physical execution remains open.

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
4. Production hardening/metrics around the implemented bounded async receive dispatcher; the unbounded receive-queue gap is closed.
5. Broader loss/duplicate/reordering policy beyond the implemented optional Latest generation filter.
6. Dynamic IPv6 PMTU discovery plus broader network congestion control. Authenticated receiver-overload feedback, rolling secure loss observation, RTT measurement and adaptive pacing are implemented; ECN, RTT-inflation policy and fairness remain open.
7. Independent review/hardening of the implemented PSK security profile, plus a decision on forward-secret/public-key profiles.
8. Kernel, NIC and physical two-host validation.

The current architecture intentionally does not fake these missing layers with a fallback transport.

## Real-socket rehearsal

The hosted raw-IPv6 API lab runs actual sockets in two namespaces/veth interfaces on one VM. It is separate from physical two-host evidence. Initial execution found an unsupported managed protocol-enum socket construction; explicit native Linux descriptor creation now avoids that mapping, with managed safe-handle ownership retained. See [hosted rehearsal](HOSTED_API_REHEARSAL.md).

## Optional echo confirmation

[DECO/1](ECHO_CONFIRMATION_PROFILE_V1.md) is an explicit full-record echo application profile with bounded admission, exact byte/session matching, deadlines and joined retirement. It leaves the default client path and V1 framing unchanged. Confirmation means matching bytes were received, not that application work executed. It is separate from DAPI/1 and does not retry or recover lost data. Physical echo throughput and real-network validation remain open.
