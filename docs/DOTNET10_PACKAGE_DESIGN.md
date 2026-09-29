# .NET 10 direct-IP API design

The protocol is independently implementable; these packages are the .NET reference work.

## Canonical projects

Use only `src/DHMP.Protocol`, `src/DHMP.Client`, `src/DHMP.Server`, `src/DHMP.Licensing`, `src/DHMP.AspNetCore` and `src/DHMP.RawIpv6`.

Namespaces also use `DHMP.*`. There are no case-only alternate projects.

## Protocol identity

`DhmpProtocol.CurrentVersion` identifies the current pre-1.0 session/wire contract version.

`DhmpProtocol.ExperimentalIpv6NextHeader` records the experimental Next Header value used by the raw IPv6 research path. It is not a permanent protocol assignment.

## Wire contract and local policies

`DhmpWireContract` is the protocol-owned interoperability contract. It contains only:

- protocol version;
- fixed record size.

`DhmpSendPolicy` is local sender configuration:

- Pmax logical-message budget;
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

`SendAsync` accepts one record. `SendBatchAsync` accepts one complete headerless DHMP packet payload. The caller serializes sends through completion and owns the sender lifetime.

There is no `ConnectAsync`, port, hidden socket choice or default compatibility transport.

`DhmpServer` is the receiver-side facade for one `DhmpWireContract` plus local `DhmpReceivePolicy`. `ProcessPacket` consumes an already-received complete DHMP payload.

`IDhmpPacketSender` is the explicit outbound direct-IP boundary. It must not silently add DHMP packet headers or convert the payload into a stream protocol.

## Raw IPv6 backend

`DHMP.RawIpv6` is the first concrete network implementation of that boundary. It is intentionally Linux-only until other operating-system raw-socket semantics are validated separately.

`DhmpRawIpv6Options` binds one local IPv6 address, one expected remote IPv6 address, a payload ceiling and an experimental protocol number (`253` or `254`).

`DhmpRawIpv6PacketSender` sends the DHMP V1 payload directly through an IPv6 raw socket. `DhmpRawIpv6Receiver` accepts payloads only from the configured peer, rejects truncated/malformed packets and hands valid payloads to one `DhmpServer` session.

The backend does not perform session negotiation and cannot multiplex different DHMP contracts between the same address pair because V1 carries no protocol-owned session ID.

## Hosting and licensing

`AddDHMP(applicationId, licenseKey, publicVerificationKey)` configures offline license validation and host startup state.

ASP.NET host integration is optional and does not carry DHMP data traffic. Registration does not auto-create clients, servers, sockets or network endpoints.

`RuntimeActivated` records authorized host startup, not network readiness.

## Migration

Old listener/connect/event APIs, byte-stream framing and compatibility transport implementations are not part of the active architecture.

The current API separates protocol interoperability from endpoint-local send and receive policy.

Historical stream-framing benchmark results do not describe the headerless direct-IP packet processor.
