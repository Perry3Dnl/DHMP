# DHMP protocol draft

This document captures the protocol model as of 2026-09-28. It is a working draft, not yet a frozen interoperability specification.

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

## 4. Delivery semantics and hard invariants

DHMP is **fire-and-forget**. There is no DHMP-level per-message acknowledgement, replay, reconnect history, or Verified delivery mode.

### 4.1 One message must fit the contract

A logical DHMP application message MUST fit completely inside the negotiated fixed payload/frame contract. DHMP MUST NOT fragment one application message into multiple logical DHMP messages and MUST NOT provide reassembly.

If `payload.Length > MaxPayload`, the sender API MUST reject the send before bytes enter the DHMP send path.

TCP or TLS may split/coalesce bytes internally. That is transport segmentation only; it does not relax the DHMP message-size invariant.

### 4.2 Processing-rate ceiling

A DHMP sender MUST be bounded by a configured maximum receiver/processor capacity `Pmax`, expressed as logical messages per second for the active contract/profile. Implementations SHOULD apply a safety factor below the measured/configured ceiling:

```text
AllowedSendRate = floor(Pmax * SafetyFactor)
0 < SafetyFactor <= 1
```

Exceeding the rate budget MUST NOT create an unbounded queue. The API may reject/drop an attempted send according to its explicit runtime policy, but MUST NOT silently turn fire-and-forget traffic into queued reliable delivery.

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

A receiver implementation may also retain a very small bounded newest-state window rather than exactly one frame. The current experimental Ring-3 fast path retains at most the newest three complete states and always overwrites the oldest retained state when necessary. This is currently an implementation strategy for `Latest`, not an additional per-frame wire encoding.

The implementation is allowed to use a separate fixed reusable I/O workspace to amortize socket operations. Such workspace is not a logical message queue: its contents may be overwritten immediately after complete-frame boundaries have been identified and the newest useful state has been published.

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
- reconnect/session identity and fresh-session behavior;
- application processing versus transport acceptance semantics;
- rate-limit negotiation/configuration and overload policy;
- route/endpoint negotiation for higher-level service APIs.

## 8. Design rules

1. Anything known before steady-state transmission should not be repeated on every frame.
2. One logical application message MUST fit the negotiated fixed payload/frame contract.
3. DHMP MUST NOT fragment or reassemble application messages.
4. DHMP is fire-and-forget: no DHMP ACK, replay, or delivery-recovery mode.
5. Send rate MUST remain bounded by the configured `Pmax`; overload MUST NOT create an unbounded queue.
6. Latest-state applications should not be forced to spend CPU reconstructing obsolete states.
7. Retained state capacity should be bounded independently from transport I/O batch size.
8. Steady-state receive paths should prefer fixed reusable memory over per-frame allocation, shifting, or clearing.
