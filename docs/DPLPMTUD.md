# Authenticated DHMP Datagram PLPMTUD search

Status: experimental R&D profile.

DHMP's data plane remains headerless. Path-MTU discovery is carried only on the authenticated control binding and adds **zero bytes** to normal V1 data records.

The design follows the core Datagram PLPMTUD requirements in RFC 8899:

- manage a current PLPMTU rather than sending ordinary packets above it;
- send explicitly larger probe packets without IPv6 source fragmentation;
- confirm successful end-to-end probe reception;
- retry failed probes before concluding that a size is unsupported;
- confirm the base PLPMTU before searching upward;
- search efficiently rather than probing every possible byte value;
- provide a way to re-confirm a selected PLPMTU for black-hole detection.

References:

- RFC 8899 — Datagram Packetization Layer Path MTU Discovery: https://www.rfc-editor.org/rfc/rfc8899.html
- RFC 8201 — Path MTU Discovery for IPv6: https://www.rfc-editor.org/rfc/rfc8201.html

## Why the existing authenticated control channel is used

DHMP V1 data packets intentionally contain no protocol-owned discriminator. Injecting an MTU-probe marker into protocol 253 would risk publishing a probe as application data or would require changing the headerless wire contract.

The existing long-lived PSK-authenticated protocol-254 control channel already demultiplexes receiver-pressure feedback and RTT/loss telemetry. MTU probes are therefore another authenticated control family on that same socket.

This has an important limitation: a successful protocol-254 MTU probe proves that the IPv6 path can carry a packet of that size on the control binding. It does **not** independently prove that a middlebox will permit protocol 253 data traffic. Reachability and MTU are separate deployment questions.

## Probe packet

An MTU probe request:

1. carries session-bound authenticated metadata;
2. carries a monotonically increasing probe identifier;
3. declares the exact raw IPv6 upper-layer payload size being tested;
4. is zero-padded to that exact size;
5. authenticates the entire padded packet with the directional session path key.

The peer returns a compact authenticated response containing the same probe ID and tested size.

Because the HMAC covers the padding, an acknowledgment confirms receipt of the complete padded probe, not merely its fixed metadata prefix.

## Search

`DhmpRawIpv6CongestionChannel.DiscoverPathMtuAsync(...)` requires the channel's `RunAdaptiveReceiveLoopAsync(...)` to be active on both peers.

The search:

1. confirms IPv6 base PLPMTU 1280;
2. probes the configured maximum directly;
3. if the maximum fails, searches between the largest confirmed size and the failed upper bound;
4. retries a candidate up to `MaximumProbeAttempts`;
5. stops when the remaining possible gain is smaller than `MinimumSearchGainBytes`.

The default probe timeout is 20 seconds. RFC 8899 requires a probe timer of at least one second and recommends a value greater than 15 seconds. Tests may use the one-second minimum.

If the base probe cannot be confirmed, the result enters `Error`. The returned 1280-byte budget is then only a conservative fallback; it is explicitly marked unconfirmed.

## Re-confirmation and black-hole handling

`ConfirmPathMtuAsync(...)` re-probes a selected PLPMTU using the same authenticated mechanism and retry policy.

This supplies the confirmation primitive required for black-hole detection. Automatic periodic maintenance and automatic live mutation of an already-running client's send policy are intentionally separate work. A future controller can use this confirmation result to:

- fall back immediately to the base 1280-byte path budget after repeated failure;
- rebuild/reconfigure the effective send policy;
- re-enter the upward search after a raise interval.

The current implementation does not silently alter application packet sizing behind the caller's back.

## ICMPv6 Packet Too Big

Validated ICMPv6 Packet Too Big information can accelerate DPLPMTUD, but RFC 8899 does not permit relying solely on PTB messages.

The current DHMP search is probe/ack driven and does not yet ingest validated PTB messages. Adding PTB input later must validate that the quoted packet actually corresponds to recently transmitted DHMP traffic before it is allowed to influence the search.

## Security

MTU probe requests and responses use the existing session-derived directional path keys. Unauthenticated packets, wrong-session packets, padding modification and replayed probe identifiers are rejected.

This prevents blind/spoofed acknowledgments from raising the discovered MTU on the protected path.

## Still open

- automatic periodic PMTU maintenance / raise timer;
- live coupling of discovered budget to an active sender/client policy;
- validated ICMPv6 PTB acceleration;
- physical-router/path testing;
- demonstrating behavior across route changes and intentionally induced black holes;
- determining whether control/data protocol-number filtering differs on real middleboxes.
