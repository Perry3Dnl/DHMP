# DHMP current status

Updated 2026-09-29. Authoritative direction: [direct DHMP over IP](DIRECT_TRANSPORT_DIRECTION.md).

## Active implementation

- One canonical DHMP.* .NET project tree; the case-only duplicate tree is removed.
- DhmpFixedContract validates record size, packet payload limit and configured Pmax.
- DhmpPacketProcessor publishes complete packets as one borrowed batch, or the last
  record of a packet in Latest mode. It has no carry storage or cross-packet reassembly.
- DhmpClient validates a whole message/batch, reserves budget once and uses an explicit
  IDhmpPacketSender supplied by the caller. The caller retains buffer storage through completion.
- DhmpServer is a packet-processing facade. It has no listener, accept loop or multicast async event.
- Typed model and receive-region ownership helpers remain .NET implementation building blocks.
- Offline licensing and host startup tests remain intact. RuntimeActivated means the license
  startup sequence completed, not that a network endpoint exists.
- CI enforces the direct-IP boundary and tests Linux/Windows builds.

## Removed from the active project

Compatibility transport implementations, their listener/connect APIs, stream reconstruction,
legacy transport benchmarks/workflows, stale charts and contradictory active documentation.
They are preserved in git history, not maintained as fallbacks.

The earlier single-carry optimization is a historical stream-framing result.
The packet path has no carry buffer; it does not inherit that benchmark percentage.

## Remaining work

1. Production direct-IP backend and explicit session/peer metadata.
2. Session contract negotiation and interoperability rules.
3. Loss/duplicate/reordering/freshness policy without delivery recovery.
4. MTU-aware batching, bounded ownership and overload handling.
5. Pacing/congestion policy; the current fixed-window Pmax budget is only a local guard.
6. Reviewed direct-packet security; no secure production mode is currently implemented.
7. Kernel, NIC and physical two-host validation.

The mock-IP experiments remain available. The raw IPv6 experiment is not yet a hardened
backend; its receive/header assumptions and loss accounting require validation.
