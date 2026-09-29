# DHMP conformance and compatible designation

The protocol and the reference .NET implementation are distinct. Implementation licensing is defined in [LICENSING_DESIGN.md](LICENSING_DESIGN.md).

The current working protocol contract is [WIRE_CONTRACT_V1.md](WIRE_CONTRACT_V1.md). It is a pre-1.0 contract and may still change before a stable interoperability release.

Passing repository tests alone is not sufficient to claim production interoperability or security.

## Required V1 packet behavior

A conforming V1 data-plane implementation must:

- use the agreed session protocol version;
- use the agreed fixed record size and bounded packet payload size;
- treat the DHMP data payload as headerless application-record bytes;
- add no hidden DHMP packet header, record header, separator or trailer;
- accept only payloads containing one or more complete records;
- reject empty, incomplete and oversized payloads;
- never reconstruct a record by joining separate IP packets;
- preserve the declared Sequential/Latest publication semantics;
- avoid claiming delivery, uniqueness, sender ordering or cross-packet freshness that V1 does not provide;
- keep memory and pending work bounded;
- respect the configured local Pmax budget where that contract applies;
- add no delivery ACK, replay, retransmission or automatic recovery semantics;
- keep storage alive until the local consumer/send operation releases ownership;
- use a reviewed security design before advertising a secure DHMP mode.

For the current raw IPv6 research path, Next Header `253` is an experimental binding only.

## Application-schema compatibility

DHMP V1 treats record bytes as opaque.

Two implementations can satisfy the DHMP packet contract and still fail to understand each other if they use different application record layouts.

A real interoperability profile therefore needs both:

1. a matching DHMP session contract; and
2. a matching application schema or schema identifier outside the current hot data packet.

## DHMP Compatible label

The intended designation should only be used once a stable versioned protocol release and independent conformance vectors exist.

Until that release, describe implementations as experimental or pre-1.0 DHMP implementations.

The compatibility designation is separate from any commercial implementation license and must not imply ownership of the open protocol definition.

## Current automated coverage

The repository contains prototype coverage for packet/record sizes, Sequential/Latest behavior, malformed packets, no cross-packet carry, borrowed storage, callback failures, send-budget rejection, cancellation, sender integration, typed boundaries and startup license gates.

Production raw-IP behavior, independent cross-language interoperability, security and physical-network validation remain additional gates.
