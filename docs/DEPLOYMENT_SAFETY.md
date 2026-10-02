# DHMP deployment safety model

Status: pre-1.0 safety contract for the native raw-IPv6 backend.

DHMP V1 deliberately keeps the data payload headerless. That decision is preserved here: none of the safeguards below add protocol-owned bytes to a V1 data record.

## Plaintext integrity

Base DHMP V1 does not include a checksum, authentication tag or other protocol-owned end-to-end integrity field.

IPv6 validates its own header handling, but applications must not interpret that as an end-to-end integrity guarantee for an arbitrary upper-layer DHMP payload. A plaintext record that is accepted by the fixed-record contract can therefore only be treated as application data, not as authenticated or tamper-evident data.

The .NET raw receiver consequently fails closed for a missing packet decoder unless the caller explicitly sets:

```csharp
allowUnprotectedPayloads: true
```

Use that option only when the deployment intentionally accepts plaintext semantics, for example a controlled benchmark or an application schema that performs its own integrity validation.

For normal protected deployments, use `DHMP.Security`. ChaCha20-Poly1305 authentication supplies integrity/authentication before plaintext V1 records are published.

An application may also place its own checksum/hash inside its fixed record schema. Those bytes remain application-owned and do not become a DHMP header.

## Service selection: IPv6 address, not a DHMP port

DHMP V1 has no transport port field.

The native raw profile therefore treats the **local IPv6 destination address as the service-selection boundary**. The recommended deployment is one explicit IPv6 address per independently owned DHMP service.

Example:

```text
2001:db8:10::10  -> game-state DHMP service
2001:db8:10::11  -> telemetry DHMP service
2001:db8:10::12  -> simulation DHMP service
```

A raw listener bound to `::` can receive traffic for multiple local addresses and can therefore accidentally claim traffic intended for another DHMP service. Wildcard binding is disabled by default and requires:

```csharp
allowWildcardLocalAddress: true
```

Use wildcard ownership only when one process intentionally owns all DHMP traffic for the selected raw protocol binding and performs any required application-level dispatch itself.

Single-peer bindings also reject an unspecified remote address. A configured peer must be an explicit native IPv6 address.

This design preserves the headerless data path. If a future DHMP version needs port-like multiplexing on one IPv6 address, that would be a deliberate version/profile decision rather than invisible metadata added to V1.

## Unknown path MTU

When the path MTU has not been established, do not guess 1500 bytes.

RFC 8201 permits a node that does not perform PMTU Discovery to use the IPv6 minimum MTU of 1280 bytes. The .NET backend therefore provides conservative factories:

```csharp
DhmpRawIpv6Options.ForUnknownPath(...)
DhmpRawIpv6ListenerOptions.ForUnknownPath(...)
```

They budget from a 1280-byte IPv6 packet. With no additional IPv6 extension headers this leaves 1240 bytes for the raw upper-layer payload before any DHMP security envelope is subtracted/aligned.

A larger `FromPathMtu(...)` value is appropriate only when the deployment has a reason to trust that path budget.

This conservative mode avoids pretending that automatic PMTU discovery exists. It can sacrifice throughput on paths that support larger packets.

The Linux raw socket also explicitly enables `IPV6_DONTFRAG` (RFC 3542 section 11.2). DHMP therefore does not rely on the kernel inserting an IPv6 Fragment header for an oversized send. An oversized packet should fail or produce PMTU feedback instead of silently becoming multiple IP fragments. This matches the V1 rule that normal operation does not depend on IP fragmentation.

Authenticated DPLPMTUD search, explicit re-confirmation, and live sender payload adaptation are implemented on the protected control channel; see [DPLPMTUD.md](DPLPMTUD.md). A dynamically managed sender starts at the 1280-byte IPv6 base budget, raises only after authenticated confirmation, and falls back to the base budget when re-confirmation fails. Automatic periodic maintenance/raise timers and validated ICMPv6 PTB acceleration remain separate work.

## Raw-socket privilege

The Linux native backend requires raw-socket permission. If the kernel rejects raw-socket creation with EPERM/EACCES, the .NET backend surfaces an `UnauthorizedAccessException` that explains the `CAP_NET_RAW` requirement instead of a generic socket failure.

Privilege is a deployment property, not a DHMP wire feature. Containers/services should grant the smallest required capability rather than broadly running an application as root.

`DhmpRawIpv6HostProbe.Probe(enableExperimentalProtocolNumbers: true)` can open and immediately close both experimental raw bindings before application startup so Linux/IPv6/permission failures are reported as a local readiness result. Probe success does not establish remote-path reachability.

## Session replacement and stale plaintext packets

Protected sessions use fresh authenticated session keys/identity and reject packets from an old session after replacement.

Plaintext V1 deliberately has no protocol-owned session ID or epoch in every data packet. An application that needs to distinguish old/new plaintext state across reconnects must include its own generation/epoch in its record schema. The existing application-generation support can be used where that model fits.

## What these safeguards do not claim

They do not establish:

- arbitrary consumer-router traversal;
- cloud load-balancer compatibility;
- full Internet congestion control;
- hidden/background automatic DPLPMTUD maintenance or validated ICMPv6 PTB acceleration;
- a permanent IANA protocol number;
- Windows raw-socket support;
- production security review.

Those remain separate release/deployment gates.
