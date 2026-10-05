# DHMP Connector

`DHMP.Connector` is the application-facing package for normal DHMP networking.

The public model is intentionally small:

```text
DhmpConnector
    owns local DHMP networking
        |
        +-- DhmpConnection -> one remote IPv6 peer
        +-- DhmpConnection -> another remote IPv6 peer
```

A connection is bidirectional. The words client and server are not protocol roles in this API.

## Install

The package is still repository-only during active R&D. When published, the intended entry point is:

```sh
dotnet add package DHMP.Connector
```

## Configure

```csharp
using System.Net;
using DHMP.Connector;
using DHMP.Security;

using var key = new DhmpPreSharedKey(
    keyId: 1,
    key: my32ByteSecret);

var options = new DhmpConnectorOptions(
    localAddress: IPAddress.Parse("2001:db8::10"),
    recordSize: 32,
    schemaId: new Guid("11111111-1111-1111-1111-111111111111"))
{
    PreSharedKey = key,

    // Required while the native backend uses the IANA experimental
    // IPv6 Next Header values 253/254.
    EnableExperimentalProtocolNumbers = true
};

await using var dhmp = new DhmpConnector(options);
await dhmp.StartAsync();
```

## Connect to a known peer

```csharp
DhmpConnection peer =
    await dhmp.ConnectAsync("2001:db8::20");

peer.RecordReceived += (connection, record) =>
{
    // record is one complete fixed-size application record.
    // The span is valid only during this synchronous callback.
};

await peer.SendAsync(my32ByteRecord);
```

The other connector accepts the explicitly configured peer:

```csharp
DhmpConnection peer =
    await dhmp.AcceptAsync("2001:db8::10");

await peer.SendAsync(responseRecord);
```

Both calls return the same `DhmpConnection` abstraction. Either side can send and receive after establishment.

## Why AcceptAsync currently takes an address

The current DHMP control plane negotiates compatibility and security with an already configured IPv6 peer. It does not yet perform safe discovery of arbitrary unknown peers.

For that reason, Connector V1 deliberately exposes:

```text
ConnectAsync(remoteAddress)
AcceptAsync(remoteAddress)
```

rather than pretending that an unrestricted accept-any-peer listener already exists.

Automatic unknown-peer discovery/admission should be added at the control-plane boundary before the Connector API exposes it.



## Messaging vocabulary

Connector APIs describe actions from the local application's perspective:

```text
SendAsync(...)      send one message
SendBurstAsync(...) send many messages at a configured rate
ReceiveAsync()      wait for the next message
OnReceive(...)      register handling for a named route
```

Named routes are an application profile above the unchanged DHMP V1 data plane. They do not add protocol-owned V1 headers or change fixed-record framing.

Example:

```csharp
peer.OnReceive("player/input", record =>
{
    HandlePlayerInput(record);
});

await peer.SendAsync(
    "player/state",
    state,
    DhmpDelivery.FireAndForget);
```

Delivery behavior remains explicit policy:

- `FireAndForget` submits once with no confirmation wait;
- `Confirmed` waits for application-owned confirmation evidence and does not retransmit;
- `FullEcho` waits for the full-record echo profile and does not retransmit;
- burst sending reuses the same route with a caller-selected rate bounded by the connection's configured send ceiling.

The same vocabulary is used on both sides. There is no client/server reversal: code that calls `Send...` sends, and code that uses `Receive...` receives.

## Duplicate source IPv6 connections

By default, one source IPv6 address maps to one `DhmpConnection`:

```csharp
DuplicatePeerHandling =
    DhmpDuplicatePeerHandling.Reject;
```

This preserves the normal zero-extra-work routing path. If a second logical connection must intentionally share the same source IPv6 address, enable:

```csharp
DuplicatePeerHandling =
    DhmpDuplicatePeerHandling.ResolveWithConnectionId;

ConnectionIdField =
    new DhmpConnectionIdField(offset: 0);
```

