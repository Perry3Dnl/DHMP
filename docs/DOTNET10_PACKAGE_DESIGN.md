# .NET 10 direct-IP API design

The protocol is independently implementable; these packages are the .NET reference work.

## Canonical projects

Use only `src/DHMP.Protocol`, `src/DHMP.Client`, `src/DHMP.Server`, `src/DHMP.Licensing`, `src/DHMP.AspNetCore`, `src/DHMP.RawIpv6` and `src/DHMP.Security`.

Namespaces also use `DHMP.*`. There are no case-only alternate projects.

## Protocol identity

`DhmpProtocol.CurrentVersion` identifies the current pre-1.0 session/wire contract version.

`DhmpProtocol.ExperimentalIpv6DataNextHeader` is experimental value 253 for headerless data. `DhmpProtocol.ExperimentalIpv6ControlNextHeader` is experimental value 254 for Control V1. Neither is a permanent assignment.

## Wire contract and local policies

`DhmpWireContract` is the protocol-owned interoperability contract. It contains only:

- protocol version;
- fixed record size.

`DhmpSendPolicy` is local sender configuration:

- Pmax logical-message rate;
- local `RejectWindow` or `SmoothPacing` behavior;
- maximum outbound DHMP payload bytes per packet.

`DhmpReceivePolicy` is local receiver configuration:

- Sequential or Latest publication;
- maximum accepted DHMP payload bytes per packet.

Peers may use different send budgets, packet ceilings and receive modes while still interpreting the same DHMP V1 bytes. Future control-plane negotiation may exchange capabilities, but local policy values do not become data-plane wire metadata.

## Packet core

`DhmpPacketProcessor.Process` accepts one complete headerless DHMP data payload and a synchronous borrowed batch callback.

The byte span contains application records only. The processor checks the complete payload length before publication and allocates no carry storage.

`DhmpProcessingMode` is Sequential or Latest. These are consumption policies, not network delivery guarantees.

`DhmpModelBoundary<T>` validates record size at construction and rejects partial typed input. Its cast requires a matching local representation; arbitrary application schemas remain outside the protocol core.

## Client and receiver

`DhmpClient` requires an `IDhmpPacketSender`, a validated `DhmpWireContract` and a local `DhmpSendPolicy`.

`SendAsync` accepts one record. `SendBatchAsync` accepts one complete headerless DHMP packet payload. The caller serializes sends through completion and owns the sender lifetime. Under `SmoothPacing`, the client asynchronously delays packet submission according to logical message count instead of rejecting a burst at the fixed one-second window boundary. An optional `DhmpAdaptiveRateController` lets authenticated receiver pressure reduce that pacing rate and then recover it gradually within the local Pmax bounds.

There is no `ConnectAsync`, port, hidden socket choice or default compatibility transport.

`DhmpServer` is the receiver-side facade for one `DhmpWireContract` plus local `DhmpReceivePolicy`. `ProcessPacket` consumes an already-received complete DHMP payload.

`DhmpLatestGenerationFilter` is an optional server-side helper for Latest workloads whose application schema contains a 64-bit generation field. It filters stale/duplicate cross-packet state without modifying the DHMP V1 data layout. It supports configurable field offset, endianness and modulo-2^64 wraparound.

`IDhmpPacketSender` is the explicit outbound direct-IP boundary. It must not silently add DHMP packet headers or convert the payload into a stream protocol.

## Raw IPv6 backend

`DHMP.RawIpv6` is the first concrete network implementation of that boundary. It is intentionally Linux-only until other operating-system raw-socket semantics are validated separately.

`DhmpRawIpv6Options` binds one local IPv6 address, one expected remote IPv6 address and a backend payload ceiling. Data and control protocol numbers are fixed by the experimental profile: 253 for data and 254 for control.

`DhmpRawIpv6PacketSender` sends the DHMP V1 payload directly through an IPv6 raw socket. If the kernel reports `SocketError.MessageSize`, the backend surfaces `DhmpPathMtuException` rather than hiding the packet-too-large failure. `DhmpRawIpv6Receiver` remains the simple one-peer receive path.

