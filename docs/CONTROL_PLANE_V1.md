# DHMP control plane V1

Status: working pre-1.0 contract.

The DHMP V1 data plane remains headerless. Control metadata is exchanged separately and is never prepended to data packets.

## IPv6 experimental bindings

For the current raw IPv6 implementation:

- protocol / Next Header `253`: DHMP headerless data packets;
- protocol / Next Header `254`: DHMP control packets.

Both values are IANA experimental-use values. They are not permanent DHMP assignments.

## Scope

Control V1 performs compatibility and capability exchange for a peer whose IPv6 address is already known/configured.

It currently does **not** provide:

- peer discovery;
- authentication;
- encryption;
- key exchange;
- multi-peer session multiplexing;
- reliability/retransmission for control packets.

A successful handshake means that the endpoints reported compatible DHMP settings. It does not prove peer identity.

## Fixed control packet

Every Control V1 packet is exactly 32 bytes:

```text
Offset  Size  Field
0       4     Magic = ASCII "DHMC"
4       1     Control version = 1
5       1     Message type
6       1     DHMP data wire version
7       1     Reject reason (0 unless REJECT)
8       2     Fixed record size, unsigned big-endian
10      2     Maximum receive payload bytes, unsigned big-endian
12      16    Application schema UUID, network/big-endian Guid representation
28      4     Correlation ID, unsigned big-endian
```

No variable-length fields exist in Control V1.

## Message types

- `HELLO = 1`
- `ACCEPT = 2`
- `REJECT = 3`

The initiator generates a non-zero correlation ID and sends HELLO.

The responder returns ACCEPT or REJECT with the same correlation ID.

Responses with another correlation ID do not complete that outstanding negotiation.

## HELLO

HELLO advertises:

- the sender's DHMP data wire version;
- fixed record size;
- maximum DHMP data payload the sender is willing to receive;
- application schema UUID.

The advertised receive payload is a capability, not a change to the V1 data wire format.

## ACCEPT

A responder sends ACCEPT when:

- the data wire version matches;
- the fixed record size matches;
- the schema UUID matches.

ACCEPT advertises the responder's own maximum receive payload.

After ACCEPT, a sender clamps its local outbound packet ceiling to the lower of:

- its configured local send ceiling; and
- the remote advertised receive ceiling.

The result is rounded down to a whole number of fixed records.

Pmax is not negotiated and remains local.

Sequential/Latest is not negotiated and remains local.

## REJECT

Control V1 rejection reasons are:

1. unsupported data wire version;
2. fixed record size mismatch;
3. schema UUID mismatch;
4. receive capability too small for one record;
5. unexpected control message.

A REJECT terminates that one negotiation attempt.

## Bounded .NET handshake lifetime

`DhmpRawIpv6Options.HandshakeTimeout` defaults to ten seconds and can be configured with the optional `handshakeTimeout` constructor/`FromPathMtu` argument. It must be positive and finite. Both initiator and one-shot responder use one deadline covering the complete control send/receive exchange. Ignoring another correlation ID or a non-HELLO does not reset that deadline.

Expiry raises `TimeoutException`; caller cancellation remains `OperationCanceledException` with the caller's token. A pre-cancelled valid call does not open a raw socket. Channel resources are disposed after success, rejection, malformed input, sender failure, cancellation or timeout. Malformed/truncated input from the configured source fails the attempt; it is not silently treated as a compatible peer.

There is no automatic retransmission. If HELLO or ACCEPT is lost, the attempt times out. A caller may start a new compatibility attempt with a new correlation ID, but must manage the remote endpoint lifecycle: local timeout does not prove the responder failed to accept the previous attempt. These APIs are one-shot exchanges, not an established control-session manager. The compatibility and PSK phases each have their own deadline and are run in sequence for a configured peer.

## Data-plane transition

After successful negotiation, data uses protocol / Next Header 253 and the normal V1 headerless payload:

```text
[record][record][record]...
```

The 32-byte control packet is not repeated on data packets.

## Security boundary

Base compatibility Control V1 is unauthenticated.

When the PSK security profile is active, the same experimental control protocol 254 also carries distinct authenticated control packet families: 64-byte `DHMS` version 2 security setup and 64-byte `DHMF` congestion feedback. Magic and version separate these profiles; they are not prepended to data. Historical 48-byte security setup V1 is not accepted by the current raw-IPv6 setup APIs.

Do not interpret successful compatibility negotiation as proof that the remote endpoint is trusted.

The separate [PSK setup V2](SECURITY_PSK_V2.md) uses an authenticated OFFER/CHALLENGE/CONFIRM/ACCEPT exchange on the same experimental control binding plus authenticated-encryption/replay handling for protected data. [Authenticated congestion feedback](CONGESTION_FEEDBACK.md) adds a 64-byte session-bound `DHMF` control packet. Base 32-byte Control V1 itself remains only compatibility negotiation.
