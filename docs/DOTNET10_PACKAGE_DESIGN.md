# .NET 10 direct-IP API design

The protocol is independently implementable; these packages are the .NET reference work.

## Canonical projects

Use only `src/DHMP.Protocol`, `src/DHMP.Client`, `src/DHMP.Server`, `src/DHMP.Licensing` and `src/DHMP.AspNetCore`.

Namespaces also use `DHMP.*`. There are no case-only alternate projects.

## Protocol identity

`DhmpProtocol.CurrentVersion` identifies the current pre-1.0 session/wire contract version.

`DhmpProtocol.ExperimentalIpv6NextHeader` records the experimental Next Header value used by the raw IPv6 research path. It is not a permanent protocol assignment.

## Session contract

`DhmpSessionContract` is the required protocol-owned agreement for one configured session.

It contains:

- protocol version;
- `DhmpFixedContract`;
- `DhmpProcessingMode`.

`DhmpFixedContract` supplies fixed record size, Pmax and maximum headerless DHMP packet payload bytes.

Client and server facades consume the same session contract so mode/version cannot silently diverge between the two sides of the reference API.

The future direct-IP control plane is responsible for establishing the same session values between remote peers.

## Packet core

`DhmpPacketProcessor.Process` accepts one complete headerless DHMP data payload and a synchronous borrowed batch callback.

The byte span contains application records only. The processor checks the complete payload length before publication and allocates no carry storage.

`DhmpProcessingMode` is Sequential or Latest. These are consumption policies, not network delivery guarantees.

`DhmpModelBoundary<T>` validates record size at construction and rejects partial typed input. Its cast requires a matching local representation; arbitrary application schemas remain outside the protocol core.

## Client and receiver

`DhmpClient` requires an `IDhmpPacketSender` and a validated `DhmpSessionContract`.

`SendAsync` accepts one record. `SendBatchAsync` accepts one complete headerless DHMP packet payload. The caller serializes sends through completion and owns the sender lifetime.

There is no `ConnectAsync`, port, hidden socket choice or default compatibility transport.

`DhmpServer` is the receiver-side facade for one `DhmpSessionContract`. `ProcessPacket` consumes an already-received complete DHMP payload.

The future direct-IP backend is responsible for IPv6 packet I/O, peer/path binding and control-plane/session establishment.

`IDhmpPacketSender` is the explicit outbound boundary for that backend. It must not silently add DHMP packet headers or convert the payload into a stream protocol.

## Hosting and licensing

`AddDHMP(applicationId, licenseKey, publicVerificationKey)` configures offline license validation and host startup state.

ASP.NET host integration is optional and does not carry DHMP data traffic. Registration does not auto-create clients, servers, sockets or network endpoints.

`RuntimeActivated` records authorized host startup, not network readiness.

## Migration

Old listener/connect/event APIs, byte-stream framing and compatibility transport implementations are not part of the active architecture.

The current API now requires an explicit session contract at the client/server facade boundary.

Historical stream-framing benchmark results do not describe the headerless direct-IP packet processor.
