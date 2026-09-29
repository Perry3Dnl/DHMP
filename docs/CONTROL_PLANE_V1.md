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

## Data-plane transition

After successful negotiation, data uses protocol / Next Header 253 and the normal V1 headerless payload:

```text
[record][record][record]...
```

The 32-byte control packet is not repeated on data packets.

## Security boundary

Base compatibility Control V1 is unauthenticated.

When the PSK security profile is active, the same experimental control protocol 254 also carries distinct fixed-size authenticated control packet families: 48-byte `DHMS` security setup and 64-byte `DHMF` congestion feedback. Packet magic/length separate these profiles; they are not prepended to data.

Do not interpret successful compatibility negotiation as proof that the remote endpoint is trusted.

The separate [PSK security profile V1](SECURITY_PSK_V1.md) adds a distinct 48-byte HMAC-authenticated security-control exchange on the same experimental control binding plus authenticated-encryption/replay handling for protected data. [Authenticated congestion feedback](CONGESTION_FEEDBACK.md) adds a 64-byte session-bound `DHMF` control packet. Base 32-byte Control V1 itself remains only compatibility negotiation.
