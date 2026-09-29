# DHMP wire contract V1

Status: working pre-1.0 contract. Breaking changes are still allowed.

## Purpose

DHMP V1 defines a deliberately small headerless data plane directly over IP.

The data plane does not wrap each record in a DHMP frame and does not prepend a DHMP packet header.

For the current IPv6 implementation path, experimental protocol / Next Header `253` carries DHMP V1 data. Experimental value `254` is reserved by this project for the separate [Control V1](CONTROL_PLANE_V1.md) handshake. Neither value is a permanent DHMP assignment.

## Wire compatibility contract

Two DHMP V1 implementations interpret the data payload the same way when they agree on:

- protocol version: `1`;
- fixed record size in bytes;
- the same application record schema.

The .NET reference implementation represents the protocol-owned values with `DhmpWireContract`.

The wire contract deliberately does **not** contain:

- Pmax;
- sender pacing/budget;
- Sequential or Latest publication mode;
- local packet/buffer ceiling;
- socket buffer sizes;
- application queue behavior.

Those are endpoint policies. Different peers may use different local policies while still speaking the same DHMP V1 wire format.

The implemented Control V1 plane exchanges maximum receive capability and schema identity. Exchanging those values does not make them part of the headerless data packet.

## V1 data packet

The DHMP data payload is exactly:

```text
+--------------------+--------------------+-----+--------------------+
| fixed record 0     | fixed record 1     | ... | fixed record N-1   |
+--------------------+--------------------+-----+--------------------+
```

There are zero DHMP header bytes before the first record, zero DHMP separator bytes between records and zero DHMP trailer bytes after the final record.

Record boundaries are derived from the agreed fixed record size and the received IP payload length.

A valid V1 data payload therefore:

1. is non-empty;
2. is an exact multiple of the fixed record size;
3. contains each record completely inside one IP packet.

An implementation also applies its local receive packet ceiling before publication.

The receiver rejects an invalid packet in full. DHMP never carries partial record bytes into another packet.

## Local send policy

The .NET reference implementation uses `DhmpSendPolicy` for local outbound behavior.

It currently contains:

- Pmax logical messages per second;
- `RejectWindow` or `SmoothPacing` local rate behavior;
- maximum DHMP payload bytes the endpoint will submit in one packet.

These values do not alter record interpretation and do not need to equal the remote peer's local values.

A successful Control V1 handshake advertises the remote receive ceiling and clamps the local outbound ceiling to a whole-record value that does not exceed it.

## Local receive policy

The .NET reference implementation uses `DhmpReceivePolicy` for:

- `Sequential` or `Latest` publication;
- maximum accepted DHMP payload bytes.

A receiver may therefore use Latest while the sender has no knowledge of that choice.

Sequential publishes every complete record in the received packet in packet-local order.

Latest publishes only the final complete record in the received packet.

Neither policy changes the V1 wire bytes.

## Fragmentation

DHMP does not provide application-message fragmentation or reassembly.

The sender must keep a DHMP packet within its effective path budget after lower-layer and future security overhead are accounted for. Normal operation should not depend on IP fragmentation.

## Freshness and ordering

V1 has no protocol-owned sequence number, timestamp or generation identifier.

Sequential represents receive arrival order, not guaranteed original sender order after network reordering.

Latest selects the final record in one received packet. It cannot prove that a later-arriving packet contains newer generated state.

Cross-packet freshness may use an application-owned generation field inside the fixed record without changing V1. The .NET reference implementation provides `DhmpLatestGenerationFilter` for an optional unsigned 64-bit generation field. Because the generation bytes belong to the application schema, DHMP still adds zero data-plane header bytes. A protocol-owned freshness field would still require an explicit future version/profile.

## Reliability

DHMP V1 has no:

- ACK;
- retransmission;
- replay history;
- delivery recovery;
- unbounded reliability queue;
- stream reconstruction.

Packet loss, duplication and reordering remain observable properties of the lower network path.

## Payload interpretation

The DHMP protocol core treats record bytes as opaque.

Application schemas define field layout, numeric representation and byte order. Two implementations need both a matching DHMP wire contract and a matching application schema to interpret records identically.

## Buffer ownership

A receive callback may borrow packet storage only for the duration documented by the implementation. Asynchronous retention requires explicit ownership or copying.

A send buffer remains valid until the direct-IP backend has finished using local storage. Completion does not mean remote delivery.

## Security

Base V1 remains an unprotected headerless data contract.

The reference implementation now includes an explicit experimental [PSK security profile](SECURITY_PSK_V1.md). That profile wraps the V1 plaintext in a separate authenticated-encryption envelope and removes it before normal V1 packet processing. The security overhead is not presented as hidden V1 framing.

The current PSK profile has not completed an independent production security review and does not provide forward secrecy.

## Compatibility rule

TCP, UDP, HTTP, QUIC, WebSocket, gRPC and TLS-stream transports are not DHMP V1 data-plane fallbacks. Historical implementations remain in git history only.
