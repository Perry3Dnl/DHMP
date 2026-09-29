# DHMP wire contract V1

Status: working pre-1.0 contract. Breaking changes are still allowed.

## Purpose

DHMP V1 defines a deliberately small headerless data plane directly over IP.

The data plane does not wrap each record in a DHMP frame and does not prepend a DHMP packet header.

For the current IPv6 implementation path, an IP packet is associated with DHMP by the backend/protocol binding. The raw IPv6 backend currently permits experimental IPv6 Next Header values `253` and `254`. These are not permanent protocol assignments for DHMP.

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

A future control plane may exchange capabilities such as maximum acceptable packet size or schema identity, but exchanging a capability does not make that capability part of the headerless data packet.

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

- Pmax logical messages per local one-second budget window;
- maximum DHMP payload bytes the endpoint will submit in one packet.

These values do not alter record interpretation and do not need to equal the remote peer's local values.

A usable deployment must nevertheless ensure that the sender does not exceed what the remote path/receiver accepts. Future session capability exchange can establish that safe bound.

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

Cross-packet freshness requires either an application-owned field or an explicit future version/profile.

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

V1 does not currently define a production security envelope.

Security metadata must not be silently inserted into the V1 headerless layout. Any protocol-owned wire bytes require an explicit versioned contract/profile change.

## Compatibility rule

TCP, UDP, HTTP, QUIC, WebSocket, gRPC and TLS-stream transports are not DHMP V1 data-plane fallbacks. Historical implementations remain in git history only.
