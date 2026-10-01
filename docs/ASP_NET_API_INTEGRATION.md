# Automatic .NET API integration over DHMP

Status: experimental opt-in application integration, added 2026-09-30. This is new authorized scope alongside the stable-base work, not evidence of a stable release. Physical API-path validation and independent security review remain open.

## Developer contract

For a server-rendered .NET website calling a .NET backend, enable the integration on both hosts:

```csharp
using DHMP.AspNetCore;
builder.Services.AddDHMP(licenseKey);
```

The registration reads the `DHMP` configuration section, validates the application's offline license before opening a connection, starts PSK setup V2 and the Linux raw-IPv6 receive runtime, wraps factory-created client handlers and decorates the ASP.NET server to reuse its configured application pipeline. Existing controllers/minimal endpoints and `IHttpClientFactory` calls retain their application code. No explicit DHMP middleware registration, controller rewrite or application-message codec is required.

The original `AddDHMP(applicationId, licenseKey, verificationKey)` remains the existing license/host-only registration. Choose one registration per service collection. This integration is currently source/project-reference consumable; it is not a published NuGet release.

## Required configuration

Example frontend configuration, with placeholders supplied through your deployment/secret configuration:

```json
{
  "DHMP": {
    "ApplicationId": "YOUR-FRONTEND-APPLICATION-ID",
    "PublicVerificationKeyBase64": "YOUR-TRUSTED-ISSUER-PUBLIC-KEY",
    "Api": {
      "LocalAddress": "2001:db8::1",
      "RemoteAddress": "2001:db8::2",
      "ApiOrigin": "https://api.example/",
      "Initiator": true,
      "AcceptRequests": false,
      "PreSharedKeyBase64": "SHARED-32-BYTE-TEST-KEY-IN-BASE64",
      "KeyId": 1,
      "PathMtu": 1280,
      "RequestTimeout": "00:00:30",
      "HandshakeTimeout": "00:00:10"
    }
  }
}
```

On the backend, reverse local/remote IPv6 addresses, set `Initiator` to false and `AcceptRequests` to true. Use the backend's own ApplicationId and corresponding license. Both sides require the same PSK/key ID and DAPI/1 schema. Exactly one side initiates. Documentation IPv6 addresses must be replaced with actual addresses.

`ApiOrigin` is the exact scheme/host/port of the existing backend URL, with root path and no query/credentials. Its DNS name need not resolve for mapped calls: DHMP uses the configured IPv6 peer. Requests to another origin keep the originally configured handler. Requests to the mapped origin always use DHMP; failure never falls back to an ordinary request, even for GET. This avoids accidentally submitting a mutation twice.

A license key verifies entitlement at startup. It is not a peer address or a shared traffic-encryption key. Load the PSK from secret configuration, not source control. Public license verification material may be deployed normally. The integration does not discover peers, change firewall rules or obtain socket privileges.

## Supported application behavior

- Factory-created default, named and typed clients, including their existing delegating handlers.
- Buffered API requests/responses, paths/query strings, methods, bodies, status codes and ordinary headers.
- Existing middleware, authorization, controller routing, model binding, minimal endpoints and request DI scopes through the actual ASP.NET hosting application.
- Authentication headers are passed to normal middleware. PSK authentication does not grant an application user identity or bypass authorization.
- Application response callbacks and bounded buffered output. Unhandled application failures return a generic 500; an application's own exception middleware remains responsible for its deliberately produced error responses.
- Normal browser-to-website HTTPS continues independently. The server-to-server hop uses raw DHMP packets, not an HTTP/TCP/UDP fallback or tunnel.

Direct `new HttpClient(...)` instances are not discoverable by dependency injection and are not switched. Cookie-container behavior, automatic redirects and other behavior implemented inside the original primary network handler are not inherited by mapped calls; pass explicit application headers and handle redirect responses. Client delegating handlers still run and may have their own retry policies: disable those policies for mutating calls if duplicate execution is unacceptable.

Streaming responses, WebSockets/upgrades, trailers, certificate-based TLS identity, arbitrary HTTP server features, browser-native DHMP and transparent discovery are outside this initial profile. An `https` origin preserves the application's logical scheme; it does not mean TLS was used on the DHMP hop. Authentication/encryption on that hop comes from the experimental PSK profile.

## Limits and failure semantics