The 8-byte ConnectionId field is part of the application's existing fixed record. It is **not** a DHMP V1 header and does not change packet framing.

When this mode is enabled:

1. the ConnectionId field definition is folded into the schema identity already checked by the DHMP compatibility handshake;
2. authenticated PSK setup creates a unique session ID shared by both peers;
3. Connector derives a nonzero 64-bit `DhmpConnection.ConnectionId` from that authenticated session;
4. Connector stamps that value into the configured application field on send;
5. normal source-IPv6 routing remains the fast path while only duplicate-source addresses require ConnectionId resolution.

Because the connection identity is derived from the authenticated security session, `ResolveWithConnectionId` requires the PSK profile. A mismatched field layout fails normal schema negotiation instead of being guessed at runtime.

If duplicate resolution is disabled, a second connection from the same source IPv6 is rejected with a clear error. DHMP never silently merges two ambiguous peers.

## BlindFire: registered one-packet telemetry

BlindFire is a separate opt-in path for tiny/intermittent devices that should send one plaintext fixed record without establishing a DHMP connection on every wake.

One-time server registration:

```csharp
var options = new DhmpConnectorOptions(
    localAddress,
    recordSize: 32,
    schemaId)
{
    AllowUnprotectedBlindFire = true,
    EnableExperimentalProtocolNumbers = true
};

await using var server = new DhmpConnector(options);
await server.StartAsync();

DhmpBlindFireRegistration sensor =
    server.RegisterIoTDevice("2001:db8::42");

sensor.RecordReceived += (registration, record) =>
{
    // Process one complete fixed-size plaintext record.
};
```

A device that has already been registered can later start a fresh process and send exactly one record without a DHMP HELLO/ACCEPT or security handshake:

```csharp
var options = new DhmpConnectorOptions(
    localAddress,
    recordSize: 32,
    schemaId)
{
    AllowUnprotectedBlindFire = true,
    EnableExperimentalProtocolNumbers = true
};

await using var device = new DhmpConnector(options);

await device.BlindFireAsync(
    serverAddress,
    temperatureRecord);
```

The sender does not need to call `StartAsync` or establish a `DhmpConnection`. It opens the raw sender, emits one DHMP V1 data record, and disposes the sender.

**Security boundary:** BlindFire provides no encryption, cryptographic sender authentication, replay protection, handshake confirmation, or delivery confirmation. The server only accepts packets from a pre-registered source IPv6 address and validates them against the already configured fixed-record contract. Source IPv6 and schema/record validation are routing/validation checks, not cryptographic identity.

Use BlindFire only when that tradeoff is acceptable for the application, such as non-sensitive telemetry on a controlled network. Normal protected DHMP connections remain separate and do not become plaintext merely because BlindFire is enabled.

## Security

The recommended Connector path supplies `PreSharedKey`. The Connector then performs the existing compatibility handshake followed by the authenticated PSK setup and uses the resulting session for protected send and receive traffic.

Plaintext is possible only with explicit opt-in:

```csharp
AllowUnprotectedPayloads = true
```

That switch should be treated as an explicit deployment decision, not a convenience default.

## Responsibilities

`DhmpConnector` owns:

- local raw-IPv6 listener lifetime;
- compatibility/security establishment;
- peer registration and removal;
- the set of active connections.

`DhmpConnection` owns the application-facing relationship with one remote address:

- `RemoteAddress`;
- `SendAsync` / `SendBatchAsync`;
- synchronous zero-copy `RecordReceived`;
- negotiated payload ceiling;
- per-peer lifetime.

The lower-level `DHMP.Protocol`, `DHMP.RawIpv6`, `DHMP.Client`, `DHMP.Server` and `DHMP.Security` projects remain separate implementation/tooling boundaries. Normal applications should not need to assemble those pieces by hand once the Connector surface is complete.
