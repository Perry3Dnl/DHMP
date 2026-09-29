# Authoritative direction: DHMP directly over IP

Decision updated 2026-09-29. This document and AGENTS.md supersede the earlier
compatibility/byte-stream architecture.

## Decision

DHMP is its own packet layer directly over IP, with IPv6 as the current research target.
TCP, UDP, HTTP, QUIC, WebSocket, gRPC and TLS-stream adapters are not active project
paths or fallbacks. Their earlier code and measurements remain only in git history.

The project manages its fixed contract, packet batching, bounded buffers, ownership,
Latest/Sequential publication and send budget. IP, kernel packet I/O, drivers and NICs
remain distinct lower layers; this project does not claim to have replaced those layers.

## Required boundaries

1. A direct-IP backend delivers a complete packet payload and its session/peer context.
2. The fixed-contract processor rejects invalid lengths before publishing any records.
3. A valid packet contains an integer number of whole messages within its configured MTU budget.
4. The processor publishes a borrowed batch; consumers finish before returning or acquire
   explicit ownership elsewhere before asynchronous use.
5. Application processing stays outside the payload-opaque protocol core.

No partial-message carry exists between IP packets. No retransmission, ACK, replay or
implicit reliable queue is introduced.

## Implemented versus pending

The repository implements the packet-processing boundary and client/server facades.
The client requires an explicitly supplied IDhmpPacketSender; there is no default network
backend. The server facade accepts already-delivered packets; it is not a listening service.
The mock-IP experiments and raw IPv6 kernel harness are research tools.

Production packet I/O, session discovery/negotiation, peer validation, sequence/freshness
policy, real path-MTU handling, congestion behavior and a reviewed secure packet profile
remain work items. Removing legacy transports is not evidence that these are complete.

Latest currently chooses the final record inside one received packet. It must not be
described as newest-by-generation across reordered packets until the wire/session policy
for that is defined. Sequential currently preserves arrival order, not original sender order.

## Work order

1. Keep the active packet core small, validated and allocation-free for borrowed publication.
2. Define the direct-IP session and bounded packet-buffer ownership interfaces.
3. Implement the direct IPv6 backend and explicitly validate OS receive behavior.
4. Validate malformed packets, overload, loss, duplicates, reordering, shutdown and buffer reuse.
5. Measure kernel costs; then driver/NIC behavior and two physical endpoints.
6. Specify and evaluate a standard reviewed security mechanism for the direct packet path.

Existing mock-IP contracts remain frozen. Header-template and other optimizations get a
separate A/B harness; previous evidence is not silently rewritten.

## Measurement scope

Memory framing, kernel loopback, NIC/link traffic and two-host application payload
throughput are different measurements. No one is a substitute for another.
The architectural direction is chosen; superior physical network performance is not yet proven.
