# DHMP Licensing Design

> Status: design specification. This document defines the intended DHMP commercial licensing model and technical enforcement architecture. It is not the final legal license agreement.

## 1. Goals

DHMP licensing should make legitimate use simple and casual license sharing inconvenient without introducing aggressive DRM, hardware locking, always-online requirements, or overhead in the DHMP message hot path.

The guiding principle is:

> **Build for free. Pay when you ship.**

The protocol specification and the official DHMP implementation are separate concerns.

The DHMP protocol specification is intended to be **royalty-free to implement and use, including for commercial software**, while ownership/copyright in the DHMP specification remains with its owner. Publishing the specification does not place DHMP in the public domain or transfer ownership.

Third parties may create independent DHMP implementations without purchasing the official DHMP implementation. The official optimized .NET/Unity implementations are separate software products and may use a commercial application license.

Protocol conformance and use of the **DHMP Compatible** designation are defined separately in [CONFORMANCE.md](CONFORMANCE.md).

## 2. Commercial licensing model

### 2.1 Development is free

A developer may use the official DHMP implementation without a commercial application license for development activities such as:

- local development;
- prototypes;
- internal testing;
- automated tests and CI;
- benchmarking;
- evaluation before release.

A commercial application license becomes relevant when software using the official implementation is publicly/commercially released. The exact legal definition of "release" belongs in the final legal license.

### 2.2 License the application, not the developer

Licensing is product/application-centric.

One application license applies to one licensed application. It is not multiplied by:

- number of developers;
- number of development machines;
- number of end users;
- number of running instances;
- sender versus receiver role;
- client versus server role when both are components of the same licensed product.

The intended rule is:

> **You license what you own and ship, not what you connect to.**

If Company A owns Application A and Company B owns Application B, each company licenses its own application when required. DHMP communication between those independently owned applications does not create a shared or cross-license.

### 2.3 Company-size tiers

Pricing may scale with company size or another suitable commercial measure, but the exact thresholds and prices are intentionally not frozen in this document.

Changing tier must not change DHMP wire compatibility.

## 3. Threat model

The licensing system is deliberately not intended to make bypassing the license technically impossible.

It should:

- prevent users from inventing authentic licenses;
- prevent editing signed license fields without detection;
- discourage casually copying a license from one application to another;
- make the licensed owner and application identifiable;
- allow a known compromised or abused license to be revoked later.

It is accepted that a sufficiently motivated developer controlling their own executable can patch local license checks. Cryptography authenticates licenses; it cannot guarantee that customer-controlled binaries execute the verifier.

Legal licensing terms remain the ultimate authorization boundary.

## 4. Architecture

Licensing must remain outside the network processing hot path.

Conceptually:

```text
Application startup
       |
       v
DHMP Licensing
       |
       +-- development policy
       |
       +-- parse license
       |
       +-- verify signature
       |
       +-- verify application identity
       |
       +-- optional cached/online status
       |
       v
license state established once
       |
       v
DHMP runtime starts
```

Normal message processing MUST NOT perform signature verification, file access, HTTP calls, database access, or repeated license parsing.

A future .NET layout may expose this through a dedicated `DHMP.Licensing` component/package.

## 5. Application license

A production license is a signed data object. Version 1 should contain at least:

```text
LicenseVersion
LicenseId
ApplicationId
ApplicationName
Licensee
Tier
IssuedAt
[optional validity/subscription fields]
Signature
```

### LicenseId

A globally unique, cryptographically random identifier for the issued license. It MUST NOT depend on a simple sequential public number for security.

### ApplicationId

A stable identifier assigned to one licensed application/product. This is the primary machine-readable binding between the license and the application.

### ApplicationName

The human-recognizable application/game name. The runtime may compare this with configured and/or runtime application identity as an additional lightweight anti-sharing check.

### Licensee

The legal person or organization to whom the application license was issued.

### Tier

The commercial tier under which the application is licensed. Tier is licensing metadata and MUST NOT alter protocol interoperability unless a future commercial policy explicitly introduces separately licensed implementation features.

## 6. Offline-first verification

