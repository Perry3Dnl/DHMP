# DHMP PSK security profile V1

Status: experimental pre-1.0 profile. The cryptographic primitives are standard, but this DHMP composition has not received an independent security review and must not yet be advertised as production-secure.

Historical setup contract: the raw-IPv6 handshake APIs now use [security setup V2](SECURITY_PSK_V2.md). V1 OFFER/ACCEPT setup is replay-vulnerable and must not be used to establish new sessions. The data-protection primitive/envelope described below is reused by V2 with a derived two-party session ID.

Base DHMP V1 remains headerless. This profile adds an explicit lower packet-protection envelope before raw IPv6 transmission and removes it before the normal V1 packet processor sees the payload.

## Primitive set

The .NET reference implementation uses:

- 256-bit pre-shared key (PSK);
- HMAC-SHA256 for authenticated security-control messages;
- HKDF-SHA256 for directional session-key and nonce-prefix derivation;
- ChaCha20-Poly1305 for authenticated encryption of data packets.

The current profile does not provide forward secrecy. Compromise of the PSK can compromise recorded sessions whose handshake/session identifiers are available.

## Security control setup

Security setup uses the existing experimental control IPv6 protocol / Next Header `254`, but it is distinct from the 32-byte compatibility Control V1 packet.

The security-control packet is exactly 48 bytes:

```text
Offset  Size  Field
0       4     Magic = ASCII "DHMS"
4       1     Security-control version = 1
5       1     OFFER / ACCEPT / REJECT
6       1     Security suite
7       1     Reject reason
8       16    Fresh session identifier
24      4     Public PSK key ID, unsigned big-endian
28      4     Correlation ID, unsigned big-endian
32      16    First 16 bytes of HMAC-SHA256 over bytes 0..31
```

The HMAC is computed with the configured PSK.

A valid HMAC proves that the message was created by a holder of the configured PSK. A matching authenticated ACCEPT is bound to the initiator's fresh outstanding session offer. An OFFER alone does not establish freshness or live possession by the current sender, because a captured OFFER can be replayed. The operational identity attached to the shared secret is a deployment responsibility.

The session identifier must be fresh for each security session. The .NET raw-IPv6 initiator currently creates it with `Guid.NewGuid()`.

The .NET one-shot initiator and responder use the configured `DhmpRawIpv6Options.HandshakeTimeout` (ten seconds by default) for the entire exchange. Timeout raises `TimeoutException`; explicit caller cancellation remains cancellation. No timeout, authentication failure, unexpected response or send failure returns an active security session. The channel is disposed on all exits. A subsequent initiator call creates a new session identifier rather than resending the previous OFFER.

This deadline does not add handshake replay protection. The responder currently has no persistent freshness check for OFFER session identifiers, and a captured authenticated OFFER must not be assumed fresh merely because its HMAC verifies. Recreating a session with the same PSK/session ID would reset data counters under identical derived keys. Responder freshness/confirmation across retries and process restarts is an explicit security release blocker requiring a reviewed design; fresh locally generated initiator IDs alone do not close it. A lost ACCEPT can leave the responder with a session the initiator did not activate. Applications must not automatically recreate or resume a timed-out session with its old ID.

## Directional key derivation

Both peers derive separate material for:

- initiator -> responder;
- responder -> initiator.

HKDF-SHA256 uses:

- the PSK as input key material;
- the 16-byte session identifier as salt;
- direction-specific context strings.

Each direction derives:

- a 32-byte ChaCha20-Poly1305 key;
- a 4-byte nonce prefix.

Separate directional keys prevent the two directions from sharing the same AEAD key/nonce space.

## Protected data packet

A protected packet on the data binding `253` is:

```text
+----------------+----------------------+----------------+
| counter (8 B)  | encrypted V1 payload | AEAD tag (16 B)|
+----------------+----------------------+----------------+
```

Security overhead is therefore 24 bytes per data packet.

The 96-bit ChaCha20-Poly1305 nonce is not sent in full. It is constructed as:

```text
derived nonce prefix (4 bytes) || packet counter (8 bytes)
```

The 16-byte session identifier is authenticated as AEAD associated data.

The plaintext inside the envelope remains the normal DHMP V1 payload:

```text
[record][record][record]...
```

After successful decryption/authentication, the envelope is removed before `DhmpPacketProcessor` sees the bytes.

## Counter and replay rules

Each sending direction starts its 64-bit counter at 1 and increments once per protected packet.

Counter reuse under the same derived directional key is forbidden. Counter exhaustion requires a new security session/rekey.

The receiver keeps a bounded 64-packet replay window:

- duplicate authenticated counters are rejected;
- packets older than the window are rejected;
- limited network reordering inside the window is accepted;
- replay state advances only after AEAD authentication succeeds.

This prevents unauthenticated packets from advancing replay state.

## Payload ceiling

Because protection adds 24 bytes, the plaintext DHMP ceiling is smaller than the underlying raw-IP sender ceiling.

Example:

```text
raw backend payload ceiling = 1408
security overhead           =   24
maximum protected sender plaintext <= 1384
```

The final value must still be rounded down to a whole number of DHMP records.

`DhmpProtectedPacketSender.MaximumPayloadBytes` exposes the reduced ceiling. `DhmpNegotiatedPeer.ConstrainToPayloadLimit(...)` can clamp a negotiated send policy to that final sender limit.

## Memory behavior

The sender uses pooled protected-packet storage and clears it before returning it to the pool.

The raw IPv6 receiver decrypts into caller-owned reusable storage. That plaintext storage is cleared after the synchronous DHMP/application publication returns, including exceptional exits.

## Authenticated congestion feedback

The same security session derives separate directional HMAC keys for ongoing congestion feedback.

Feedback packets use magic `DHMF`, carry a directional sequence number plus bounded receive-pressure evidence, and are authenticated with a 16-byte truncated HMAC-SHA256 tag. Authenticated `DHMR` path probes use separately derived directional HMAC keys and echo an opaque monotonic timestamp plus rolling secure receive-counter telemetry.

The feedback replay window is separate from the encrypted-data replay window.

This allows receiver-driven pacing feedback without accepting unauthenticated remote throttle instructions.

See [authenticated congestion feedback](CONGESTION_FEEDBACK.md).

## Current limitations

This profile still needs:

- independent security review;
- PSK rotation/lifecycle tooling;
- secure multi-key lookup rather than one explicitly configured PSK;
- denial-of-service analysis;
- physical two-host validation;
- performance benchmarking of protected raw-IP packets;
- a decision on whether a future public-key/forward-secret profile is required.

Do not describe the current profile as providing forward secrecy or as having completed a production security review.