`DhmpIpv6PathBudget` converts a known IPv6 PMTU into the maximum raw protocol payload after the 40-byte IPv6 base header and optional extension-header bytes. It can then subtract an explicit packet envelope and round down to a whole number of DHMP records. `DhmpRawIpv6Options.FromPathMtu` and `DhmpRawIpv6ListenerOptions.FromPathMtu` expose this calculation. This is budgeting from a known PMTU, not dynamic PMTU discovery.

`DhmpRawIpv6MultiPeerReceiver` adds a bounded server-side receive loop. `DhmpRawIpv6PeerRouter` maps source IPv6 address to a `DhmpRawIpv6PeerBinding`, which owns the per-peer `DhmpServer`, publication callback and optional packet decoder. Registration/removal is outside the packet hot path; receive routing uses source-address lookup. Each V1 source IPv6 address may have only one registered DHMP session because the headerless data plane carries no session ID.

`DhmpRawIpv6Handshake` performs a one-shot HELLO/ACCEPT/REJECT exchange on the control binding. It validates wire version, record size and schema UUID, advertises receive capability and returns a `DhmpNegotiatedPeer` with an effective outbound packet ceiling. It does not discover peers or authenticate identity. The backend still cannot multiplex different DHMP contracts between the same address pair because V1 data carries no protocol-owned session ID.

## Control plane

`DhmpControlCodec` encodes a fixed 32-byte Control V1 packet. `DhmpControlNegotiator` contains pure compatibility logic with no socket dependency.

The current control packet carries magic, control version, message type, data wire version, record size, maximum receive payload, schema UUID and correlation ID.

Pmax and Sequential/Latest are intentionally not negotiated.

See [Control V1](CONTROL_PLANE_V1.md) for the byte layout and security limitations.

## Security profile

`DHMP.Security` is an explicit optional layer, not part of the base V1 framing core.

`DhmpPskChaCha20Poly1305Session` derives separate send/receive keys and nonce prefixes from a 256-bit PSK plus a fresh session ID using HKDF-SHA256. Protected data carries an 8-byte counter and 16-byte ChaCha20-Poly1305 tag.

`DhmpProtectedPacketSender` wraps any `IDhmpPacketSender`. Its `MaximumPayloadBytes` subtracts the 24-byte security overhead.

`IDhmpPacketDecoder` is the receive-side protocol boundary. `DhmpRawIpv6Receiver` can use a decoder before V1 validation and clears decrypted packet storage after the synchronous callback returns.

`DhmpRawIpv6SecurityHandshake` uses 64-byte HMAC-SHA256-authenticated PSK setup V2 over control protocol 254, with a fresh responder challenge and transcript confirmation before session activation. It does not provide forward secrecy or automatically downgrade to historical setup V1.

See [PSK security setup V2](SECURITY_PSK_V2.md).

`DhmpRawIpv6CongestionChannel` uses the security session to authenticate/replay-protect ongoing `DHMF` pressure feedback on control protocol 254. It can run a periodic reporter loop from `DhmpBoundedReceiveDispatcher` snapshots and a receive loop that applies authenticated feedback to `DhmpAdaptiveRateController`. See [authenticated congestion feedback](CONGESTION_FEEDBACK.md).

## Hosting and licensing

`AddDHMP(applicationId, licenseKey, publicVerificationKey)` configures offline license validation and host startup state.

ASP.NET host integration is optional and does not carry DHMP data traffic. Registration does not auto-create clients, servers, sockets or network endpoints.

`RuntimeActivated` records authorized host startup, not network readiness.

## Migration

Old listener/connect/event APIs, byte-stream framing and compatibility transport implementations are not part of the active architecture.

The current API separates protocol interoperability from endpoint-local send and receive policy.

Historical stream-framing benchmark results do not describe the headerless direct-IP packet processor.
