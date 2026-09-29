# DHMP direct-IP packet draft

Updated 2026-09-29. Working design, not a frozen interoperability specification.
[The direct-IP decision](DIRECT_TRANSPORT_DIRECTION.md) is authoritative.

## Packet contract

A preconfigured session currently supplies a positive message size, a positive Pmax and
a maximum packet payload size. Every incoming IP payload must be nonempty, within that
limit and an exact multiple of the message size. Validate the entire length before publishing.

The payload is a contiguous batch of fixed-layout messages. No per-message length field
is required once the contract is known. An application message must fit in one packet.
Invalid or truncated packets are rejected in full; no bytes are saved for the next packet.

The default prototype payload limit, 1408 bytes, is a lab setting for 44 records of 32 bytes,
not a universally valid path-MTU limit. The backend must account for all IP and security
overhead and must reject any packet that would exceed the actual configured path limit.

## Session metadata is not finished

Session identity, versioning, negotiation, peer association, record schema and byte order,
freshness metadata and the final secure wire format are not yet frozen.
Current tests supply a matching contract directly at both ends; they do not implement a handshake.
Do not infer that the final packet needs no session/security metadata merely because record
length is fixed. Headerless refers to avoiding repeated per-message metadata where possible.

## Publication semantics

Sequential publishes all complete records received in the packet, in arrival order. It
does not guarantee delivery, uniqueness or sender order across the network.

Latest publishes the packet's final complete record. Global newest-state selection across
packet reordering requires session/freshness rules that are still to be defined. Keep the
bounded Ring-3 newest-state handoff as a candidate, not a claim that it is already integrated.

## Sending and overload

The sender validates a whole message or batch before forwarding it to an explicit direct-IP
backend. Pmax counts logical messages, not packets. A batch consumes its whole budget or
is rejected; rejection does not create a queue. The current fixed one-second window is a
prototype local budget, not smooth pacing or network congestion control. Production work
must address those concerns without adding delivery recovery.

No per-message ACKs, retransmission, replay history or application-message reassembly.
Packet loss, duplicates and reordering must be handled according to the selected application
semantics; they must not silently turn into a reliable transport.

## Memory and failure contract

Callbacks borrow spans only for the duration of the call. Async consumers must obtain
explicit ownership or copy at that boundary. A send buffer remains valid until SendPacketAsync
completes; completion indicates release of local buffer use, not remote delivery.
Exceptions propagate and there is no implicit retry. A failed send retains its reserved budget.

## Security

No production secure profile is implemented. Select a standard, reviewed mechanism suitable
for the direct packet path. Do not invent cryptography, advertise an unimplemented secure mode,
or reintroduce a stream transport as an implicit security fallback.
