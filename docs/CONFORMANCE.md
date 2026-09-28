# DHMP Protocol Conformance

> Status: design specification. This document defines the intended technical meaning of **DHMP Compatible**. Final trademark/branding terms should be reviewed separately before public release.

## 1. Purpose

DHMP is intended to be an openly implementable protocol with a stable, testable interoperability contract.

The protocol may be independently implemented and used royalty-free, including commercially, while ownership/copyright in the DHMP specification remains with its owner.

**DHMP Compatible** is intended to mean something stronger than "uses ideas from DHMP": an implementation claiming compatibility must conform to the published DHMP protocol requirements and interoperate with other conforming implementations.

## 2. Separation from commercial implementation licensing

Protocol conformance and implementation licensing are separate.

A third party does not need to purchase the official DHMP.NET or DHMP.Unity implementation in order to create a conforming implementation.

Conversely, purchasing or using an official DHMP implementation does not permit a modified, non-conforming protocol to be represented as DHMP Compatible.

The intended model is:

```text
DHMP protocol/specification
    |
    +-- royalty-free implementation/use
    |
    +-- objective conformance requirements
    |
    +-- DHMP Compatible designation
              |
              v
       passes conformance suite

Official implementations
    |
    +-- DHMP.NET
    +-- DHMP.Unity
    +-- future official implementations
              |
              v
       separate software licensing
```

## 3. Meaning of DHMP Compatible

An implementation may claim **DHMP Compatible** only when the applicable version of the implementation passes the required conformance tests for the DHMP protocol version/profile it claims to support.

Compatibility is versioned. A claim should identify the relevant protocol version where ambiguity is possible.

Examples:

```text
DHMP Compatible — Protocol v1
DHMP Compatible — Protocol v1 / DHMPS profile
```

A compatibility claim must not imply that the implementation was written, audited, supported, or commercially endorsed by the DHMP owner unless that is separately true.

## 4. Objective conformance

Conformance should be based on reproducible tests rather than subjective approval.

The conformance suite should eventually cover at least:

1. protocol version negotiation;
2. fixed-contract negotiation;
3. framing and payload boundaries;
4. exact fixed payload/frame requirements;
5. payload-size rejection behavior;
6. configured Pmax/rate contract behavior;
7. absence of DHMP fragmentation/reassembly;
8. required malformed-input handling;
9. connection lifecycle rules;
10. Latest semantics where supported by the relevant profile;
11. Every/sequential semantics where supported by the relevant profile;
12. interoperability with a reference peer;
13. DHMPS/TLS profile requirements when DHMPS compatibility is claimed.

The authoritative list must ultimately come from the frozen protocol specification and conformance test manifest, not from this preliminary design document.

## 5. Conformance levels and profiles

DHMP should avoid a vague single compatibility claim if optional protocol profiles later exist.

A conformance manifest may therefore describe capabilities such as:

```text
ProtocolVersion: 1
FixedContract: required
Latest: supported
Every: supported
DHMPS: supported
ComputeBlock: optional/not applicable
```

Optional implementation optimizations must not become wire-level requirements unless explicitly specified by the protocol.

A slower implementation can still be DHMP Compatible. Conformance measures correctness and interoperability, not benchmark performance.

## 6. Reference conformance suite

The project should provide a public automated conformance suite.

The suite should be usable by independent implementations in languages such as C++, Rust, Java, Go, C#, or others without requiring the official commercial runtime.

Where practical, test vectors should be language-neutral.

Suggested structure:

```text
conformance/
  README.md
  manifest/
  vectors/
  malformed/
  interoperability/
  reference/
```

The exact implementation can be added after the wire specification is sufficiently stable.

## 7. Required test categories

### 7.1 Positive vectors

Known-valid handshakes, contracts, frames, and state transitions must produce the specified result.

### 7.2 Negative vectors

Known-invalid inputs must be rejected in the specified way. Tests should include truncated data, incorrect lengths/contracts, unsupported versions, invalid state transitions, and values outside negotiated limits.

