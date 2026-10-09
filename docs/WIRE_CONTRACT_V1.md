# DHMP wire contract V1

Status: working pre-1.0 contract. Breaking changes are still allowed.

## Purpose

DHMP V1 defines a deliberately small headerless data plane.

The data plane does not wrap each record in a DHMP frame and does not prepend a DHMP packet header.

For the preferred native IPv6 path, experimental protocol / Next Header `253` carries DHMP V1 data. Experimental value `254` is reserved by this project for the separate [Control V1](CONTROL_PLANE_V1.md) handshake. Neither value is a permanent DHMP assignment.

The application-facing resolver may instead select the explicit UDP compatibility carrier. In that profile the UDP payload is the exact same complete DHMP V1 record; UDP/IP headers are lower-layer carrier overhead, not DHMP framing.

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

The canonical direct-IP DHMP data payload is exactly one negotiated fixed-size application record:

```text
+--------------------+
| fixed record       |
+--------------------+
```

There are zero DHMP header bytes before the record and zero DHMP trailer bytes after it. The handshake/session already established the record size, so the steady-state raw receive path does not derive a record count from packet length.

A canonical raw V1 data payload therefore has exactly the negotiated record size. A payload with another size is simply ignored before Ring-3 publication; DHMP never carries partial record bytes into another packet.

The .NET compatibility and benchmark APIs may accept a local batch containing several complete records. That local batch is not the canonical raw wire shape: `DhmpClient.SendBatchAsync` submits each record as its own data packet.

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

- `Sequential`, experimental `UnsafeSequential`, `Latest`, or experimental `UnsafeLatest` publication/storage policy;
- maximum accepted DHMP payload bytes.

A receiver may therefore use Latest while the sender has no knowledge of that choice.

Sequential preserves every received record in arrival order through Ring-3 and then its FIFO backlog.

Experimental UnsafeSequential preserves FIFO order for records accepted into the process but lets plaintext fixed-slot transports receive directly into FIFO-owned memory, bypassing Ring-3 and its payload copy. When the FIFO is full, the receive loop stops posting the next socket receive earlier, so kernel/network loss under overload is easier to trigger. It adds no ACK, retransmission or delivery guarantee.

Latest publishes the received record immediately as the newest state.

Experimental UnsafeLatest is the minimal newest-state policy: plaintext fixed-slot transports receive into one reusable server-owned record slot and publish that borrowed span synchronously. The next receive may overwrite the same slot after the callback returns. It has no Ring-3 history, independent Latest grabber or Native Smoothing window.

None of these local policies changes the V1 wire bytes.

## Fragmentation

The DHMP V1 framing core does not provide application-message fragmentation or reassembly. The separately selected [DAPI/1 application schema](API_APPLICATION_PROFILE_V1.md) may assemble application messages from multiple complete fixed-size records above that core; it does not carry partial DHMP records across packets or add hidden V1 metadata.

The sender must keep a DHMP packet within its effective path budget after lower-layer and future security overhead are accounted for. Normal operation must not depend on IP fragmentation. The Linux raw backend explicitly requests `IPV6_DONTFRAG` so an oversized send is treated as a path-budget failure rather than silently fragmented by the host.

## Freshness and ordering

V1 has no protocol-owned sequence number, timestamp or generation identifier.

Sequential represents receive arrival order, not guaranteed original sender order after network reordering.

Latest selects the record in the most recently completed received packet. It cannot prove that a later-arriving packet contains newer generated state.

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

Base V1 remains an unprotected headerless data contract. It contains no protocol-owned checksum, authentication tag or session/epoch field. A plaintext deployment that requires corruption detection, authenticity or reconnect epochs must provide those properties in its application schema, or select a protection profile. The .NET raw receiver therefore requires explicit acceptance before it publishes unprotected records.

The reference implementation now includes an explicit experimental [PSK profile with security setup V2](SECURITY_PSK_V2.md). That profile wraps the V1 plaintext in a separate authenticated-encryption envelope and removes it before normal V1 packet processing. The security overhead is not presented as hidden V1 framing.

The current PSK profile has not completed an independent production security review and does not provide forward secrecy.

## Compatibility rule

Transport selection does not change V1 record interpretation. Native Raw IPv6 is preferred; the active Connector may use the explicit UDP compatibility carrier defined in [TRANSPORT_RESOLVER.md](TRANSPORT_RESOLVER.md) when the native path cannot be established.

TCP, HTTP, QUIC, WebSocket, gRPC and TLS-stream transports are not DHMP V1 data-plane fallbacks. Historical implementations remain in git history only.