DHMP applications must be able to verify a license without contacting a DHMP licensing server.

The issuer holds a private signing key. The runtime contains only the corresponding public verification key.

Issuance:

```text
canonical license payload
        |
        v
DHMP private signing key
        |
        v
digital signature
        |
        v
payload + signature = application license
```

Runtime:

```text
application license
        |
        +--> payload
        |
        +--> signature
               |
               v
       embedded public key
               |
               v
         valid / invalid
```

The private signing key MUST NEVER be included in:

- the public GitHub repository;
- a NuGet package;
- a Unity package;
- client/server binaries;
- example projects;
- test fixtures intended for production distribution.

Tests must use a dedicated non-production test key pair.

The implementation must use a standard, reviewed asymmetric digital-signature primitive available reliably in the selected .NET stack. Custom cryptography MUST NOT be designed for DHMP licensing.

## 7. Canonical payload

Signature verification requires deterministic bytes.

Before implementation, DHMP Licensing v1 must define one canonical representation for every signed field, including:

- field ordering;
- text encoding;
- casing rules where applicable;
- timestamp representation;
- identifier representation;
- escaping;
- null/optional field behavior;
- license format versioning.

A license parser must reject ambiguous, malformed, unsupported, or duplicate representations rather than guessing.

## 8. Application identity check

The application identity check is intentionally a lightweight anti-sharing measure rather than strong DRM.

At startup the verifier should compare the signed license identity with the identity configured by the application.

Minimum comparison:

```text
license.ApplicationId   == application.ApplicationId
license.ApplicationName == application.ApplicationName
```

Where useful, the .NET implementation may additionally inspect assembly/process metadata. Such metadata is supplementary; it must not replace the stable ApplicationId.

Example:

```text
License:
  ApplicationId   = 7a92...
  ApplicationName = CookieClicker

Runtime:
  ApplicationId   = 7a92...
  ApplicationName = Battlefield

Result:
  APPLICATION_MISMATCH
```

Renaming or modifying a program may eventually bypass local identity checks if the developer controls the executable. Preventing that at all costs is explicitly not a goal.

## 9. Development mode

Development use should remain frictionless.

The implementation must distinguish development/evaluation use from a released licensed application without putting a hidden performance penalty in the runtime.

The exact release-mode API/build policy is still to be designed. It must not silently claim that an unlicensed production application is licensed.

Potential implementation mechanisms must be evaluated during the .NET implementation phase rather than frozen prematurely here.

## 10. Optional online status validation

Offline signature and application checks are the baseline.

A later licensing service may additionally answer whether a LicenseId is:

- active;
- revoked;
- expired;
- superseded;
- moved to another commercial tier;
- otherwise no longer valid.

Online validation MUST NOT be performed per message and SHOULD NOT be required for every application startup.

A future design may use periodically refreshed signed status data cached locally with a reasonable grace period. A temporary licensing-service outage must not automatically take production networking offline.

The online service is therefore a secondary status/revocation mechanism, not the cryptographic root of every runtime start.

## 11. Revocation

Revocation exists for exceptional cases such as:

- compromised/shared license credentials;
- fraudulent issuance;
- contractual termination;
- replacement of an incorrectly issued license.

Revocation policy must avoid treating legitimate scale as abuse. Large server clusters, cloud deployments, CI systems, and large numbers of game clients can all be legitimate uses of one application license.

Automatic revocation solely because a license appears on many machines is not part of the v1 design.

## 12. Validation results

The verifier should return structured results rather than only throwing generic exceptions.

Candidate states:

```text
Valid
Development
MissingLicense
MalformedLicense
UnsupportedLicenseVersion
InvalidSignature
ApplicationMismatch
Expired
Revoked
OnlineStatusUnavailable
```

The exact public API is an implementation decision, but failures must be diagnosable without exposing private signing material or weakening verification.

## 13. Security boundaries

### Trusted

- private signing infrastructure;
- production private signing keys;
- issuer-side license database.

### Untrusted

- customer machine;
- application binary;
- local license file;
- configuration files;
- local system clock;
- network between application and future licensing API.

The runtime public key is not secret.

