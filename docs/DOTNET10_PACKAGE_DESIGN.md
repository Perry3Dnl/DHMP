# .NET 10 direct-IP API design

The protocol is independently implementable; these packages are the .NET reference work.

## Canonical projects

Use only `src/DHMP.Protocol`, `src/DHMP.Client`, `src/DHMP.Server`, `src/DHMP.Licensing`, `src/DHMP.AspNetCore` and `src/DHMP.RawIpv6`.

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

`SendAsync` accepts one record. `SendBatchAsync` accepts one complete headerless DHMP packet payload. The caller serializes sends through completion and owns the sender lifetime. Under `SmoothPacing`, the client asynchronously delays packet submission according to logical message count instead of rejecting a burst at the fixed one-second window boundary.

There is no `ConnectAsync`, port, hidden socket choice or default compatibility transport.

`DhmpServer` is the receiver-side facade for one `DhmpWireContract` plus local `DhmpReceivePolicy`. `ProcessPacket` consumes an already-received complete DHMP payload.

`DhmpLatestGenerationFilter` is an optional server-side helper for Latest workloads whose application schema contains a 64-bit generation field. It filters stale/duplicate cross-packet state without modifying the DHMP V1 data layout. It supports configurable field offset, endianness and modulo-2^64 wraparound.

`IDhmpPacketSender` is the explicit outbound direct-IP boundary. It must not silently add DHMP packet headers or convert the payload into a stream protocol.

## Raw IPv6 backend

`DHMP.RawIpv6` is the first concrete network implementation of that boundary. It is intentionally Linux-only until other operating-system raw-socket semantics are validated separately.

`DhmpRawIpv6Options` binds one local IPv6 address, one expected remote IPv6 address and a backend payload ceiling. Data and control protocol numbers are fixed by the experimental profile: 253 for data and 254 for control.

`DhmpRawIpv6PacketSender` sends the DHMP V1 payload directly through an IPv6 raw socket. `DhmpRawIpv6Receiver` accepts payloads only from the configured peer, rejects truncated/malformed packets and hands valid payloads to one `DhmpServer` session.

`DhmpRawIpv6Handshake` performs a one-shot HELLO/ACCEPT/REJECT exchange on the control binding. It validates wire version, record size and schema UUID, advertises receive capability and returns a `DhmpNegotiatedPeer` with an effective outbound packet ceiling. It does not discover peers or authenticate identity. The backend still cannot multiplex different DHMP contracts between the same address pair because V1 data carries no protocol-owned session ID.

## Control plane

`DhmpControlCodec` encodes a fixed 32-byte Control V1 packet. `DhmpControlNegotiator` contains pure compatibility logic with no socket dependency.

The current control packet carries magic, control version, message type, data wire version, record size, maximum receive payload, schema UUID and correlation ID.

Pmax and Sequential/Latest are intentionally not negotiated.

See [Control V1](CONTROL_PLANE_V1.md) for the byte layout and security limitations.

## Hosting and licensing

`AddDHMP(applicationId, licenseKey, publicVerificationKey)` configures offline license validation and host startup state.

ASP.NET host integration is optional and does not carry DHMP data traffic. Registration does not auto-create clients, servers, sockets or network endpoints.

`RuntimeActivated` records authorized host startup, not network readiness.

## Migration

Old listener/connect/event APIs, byte-stream framing and compatibility transport implementations are not part of the active architecture.

The current API separates protocol interoperability from endpoint-local send and receive policy.

Historical stream-framing benchmark results do not describe the headerless direct-IP packet processor.
