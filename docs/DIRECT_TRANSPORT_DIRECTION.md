# Authoritative direction: DHMP directly over IP

Decision updated 2026-09-29.

This document, [WIRE_CONTRACT_V1.md](WIRE_CONTRACT_V1.md) and `AGENTS.md` supersede the older compatibility/byte-stream architecture.

## Decision

DHMP is its own packet protocol directly over IP, with IPv6 as the current implementation target.

The active project does not use TCP, UDP, HTTP, QUIC, WebSocket, gRPC or TLS-stream transports as DHMP data-plane implementations or fallbacks.

Historical code and measurements remain in git history only.

## V1 data-plane decision

The V1 DHMP data payload is headerless.

A received DHMP payload contains only one or more complete fixed-size application records. DHMP adds no packet header, per-record header, separator or trailer in V1.

The wire contract supplies only the protocol-owned information needed to interpret payload bytes:

- protocol version;
- record size.

Pmax, Sequential/Latest mode and packet ceilings are endpoint-local policies. They are intentionally not part of V1 wire identity.

The current IPv6 experimental profile uses protocol / Next Header `253` for data and `254` for the separate control plane. Neither is a permanent protocol assignment.

## Required boundaries

1. A direct-IP backend identifies the peer/path/session and delivers one complete DHMP payload.
2. The wire contract is established before data-plane processing; local send/receive policy remains endpoint-owned.
3. The fixed-contract processor validates the complete received payload before publication.
4. A valid payload contains an integer number of whole records within its session packet budget.
5. No record spans two IP packets.
6. The processor publishes borrowed data synchronously unless ownership is explicitly transferred elsewhere.
7. Application schema/model processing remains outside the payload-opaque protocol core.

No partial-message carry, ACK, retransmission, replay history or implicit reliable queue is introduced.

## Implemented versus pending

Implemented:

- headerless fixed-record packet validation;
- Sequential and packet-local Latest publication;
- explicit `DhmpWireContract`, `DhmpSendPolicy` and `DhmpReceivePolicy`;
- explicit outbound `IDhmpPacketSender` boundary;
- client/server protocol facades;
- offline licensing/host startup integration;
- raw/mock IP research harnesses;
- fixed 32-byte Control V1 codec and HELLO/ACCEPT/REJECT compatibility negotiation;
- Linux raw-IPv6 control channel for one configured peer;
- architecture checks preventing legacy transport restoration.

Pending:

- production hardening of direct-IPv6 packet I/O;
- peer discovery beyond an explicitly configured peer;
- peer authentication and secure negotiation;
- bounded asynchronous packet-buffer ownership;
- path-MTU behavior;
- cross-packet freshness/reordering policy;
- pacing/congestion behavior;
- reviewed production security.

Removing the legacy transports is not evidence that these pending pieces already exist.

## Work order

1. Harden the direct-IPv6 data/control backend around the V1 contracts.
2. Add peer discovery/authentication beyond the implemented configured-peer compatibility handshake.
3. Add bounded packet-buffer ownership for asynchronous I/O.
4. Define overload, loss, duplicate and reordering behavior.
5. Add MTU/path handling and pacing/congestion policy.
6. Specify and evaluate a reviewed direct-packet security profile.
7. Validate kernel, NIC and two-physical-host behavior.
8. Optimize only after the actual direct-IP path is measurable.

Existing benchmark evidence remains historically valid only for the scope it measured. It must not be relabeled as production direct-IP performance.
