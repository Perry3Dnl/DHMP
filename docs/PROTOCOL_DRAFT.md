# DHMP protocol design notes

Updated 2026-09-29.

The active V1 data-plane contract is defined in [WIRE_CONTRACT_V1.md](WIRE_CONTRACT_V1.md).
The architectural direction is defined in [DIRECT_TRANSPORT_DIRECTION.md](DIRECT_TRANSPORT_DIRECTION.md).

This file tracks protocol work that is deliberately not yet part of the V1 wire contract.

## Frozen for the current V1 build direction

- DHMP runs directly over IP; IPv6 is the current implementation target.
- The data payload is headerless: only complete fixed-size records are present.
- A packet contains one or more whole records.
- Partial record bytes are never carried into another packet.
- Sequential and Latest are receive/publication policies, not reliability modes.
- No ACK, retransmission, replay history or hidden reliable queue is introduced.
- The protocol core treats application record bytes as opaque.
- Wire compatibility is explicit in `.NET` through `DhmpWireContract`; local send/receive behavior is configured separately.

## Session/control plane still to design

The V1 data plane assumes both peers already possess the same wire contract. Endpoint-local policies may differ.

The future control plane must define how peers establish or reject at least:

- protocol version;
- fixed record size;
- publication mode;
- packet payload limit;
- Pmax/pacing policy;
- peer/path association;
- application schema identifier if one is required;
- security profile and key material.

Negotiation must stay outside the hot record-processing path. It must not make the data plane depend on a stream transport.

## Protocol identification

The current raw IPv6 experiments use Next Header `253` for research.

That value is experimental and must remain configurable in backend work. A permanent protocol-number strategy is a separate standards/deployment concern and must not be implied by the prototype constant.

## Freshness and ordering

DHMP V1 deliberately has no protocol-owned sequence number or timestamp in each data packet.

Consequences:

- Sequential can preserve received arrival order, not original send order after network reordering.
- Latest can choose the last record inside one received packet, but cannot prove that a later-arriving packet was generated later.

If protocol-owned cross-packet freshness becomes necessary, it requires an explicit versioned wire-format change. It must not be added invisibly to V1.

An application may include its own generation field inside its fixed record schema without changing DHMP V1.

## MTU and packet sizing

`MaxPacketPayloadBytes` is the maximum DHMP data payload for a session, not the full IP packet size.

The direct-IP backend must account for IPv6 and future security overhead. Production operation should avoid relying on IP fragmentation as a normal mechanism.

Automatic path-MTU discovery/response is not implemented yet.

## Pacing and congestion

The current `DhmpPmaxBudget` is only a fixed local one-second logical-message budget.

It is not:

- smooth pacing;
- congestion control;
- receiver feedback;
- fairness;
- a network-capacity measurement.

A direct-IP deployment needs a bounded pacing/congestion policy before production use on shared networks.

## Ownership and overload

The hot receive callback borrows the supplied span synchronously.

Future packet I/O must use bounded buffer ownership across asynchronous boundaries. Overload policy must be explicit and mode-aware rather than growing an unbounded queue.

Likely direction:

- Latest may replace obsolete queued state;
- Sequential requires a bounded queue or explicit rejection/drop behavior;
- neither mode silently becomes reliable delivery.

## Security

No production secure profile exists yet.

The eventual design must use a reviewed mechanism suitable for direct packets. Security framing, nonce/counter requirements and authentication failure behavior must be versioned explicitly.

Do not solve security by restoring an implicit stream transport underneath DHMP.

## Interoperability target

The protocol definition must remain implementable without the .NET packages.

A conforming Rust, C, C++, Go, kernel or hardware implementation should need the protocol/session specification and application record schema, not DHMP's .NET runtime internals.
