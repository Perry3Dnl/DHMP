# Active direct-IP architecture

Updated 2026-09-29. This replaces the earlier compatibility-oriented architecture summary.
See [the authoritative decision](DIRECT_TRANSPORT_DIRECTION.md).

| Boundary | Active direction | Current implementation |
| --- | --- | --- |
| Packet I/O | Direct IP, IPv6 research target | Sender interface and experimental kernel harness; production backend pending |
| Session | Negotiate once, keep steady-state work small | Explicit matching contract supplied by caller; negotiation pending |
| Framing | Whole fixed-size records within one IP payload | Packet-length validation and one borrowed publication |
| Latest | Bounded newest useful state | Last record within a packet; cross-packet freshness pending |
| Sequential | Bounded batches in receive order | Synchronous per-packet publication; queue integration pending |
| Ownership | Preallocated receive storage and explicit leases | Receive-region/pool building blocks; backend wiring pending |
| Model boundary | Validated native-layout view | Typed cast; not endian conversion or general deserialization |
| Sending | MTU-safe batch and bounded rate budget | Packet sender boundary and fixed-window budget |
| Security | Standard reviewed direct-packet mechanism | Not implemented |

## Ownership rules

A synchronous borrowed packet needs no per-message ownership transfer. A cross-thread
consumer does need real ownership protection. Do not remove atomics from shared receive
regions merely because a synchronous packet processor itself needs no borrowing flags.

Latest can overwrite only state that is not owned by a reader. Sequential cannot overwrite
unread published data. A full bounded queue must follow an explicit drop/reject policy; it
does not justify allocating more memory indefinitely.

## Reuse of earlier ideas

Batch handoff, reusable regions, contract specialization and a compact Ring-3 Latest buffer
remain useful candidates. Their previous measurements do not establish an integrated direct-IP
result. Any retained technique must be tested in the new packet path with its actual ownership
and publication semantics.

The active code contains no stream-carry processor. Whole IP packets already supply framing
boundaries; incomplete records are errors, not fragments waiting for another packet.