### 7.3 Boundary vectors

Tests must cover minimum/maximum negotiated sizes and values at, below, and above defined limits.

### 7.4 Interoperability

At least two independently built endpoints should be able to exchange valid DHMP traffic according to the same negotiated contract.

The official implementation may serve as one reference endpoint, but the specification and test vectors remain authoritative.

### 7.5 Transport segmentation

For stream transports, tests must verify that arbitrary TCP/TLS segmentation does not change DHMP logical message semantics.

Transport segmentation must not be confused with DHMP-level fragmentation.

## 8. DHMPS

DHMPS conformance means DHMP operated through the specified standard secure transport/profile.

DHMPS must use standard platform cryptography/TLS as defined by the applicable specification. Passing DHMP framing tests alone is insufficient to claim the DHMPS profile if the secure transport requirements are not met.

## 9. Badge and designation

A future **DHMP Compatible** badge/logo may be published for implementations that meet the conformance requirements.

The badge should communicate protocol compatibility, not ownership.

Intended rules:

- passing the required conformance suite is necessary;
- the badge must not be modified in a misleading way;
- it must not imply official authorship or endorsement;
- it must identify the applicable protocol version/profile when required;
- compatibility can be re-tested after material protocol-facing changes.

The exact trademark/logo license must be published separately and should receive legal review.

## 10. Self-certification versus certification service

The initial model should favor low-friction self-certification using the public conformance suite.

A developer can run the suite and publish the results for the implementation/version being claimed as compatible.

A future official certification service may be added if ecosystem demand justifies it, but protocol adoption must not depend on paying for certification.

## 11. Evidence

A project claiming DHMP Compatible should ideally publish machine-readable evidence containing:

```text
ImplementationName
ImplementationVersion
DHMPProtocolVersion
SupportedProfiles
ConformanceSuiteVersion
TestResult
TestTimestamp
SourceCommitOrBuildId
```

For open-source implementations this may be generated in CI. Closed-source implementations may publish the test report without publishing proprietary source code.

## 12. Compatibility is not performance certification

The DHMP Compatible designation does not mean:

- fastest implementation;
- official implementation;
- security audit completed;
- bug-free software;
- identical internal architecture;
- identical API;
- commercial endorsement.

Performance claims require separate reproducible benchmarks.

An independent implementation is free to use a completely different internal architecture as long as its externally observable protocol behavior conforms.

## 13. Compatibility is not implementation licensing

No license key from DHMP.Licensing should be required merely to execute the public protocol conformance tests against an independent implementation.

Official DHMP software used as part of a released product remains subject to its own implementation license.

This separation prevents commercial implementation licensing from becoming a de facto toll on the protocol itself.

## 14. Versioning

Conformance must be tied to protocol versions.

A new protocol revision must document whether it is:

- backward compatible;
- conditionally compatible;
- incompatible and therefore a new major protocol version.

The conformance suite must preserve historical vectors needed to test supported protocol versions.

## 15. Implementation plan

When the protocol specification is stable enough, implement conformance in this order:

1. freeze the normative wire requirements for Protocol v1;
2. assign stable requirement identifiers;
3. create machine-readable valid and invalid test vectors;
4. create a conformance manifest format;
5. implement a reference test runner;
6. test the official .NET implementation against it;
7. test at least one independent/minimal reference implementation where practical;
8. document CI integration;
9. publish the DHMP Compatible usage rules;
10. design the badge/logo only after the technical requirements are objective and reproducible.

## 16. Non-goals

The conformance program is not intended to:

- force third parties to use the official implementation;
- charge royalties for implementing the protocol;
- prescribe internal implementation architecture;
- require a particular programming language;
- certify benchmark performance;
- replace security auditing;
- make compatibility dependent on company size;
- make compatibility dependent on a commercial DHMP.NET/Unity license.

The goal is simple: when developers see **DHMP Compatible**, they should be able to rely on a concrete, reproducible interoperability contract.
