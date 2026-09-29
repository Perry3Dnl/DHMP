# .NET 10 direct-IP API design

The protocol is independently implementable; these packages are the .NET reference work.

## Canonical projects

Use only src/DHMP.Protocol, src/DHMP.Client, src/DHMP.Server, src/DHMP.Licensing and
src/DHMP.AspNetCore. Namespaces also use DHMP. There are no case-only alternate projects.

## Packet core

DhmpFixedContract supplies PayloadSize, Pmax and MaxPacketPayloadBytes. Components call
Validate at construction so default(struct) cannot bypass setup checks.

DhmpPacketProcessor.Process accepts one complete packet payload and a synchronous borrowed
batch callback. It checks length before publication and allocates no carry storage.
DhmpProcessingMode is Sequential or Latest; these are consumption policies, not delivery guarantees.

DhmpModelBoundary<T> validates record size at construction and rejects partial typed input.
Its cast requires matching native layout and byte order. It does not decode arbitrary schemas.

## Client and receiver

DhmpClient requires an IDhmpPacketSender and a validated contract. SendAsync accepts one
record; SendBatchAsync accepts a whole packet batch. The caller serializes sends through
completion and owns the sender lifetime. There is no ConnectAsync, port or default listener.

DhmpServer is the receiver-side protocol facade. ProcessPacket consumes an already-received
complete packet. The future backend is responsible for endpoint/session selection and packet
I/O; the facade does not conceal a network listener behind its name.

The IDhmpPacketSender contract is an explicit boundary for a direct-IP backend. No production
implementation is installed automatically. In-memory test implementations are only test doubles.

## Hosting and licensing

AddDHMP(applicationId, licenseKey, publicVerificationKey) configures offline license validation
and host startup state. ASP.NET host integration is optional and does not carry DHMP traffic.
The registration does not auto-create clients, servers or network endpoints without contracts.
RuntimeActivated records authorized host startup, not network readiness.

## Migration

Old listener/connect/event APIs and the case-variant package tree have been removed.
Old stream-framing benchmark results do not describe the new packet processor.
Configure a real packet backend and session contract before attempting network operation.