The profile uses 1200-byte complete application records and the existing 24-byte PSK envelope. Known PMTU and configured IPv6 extension-header overhead must fit both. Linux/native IPv6 and raw-socket permissions remain required; enabling a NuGet integration cannot remove OS requirements.

Defaults are 256 KiB per body, 512 KiB per serialized application message, 32 outstanding client requests, up to 64 incomplete incoming messages, a queue of 32 complete incoming requests and 8 application workers. Larger supported settings are validated with a combined admission/message budget. These component limits bound logical retained storage, not a measured RSS guarantee; temporary JSON/body copies also consume memory. The fixed 1024-request execution window does not grow during long sessions. Requests older than its window are dropped rather than executed again.

Complete records may arrive duplicated/reordered. DAPI/1 assembles its application chunks, validates lengths/padding and processes a request once. Conflicting fragments invalidate that request. Incomplete messages expire against a monotonic deadline. Saturated admission rejects a local call; saturated incoming storage/queues drop work, producing a client timeout. No unbounded request queue, record ACK, retransmission or implicit fallback exists.

A timeout/cancellation does not prove that a remote action did not execute. The library never automatically retries it. Caller cancellation stops local waiting/sending; it does not undo a remote action or deliver a remote cancellation signal. The remote worker has its own original request deadline. Use application idempotency keys/transactions where an external retry must be safe. Execution deduplication lasts only within the current connection/session, not across process restart, and is not a durable exactly-once guarantee.

Only one configured peer and one integration runtime per service collection are supported initially. Do not run multiple DHMP sessions for the same source address/protocol binding on the same host. Setup is one bounded exchange without retry/reconnect; start the responder before the initiator within the configured deadline. A failed transport stops the hosted service/host under the normal background-service failure policy. Restart both peers to establish fresh sessions; ongoing calls are not migrated between protocols.

`DhmpRuntimeState.ApiReady` means the local secure receive runtime is active; it does not establish peer API availability or completed physical validation. Calls made before readiness wait within their request deadline.

## Shutdown

The hosted runtime cancels/joins incoming I/O, retires the exchange, cancels queued/pending work, joins active application workers and outgoing sends, drains protected sender storage, then disposes sockets/crypto. Application code must honor `RequestAborted`; a worker ignoring cancellation can delay cleanup. A host stop timeout does not authorize disposing resources still in use.

The integration does not replay in-flight work during a protocol change. Changing registration/deployment chooses the mapped transport for future calls, not a live migration of existing requests.

## Runnable example and first API-path test

`benchmarks/raw-ipv6-api-app` is a small ordinary ASP.NET app with `/api/hello`, `/api/echo`, `/demo` and `/dhmp/status`. `/demo` uses an ordinary named factory client. The existing CI experiment-build job builds the sample.

```bash
dotnet build benchmarks/raw-ipv6-api-app/Dhmp.RawIpv6ApiApp.csproj -c Release
```

Configure the deployed sample on two Linux hosts using the settings above. The sample reads its license from `DHMP:LicenseKey` and its ordinary client BaseAddress from `Backend:BaseAddress`; keep that origin equal to `DHMP:Api:ApiOrigin`. Its placeholder appsettings intentionally cannot activate until valid application licenses, verification material, addresses and PSK are supplied. Use environment/secret configuration overrides such as `DHMP__Api__LocalAddress`, `DHMP__LicenseKey` and `Backend__BaseAddress`.

Start the backend/responder first, then the frontend/initiator, with appropriate raw-socket privileges. Allow Next Header 253/254 between the two hosts. The browser-facing ASP.NET listener remains an ordinary website listener. Visit the frontend `/dhmp/status` and require `apiReady: true`; then visit `/demo`. It must return the backend process ID/message through the existing API route. Capture a controlled packet trace to verify that the mapped inter-host request uses Next Header 253 and no ordinary backend request is emitted.

Record exact commit/configuration (omit secrets), host OS/NIC/MTU, request/response status/body, session startup, timeout behavior and clean shutdown. Exercise JSON POST, a denied authorization request, an unmatched route, a deliberately stopped backend, restart with fresh setup and a body exceeding the configured limit. Packet loss must surface as timeout without automatic repeated mutations. This sample/profile has not yet passed that physical two-host test; the earlier [packet smoke run](TWO_HOST_SMOKE.md) remains a complementary backend check.
