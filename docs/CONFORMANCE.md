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
- correctly interpret the declared V1 wire version and fixed record size;
- avoid claiming delivery, uniqueness or sender ordering that V1 does not provide; any cross-packet freshness claim must identify the application-owned generation/profile used;
- keep memory and pending work bounded;
- keep any local send budget and receive publication policy separate from wire compatibility;
- add no delivery ACK, replay, retransmission or automatic recovery semantics;
- keep storage alive until the local consumer/send operation releases ownership;
- keep base V1 security-free unless an explicit security profile is selected;
- do not advertise the current PSK profile as independently reviewed or forward-secret.

For the current raw IPv6 experimental profile, protocol / Next Header 253 carries data and 254 carries control. Neither is a permanent assignment.

## Control-plane behavior

A conforming implementation of the current Control V1 profile must:

- use the fixed 32-byte control format;
- keep control metadata off the V1 data payload;
- preserve HELLO correlation IDs in ACCEPT/REJECT responses;
- reject incompatible wire version, record size or schema UUID;
- advertise a receive capability that can hold at least one complete record;
- not describe successful compatibility negotiation as authentication.

Pmax and Sequential/Latest are local policy and must not be treated as wire compatibility requirements.

## Experimental PSK security profile

An implementation claiming compatibility with the current PSK profile must:

- authenticate the security-control body with the specified truncated HMAC-SHA256 tag;
- derive distinct directional key/nonce-prefix material with HKDF-SHA256;
- use a unique non-zero 64-bit packet counter per direction/session;
- use ChaCha20-Poly1305 with the specified session-associated data;
- reject authentication failures;
- reject duplicate/too-old authenticated counters using a bounded replay policy compatible with the profile;
- account for the 24-byte protected-data overhead;
- never claim forward secrecy from this PSK-only profile.

Independent security review remains a release gate.

## Application-schema compatibility

DHMP V1 treats record bytes as opaque.

Two implementations can satisfy the DHMP packet contract and still fail to understand each other if they use different application record layouts.

A real interoperability profile therefore needs both:

1. a matching DHMP wire contract; and
2. a matching application schema or schema identifier outside the current hot data packet.

## DHMP Compatible label

The intended designation should only be used once a stable versioned protocol release and independent conformance vectors exist.

Until that release, describe implementations as experimental or pre-1.0 DHMP implementations.

The compatibility designation is separate from any commercial implementation license and must not imply ownership of the open protocol definition.

## Current automated coverage

The repository contains prototype coverage for packet/record sizes, Sequential/Latest behavior, schema-owned cross-packet generation filtering including wraparound, malformed packets, no cross-packet carry, borrowed storage, callback failures, reject-window and smooth-pacing behavior, cancellation, sender integration, control codec/negotiation behavior, typed boundaries and startup license gates.

Production raw-IP behavior, independent cross-language interoperability, security and physical-network validation remain additional gates.
