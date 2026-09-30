# DHMP PSK security setup V2

Status: experimental pre-1.0 profile. This explicitly versioned control change replaces the replay-vulnerable V1 security setup in the Linux raw-IPv6 handshake APIs. Independent security review is still required.

## Scope and compatibility

Base DHMP data wire V1 remains headerless. The separate protected data envelope remains an 8-byte counter, encrypted V1 payload and 16-byte ChaCha20-Poly1305 tag (24 bytes overhead). Compatibility Control V1, congestion feedback and path-probe encodings do not change.

Only security setup changes: `DHMS` version 2 uses 64-byte packets and four messages. The raw-IPv6 APIs accept only setup V2. Both peers must upgrade together; there is no automatic V1 fallback. The legacy V1 codec remains for historical/conformance tests and is not a safe session-setup alternative.

## Problem addressed

V1 authenticated an initiator-provided session ID but supplied no responder freshness challenge. Replaying a captured OFFER could recreate the same directional keys and reset counters, allowing replay acceptance and potentially AEAD nonce reuse.

V2 requires a fresh responder nonce and a transcript-bound CONFIRM before the responder returns a security session. Replaying an old OFFER elicits a new challenge; its captured CONFIRM does not match that challenge. This uses fresh randomness on every responder attempt, without depending on a replay cache surviving process restarts. Correctness depends on the operating system's cryptographic random generator; restoring RNG state or duplicating session objects is outside this guarantee.

## Fixed security-control packet

Every message is exactly 64 bytes:

| Offset | Bytes | Field |
| ---: | ---: | --- |
| 0 | 4 | ASCII `DHMS` |
| 4 | 1 | Security setup version `2` |
| 5 | 1 | OFFER `1`, CHALLENGE `2`, CONFIRM `3`, ACCEPT `4` |
| 6 | 1 | PSK ChaCha20-Poly1305/HKDF-SHA256 suite `1` |
| 7 | 1 | Reserved, must be zero |
| 8 | 16 | Initiator nonce UUID, network/big-endian Guid representation |
| 24 | 16 | Responder nonce UUID, same representation |
| 40 | 4 | Non-zero public PSK key ID, unsigned big-endian |
| 44 | 4 | Non-zero correlation ID, unsigned big-endian |
| 48 | 16 | First 16 bytes of HMAC-SHA256 over bytes 0..47 with the PSK |

The initiator nonce is non-empty on all messages. OFFER must have an empty responder nonce; all other types require a non-empty responder nonce. The key ID must match the configured PSK. Unknown versions, suites, types, reserved values, invalid nonce layouts, lengths and invalid tags fail closed.

Message type is covered by the HMAC. Reflecting CHALLENGE or ACCEPT as CONFIRM fails the state/type check even when the tag is valid.

## Exchange and activation

1. Initiator creates a fresh `Guid.NewGuid()` nonce and non-zero correlation ID; sends authenticated OFFER.
2. Responder validates OFFER and creates its own fresh `Guid.NewGuid()` nonce; sends authenticated CHALLENGE containing both nonces and the offered identifiers. No security session is returned yet.
3. Initiator verifies type, offered nonce, key ID and correlation; sends authenticated CONFIRM with the complete unchanged challenge transcript.
4. Responder verifies CONFIRM type and exact transcript match; sends authenticated ACCEPT. It returns a responder session only after successful local send completion and a cancellation check.
5. Initiator verifies ACCEPT type and exact confirmed transcript match; only then returns an initiator session.

Every step shares one configured `HandshakeTimeout` (ten seconds by default). Explicit cancellation remains cancellation; deadline expiry raises `TimeoutException`. All channels are disposed on every exit. Invalid messages, unexpected states and transcript mismatches terminate the attempt. No retransmission, implicit session resume or setup downgrade is provided.

Only one setup exchange per configured peer/control channel should be active at a time. This one-shot API does not multiplex concurrent handshakes or maintain established peer session lifetimes.

## Session identity and directional keys

The canonical transcript is the 48-byte CHALLENGE body, regardless of whether session identity is derived from CHALLENGE, CONFIRM or ACCEPT. It covers version, suite, both nonces, key ID and correlation ID.

Compute `SHA256(ASCII("DHMP-PSK-SETUP-V2-ID") || canonical_challenge_body)`. The first 16 digest bytes, interpreted as a network/big-endian Guid, become the final session ID. An all-zero result is rejected and requires a fresh attempt. OFFER cannot derive a session ID.

The existing data-profile HKDF and AEAD rules use this derived ID as the session salt/context. The directional contexts remain `DHMP-S1-I2R`, `DHMP-S1-R2I` and their existing feedback/path variants. This deliberately reuses the existing protection profile with a new domain-separated, two-party setup transcript. Data counter/replay-window semantics remain unchanged.

Both sending directions start at counter 1 only for their newly derived keys. Recreating a disposed session with its old final ID remains forbidden. The low-level public session constructor requires the caller to enforce this lifetime rule; use the V2 handshake to establish fresh peer sessions.

## Conformance vector

Test-only PSK: 32 zero bytes; key ID `7`; correlation `42`; initiator nonce `00112233-4455-6677-8899-aabbccddeeff`; responder nonce `ffeeddcc-bbaa-9988-7766-554433221100`.

CHALLENGE packet hex:

```text
44484d530202010000112233445566778899aabbccddeeffffeeddccbbaa99887766554433221100000000070000002ad3706e9b78b650c4319b635047caeb7b
```

Derived final session ID: `b20c9b03-7c97-2851-6f58-7bad66f082a0`.

The vector is independently calculated with standard SHA256/HMAC and retained in codec/session-identity tests. These zero key bytes are not a production key.

## Limits and remaining release gates

- No forward secrecy. PSK compromise still compromises recorded sessions.
- No claim of independently reviewed production security. Review must include reflection, downgrade, transcript binding, key derivation, randomness and packet-counter lifetimes.
- Invalid/spoofed control traffic can abort attempts, and replayed OFFERs can consume bounded handshake resources. Denial-of-service/flood management remains open; freshness is not a DoS defense.
- ACCEPT loss can leave the responder active while the initiator times out. The caller owns retirement/replacement of that half-established session. Do not infer remote activation from local send completion or resume old counters/IDs on retry.
- The unauthenticated compatibility exchange is not bound into this PSK transcript. Application schema/compatibility authentication and shared-key deployment identity need separate review.
- Production key rotation, multi-key selection, secure operational lifecycle and physical two-host validation remain open.

The implementation closes the concrete captured-OFFER/CONFIRM session-recreation path; it does not mark every security or stable-base release gate complete.