Security must never depend on hiding the public verification algorithm or public key.

## 14. Performance requirements

Licensing is a startup/control-plane concern.

It MUST NOT:

- add fields to every DHMP message;
- add per-message cryptographic work;
- add license identifiers to the DHMP steady-state wire format solely for DRM;
- perform network license checks in the Stream Processor;
- add locks or allocations to the message hot path;
- alter `Every`/`Latest` processing semantics.

Benchmarks of DHMP transport/runtime performance should therefore remain representative with licensing enabled after successful startup validation.

## 15. Testing requirements

The .NET implementation must include automated tests for at least:

1. valid signed license;
2. invalid signature;
3. modified signed payload;
4. wrong ApplicationId;
5. wrong ApplicationName;
6. malformed license;
7. unsupported license version;
8. missing license under each supported runtime policy;
9. development-mode behavior;
10. expired license if expiry is adopted;
11. revoked status when online validation is implemented;
12. licensing-service outage/grace behavior when online validation is implemented;
13. proof that no private production signing key is present in distributed artifacts.

A benchmark/regression check should confirm that steady-state DHMP message processing does not call the licensing verifier.

## 16. Key rotation

The license format must leave room for signing-key rotation.

A future license may therefore include a non-secret `KeyId` identifying which trusted public verification key should verify the signature.

Old verification keys may remain available for existing licenses while new licenses are signed with a new private key.

Production private keys must be backed up and protected separately from source control.

## 17. Data retained by the issuer

A future licensing database will likely need:

```text
LicenseId
ApplicationId
ApplicationName
Licensee / customer reference
Tier
Issue date
Current status
Signing KeyId
Relevant subscription/validity state
Audit timestamps
```

Only data necessary for licensing and support should be collected. Hardware fingerprints are not required by this design.

## 18. Protocol interoperability

Licensing belongs to the official implementation, not the DHMP wire protocol itself.

Two independently owned applications may communicate over DHMP when each is legitimately entitled to use its own implementation.

The wire protocol must not require both peers to share:

- a license;
- a LicenseId;
- an owner;
- an application name;
- a licensing account.

A third party may independently implement and use the published DHMP protocol royalty-free, including commercially, subject to the final published protocol/specification license terms. This does not grant ownership of the DHMP specification, the official implementation source code, or unrestricted rights to DHMP branding.

Use of the official DHMP .NET/Unity implementation remains a separate software-licensing matter.

## 19. Decisions intentionally left open

The following must be decided before the commercial release but are not required to implement the first technical verifier:

- exact prices;
- company-size/revenue tier thresholds;
- exact legal definition of a released application;
- treatment of free but publicly released software;
- same game across Windows/Linux/macOS/consoles;
- DLC and expansions;
- sequels;
- white-label/OEM redistribution;
- contractors and outsourced development;
- subscription expiry versus perpetual application licenses;
- upgrade behavior when a company grows into another tier;
- open-source/noncommercial exceptions;
- exact online validation cadence and grace period.

These decisions should not be accidentally encoded into low-level protocol code.

## 20. Implementation sequence

When the .NET implementation is ready, licensing should be added in this order:

1. freeze License v1 fields and canonical encoding;
2. choose the standard signature API/primitive supported by the target .NET runtime;
3. create test-only issuer/signing utilities;
4. implement license parser;
5. implement offline signature verifier;
6. implement ApplicationId/ApplicationName verification;
7. integrate one startup validation point;
8. add negative/tampering tests;
9. verify zero steady-state hot-path interaction;
10. design production issuer/key storage separately;
11. add online status/revocation only after offline v1 is stable;
12. obtain legal review and publish the final commercial software license separately.

## 21. Non-goals for v1

DHMP Licensing v1 will not attempt to provide:

- unbreakable DRM;
- hardware locking;
- anti-debugging;
- executable obfuscation as a security boundary;
- per-user licensing;
- per-seat licensing;
- per-server-instance charging;
- per-message license validation;
- proprietary cryptographic algorithms.

The goal is a small, understandable and maintainable licensing system that discourages casual misuse while keeping the legitimate developer experience simple.
