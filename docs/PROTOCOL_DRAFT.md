# DHMP protocol draft

This document captures the protocol model as of 2026-09-22. It is a working draft, not yet a frozen interoperability specification.

## 1. Purpose

DHMP — Direct Headerless Message Protocol — is intended for persistent communication between endpoints that can agree on a fixed data contract before steady-state traffic begins.

The design attempts to move decisions out of the per-message hot path. Contract identity, frame size, delivery semantics, security mode and other connection properties should be negotiated at setup rather than repeatedly carried inside every application frame.

## 2. Connection contract

The current .NET prototype uses a compact handshake containing:

```text
0..3    magic
4..5    protocol version
6..7    reserved
8..11   outgoing schema ID
12..15  outgoing fixed frame size
```

The final specification will likely expand connection negotiation as the protocol grows, but the important rule is that this is connection/session metadata rather than per-frame metadata.

Once the connection contract is accepted, both sides know the logical frame size.

## 3. Steady-state framing

A DHMP data lane is a byte stream containing fixed-size logical frames:

```text
[frame][frame][frame][frame]...
```

There is no DHMP length field on every frame because length is already known from the connection contract.

TCP may split or combine writes arbitrarily. DHMP reconstructs logical frames from the ordered byte stream using the negotiated frame size. A socket write is therefore not required to correspond 1:1 with a logical DHMP frame.

## 4. Delivery semantics

### 4.1 Unconfirmed

Unconfirmed means DHMP does not require application-level proof that every logical frame was accepted.

The sender may continuously push data without waiting for a reply.

If a session breaks, uncertain application frames do not have to be replayed.

TCP still handles normal packet loss, ordering and retransmission while a connection is alive. DHMP does not attempt to replace TCP's packet-recovery algorithms.

### 4.2 Verified

Verified deliberately keeps the same hot data path:

```text
sender                         receiver

frame  ----------------------->
frame  ----------------------->
frame  ----------------------->
frame  ----------------------->
...
```

There are no per-frame ACKs and no periodic ACKs merely because data is flowing.

The sender retains unverified logical frames locally. When a session breaks, the reconnect handshake obtains the receiver's authoritative accepted stream position. The sender can then replay the uncertain tail and continue.

Because the underlying stream is ordered, normal recovery is a tail/resume problem, not a search for arbitrary holes in the middle of a live TCP stream.

Conceptually:

```text
sender emitted through:       50137
receiver accepted through:    50112

resume/replay:                 50113..50137
```

### 4.3 Verification boundaries

Verified cannot retain an infinite amount of history while also refusing all reverse communication forever.

The intended next step is asynchronous checkpointing. A verification boundary can ask the receiver for its accepted position without stopping the sender's data stream. Once the answer arrives, older retained history can be discarded.

The protocol should optimize verification for bounded memory rather than turn normal sends into request/reply operations.

## 5. Consumption semantics

Delivery semantics and receive semantics are separate concerns.

### Every

Every complete frame is exposed to application processing.

Typical uses include command streams, replication changes, event/log shipping and other cases where each logical frame matters.

### Latest

The receiver may consume queued bytes in bulk and publish only the newest complete state frame.

If queued data looks like:

```text
[1][2][3][4][5][partial 6]
```

Latest may skip 1–4, expose 5, and retain the partial bytes for 6.

This is intended for state such as current position, RPM, telemetry snapshots, UI/device state or other data where a newer complete state can make older queued states obsolete.

## 6. Security

Two first-class deployment modes are intended:

- `dhmp://` — DHMP over a reliable byte-stream transport such as TCP.
- `dhmps://` — DHMP wrapped in standard TLS.

DHMPS should use platform TLS rather than inventing DHMP-specific cryptography.

TLS record framing and cryptographic overhead remain real transport costs, but the DHMP application frame itself remains fixed-contract data.

## 7. Current boundaries

The prototype currently assumes trusted peers and trusted contracts. The final protocol still needs explicit decisions around:

- interoperability and byte order;
- schema/version compatibility rules;
- authentication and certificate/service identity expectations;
- resource limits and malicious-peer behavior;
- checkpoint and retention policy for Verified mode;
- reconnect/session identity;
- application processing versus transport acceptance semantics;
- flow control and bounded sender history;
- route/endpoint negotiation for higher-level service APIs.

## 8. Design rules

1. Anything known before steady-state transmission should not be repeated on every frame.
2. Do not reproduce TCP's packet-loss recovery at the DHMP layer.
3. Reliability above TCP should address logical DHMP acceptance/session recovery.
4. Sending should not become synchronous merely because a caller wants delivery verification.
5. Latest-state applications should not be forced to spend CPU reconstructing obsolete states.
