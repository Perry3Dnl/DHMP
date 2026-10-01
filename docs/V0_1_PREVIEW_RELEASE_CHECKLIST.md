# DHMP v0.1 preview release-readiness checklist

Status: **PREPARATION ONLY — DO NOT PUBLISH**

This document tracks work required before any public NuGet publication. The repository currently contains no workflow that pushes DHMP packages to NuGet.org, and release-prep CI must remain local-only until the repository owner explicitly decides to publish.

## Current intended preview scope

- Protocol: DHMP V1 fixed-record, headerless data plane.
- Runtime target: .NET 10.
- Native transport: experimental Linux raw IPv6.
- Experimental IPv6 Next Header values: 253 data / 254 control, explicit opt-in only.
- Security: optional experimental PSK profile.
- Reliability: no automatic retransmission; application-owned confirmation and full echo are optional profiles.
- Network scope: controlled/validated IPv6 paths. General Internet/router reachability is not claimed.

## Completed release-prep gates

- [x] Linux and Windows managed test matrix.
- [x] Raw IPv6 namespace rehearsal.
- [x] Reproducible benchmark workflow.
- [x] Experimental protocol numbers disabled by default.
- [x] Plaintext raw receive requires explicit acceptance.
- [x] Wildcard raw service ownership requires explicit acceptance.
- [x] Conservative 1280-byte unknown-path mode.
- [x] Linux raw sockets explicitly disable IPv6 source fragmentation.
- [x] Shared NuGet metadata for repository URL, descriptions, tags, README and symbols.
- [x] Local package-consumer CI: pack all public projects to an isolated local feed, restore clean consumer projects, and compile them.

## Hard gates before publication

- [ ] **Choose and add the repository/package software license.** This is a legal/product-owner decision and is intentionally not guessed by the implementation.
- [ ] Decide whether the first public preview is intentionally .NET 10-only or whether another target is required and actually supportable.
- [ ] Add a NuGet-compatible package icon (PNG/JPEG) if desired. The repository logo is currently WebP.
- [ ] Audit every public type/member for naming, XML documentation, lifetime/disposal behavior and preview stability.
- [ ] Add a minimal external-user quickstart that does not assume knowledge of the repository's benchmark harnesses.
- [ ] Perform a clean-machine package inspection from the generated local packages.
- [ ] Decide the preview version/tag and release notes.
- [ ] Explicitly accept/document which deployment limitations remain open for the preview: physical two-host/NIC validation, router/ISP reachability matrix, DPLPMTUD, complete Internet congestion control and independent security review.
- [ ] Explicit owner approval to publish.

## Publication safety rule

No automated workflow should contain a NuGet API key, `dotnet nuget push`, `nuget push`, or equivalent package-publication action during the preparation phase.

The supported preparation command is:

```sh
python3 tools/test_local_packages.py
```

It creates temporary `.nupkg` / `.snupkg` files in an isolated local feed, restores two clean consumer projects from that feed, builds them, and deletes the workspace. It does **not** contact NuGet.org to publish anything.

## Planned package IDs

- `DHMP.Protocol`
- `DHMP.Client`
- `DHMP.Server`
- `DHMP.RawIpv6`
- `DHMP.Security`
- `DHMP.Licensing`
- `DHMP.AspNetCore`

The package split mirrors the current project boundaries. An umbrella/meta-package should only be added if it materially improves onboarding without hiding the Linux/raw-IP deployment boundary.
