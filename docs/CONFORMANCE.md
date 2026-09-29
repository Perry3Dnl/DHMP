# DHMP conformance and compatible designation

The protocol and the reference .NET implementation are distinct. This document concerns
protocol behavior; implementation licensing is defined in [LICENSING_DESIGN.md](LICENSING_DESIGN.md).

The wire/session specification is not frozen. Passing the current prototype tests is not
sufficient to claim interoperable production DHMP conformance or a secure implementation.

## Required packet behavior

- A session has a valid, explicit fixed record size and bounded packet payload size.
- Packets contain whole messages only. Reject empty, incomplete and oversized payloads.
- Never reconstruct a message by joining separate packet payloads.
- Preserve the declared Sequential/Latest publication semantics.
- State the handling of duplicates, loss, reordering and freshness; do not claim guarantees
  that the wire/session implementation does not provide.
- Keep memory and pending work bounded; respect the configured Pmax budget.
- No delivery ACK, replay, retransmission or automatic recovery semantics.
- Keep storage alive until the consumer/send operation releases its ownership.
- Use a reviewed standard security design if advertising security.

## DHMP Compatible label

The intended designation requires a versioned frozen protocol contract and passing the
corresponding independent test vectors. Until those exist, describe the code as an
experimental DHMP implementation. The label is separate from a commercial implementation
license and must not imply ownership transfer of the protocol.

## Current automated coverage

Prototype tests cover record/packet sizes, both publication policies, malformed packets,
no cross-packet carry, borrowed storage, callback failures, batch budget rejection, send
cancellation, explicit sender integration, typed boundaries and startup license gates.
Physical network behavior and interoperability remain additional gates.
