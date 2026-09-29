# DHMP wire contract V1

Status: working pre-1.0 contract. Breaking changes are still allowed while the direct-IP backend is being built.

## Purpose

DHMP V1 defines a deliberately small data plane directly over IP. The data plane does not wrap each message in a DHMP frame and does not prepend a DHMP packet header.

For the current IPv6 research path, an IP packet is associated with DHMP by the backend/protocol binding. The raw IPv6 experiments currently use IPv6 Next Header value `253`, which is an experimental-use value and is not a permanent protocol assignment for DHMP.

## Session contract

Before data packets are accepted, both endpoints must already agree on the same session contract:

- protocol version: `1`
- fixed record size in bytes
- processing mode: `Sequential` or `Latest`
- Pmax logical messages per local one-second budget window
- maximum DHMP payload bytes allowed for one IP packet
- peer/path binding supplied by the direct-IP backend
- security context, once a production security profile exists

The .NET reference implementation represents the protocol-owned portion of this agreement with `DhmpSessionContract`.

Session discovery and negotiation are control-plane work and are not implemented yet. Until they exist, applications/backends must configure the same contract at both endpoints.

## V1 data packet

The DHMP data payload is exactly:

```text
+--------------------+--------------------+-----+--------------------+
| fixed record 0     | fixed record 1     | ... | fixed record N-1   |
+--------------------+--------------------+-----+--------------------+
```

There are zero DHMP header bytes before the first record, zero DHMP separator bytes between records and zero DHMP trailer bytes after the final record.

The number and boundaries of records are derived entirely from the agreed fixed record size and the received IP payload length.

A valid packet therefore satisfies all of the following:

1. payload length is greater than zero;
2. payload length does not exceed the session's maximum DHMP packet payload;
3. payload length is an exact multiple of the fixed record size;
4. every record is fully contained in this one IP packet.

The receiver rejects the entire packet if any condition fails. DHMP never carries partial record bytes into another packet.

## Fragmentation

DHMP does not provide application-message fragmentation or reassembly.

The sender must keep a DHMP packet within the configured path budget after lower-layer and future security overhead are accounted for. A production backend should avoid depending on IP fragmentation as normal operation.

## Sequential mode

Sequential publishes every complete record in the received packet in packet-local order.

Across packets it represents receive arrival order only. It does not promise delivery, uniqueness, original sender order or recovery.

## Latest mode

Latest publishes only the final complete record in the received packet.

V1 does not place a sequence number, timestamp or generation identifier on the wire. Therefore V1 cannot determine that a later-arriving packet contains newer generated state than an earlier-arriving packet. Cross-packet freshness requires a separately specified protocol revision or an application-owned field.

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

Application schemas define field layout, numeric representation and byte order. Two implementations are interoperable only when they use the same application schema in addition to the same DHMP session contract.

## Buffer ownership

A receive callback may borrow packet storage only for the duration documented by the local implementation. Asynchronous retention requires explicit ownership or copying.

A send buffer must remain valid until the direct-IP backend reports that it has finished using the local storage. That completion does not mean remote delivery.

## Security

V1 does not currently define a production security envelope. A reviewed direct-packet security profile must be specified separately before a secure mode is advertised.

Security metadata must not be silently inserted into the V1 headerless data layout. Any protocol-owned wire bytes require an explicit versioned contract change.

## Compatibility rule

TCP, UDP, HTTP, QUIC, WebSocket, gRPC and TLS-stream transports are not DHMP V1 data-plane fallbacks. Historical implementations remain in git history only.
