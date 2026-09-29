# Active standalone direct-IP architecture

Updated 2026-09-29.

See [the authoritative direction](DIRECT_TRANSPORT_DIRECTION.md) and [DHMP wire contract V1](WIRE_CONTRACT_V1.md).

| Boundary | Active direction | Current implementation |
| --- | --- | --- |
| Packet I/O | Direct IP, IPv6 target | Explicit sender boundary and experimental raw IPv6 harness; production backend pending |
| Protocol identification | Direct IPv6 protocol binding | Experimental Next Header 253 in current research path |
| Session | Explicit versioned agreement before data traffic | `DhmpSessionContract`; remote negotiation pending |
| Data wire format | Headerless batch of whole fixed-size records | Implemented packet-length validation |
| Latest | Bounded newest useful state | Last record within one received packet; cross-packet freshness pending |
| Sequential | Bounded records in receive arrival order | Synchronous packet-batch publication |
| Ownership | Bounded storage and explicit lifetime | Borrowed synchronous spans plus existing ownership building blocks |
| Model boundary | Payload-opaque protocol core | Typed .NET boundary; application schema remains separate |
| Sending | MTU-safe data batch and bounded local rate | `IDhmpPacketSender` plus fixed-window Pmax budget |
| Reliability | No recovery layer | No ACK/retransmission/replay/stream reconstruction |
| Security | Reviewed direct-packet mechanism | Not implemented |

## Headerless data-plane rule

The active V1 packet processor receives only application record bytes.

There is no DHMP packet envelope around those bytes. Version, record size, mode and budget are session state and are therefore not repeated in the hot data path.

Any future protocol-owned wire metadata is a protocol-version change, not a transparent implementation detail.

## Ownership rules

A synchronous borrowed packet needs no per-message ownership transfer. A cross-thread consumer does need real ownership protection.

Latest may replace obsolete state only when storage ownership makes that safe. Sequential cannot overwrite unread published data.

A full bounded queue must follow an explicit drop/reject policy; it does not justify allocating memory indefinitely.

## Reuse of earlier ideas

Batch handoff, reusable regions, contract specialization and compact Latest-state storage remain useful candidates.

Their previous measurements do not establish an integrated direct-IP result. Any retained optimization must be evaluated later against the actual raw-IP backend and ownership model.

The active protocol contains no stream-carry processor. Incomplete records are invalid packets, not fragments waiting for another transport read.
