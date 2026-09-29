# DHMP test strategy

Status: active repository quality gate.

DHMP tests are intended to protect protocol semantics, bounded-resource behavior, security state and platform-independent .NET behavior. Test count alone is not a quality metric.

## Required flow classes

New behavior should normally cover each applicable class below.

### Happy flow

Proves the intended supported path end-to-end at the smallest useful scope.

Examples:

- valid fixed records are published unchanged;
- compatible peers negotiate successfully;
- protected packets decrypt in both directions;
- a registered raw IPv6 peer routes to the correct session;
- a valid license activates the hosted runtime.

### Critical flow

Proves failure is explicit, bounded and does not create hidden side effects.

Examples:

- malformed packets never reach application callbacks;
- failed authentication does not advance replay state;
- cancelled sends do not reach the backend;
- queue saturation does not allocate an unbounded backlog;
- a backend failure is not retried implicitly;
- invalid/default structs fail closed at public trust boundaries;
- decoder contract violations cannot escape as unsafe slicing or stale plaintext.

### Boundary flow

Locks exact protocol/resource thresholds.

Examples:

- packet size exactly at the configured ceiling;
- one byte over/under record boundaries;
- 64-packet replay-window edge;
- 2% and 10% experimental rolling-loss thresholds;
- IPv6 minimum MTU and record alignment;
- one free queue slot versus saturation.

### Concurrency flow

Required for state shared by multiple threads.

Examples:

- receive-region acquisition never double-leases storage;
- peer registration never exceeds the configured maximum;
- concurrent publishers respect exact bounded dispatcher capacity;
- adaptive feedback counters remain coherent under concurrent calls.

Tests must be deterministic. Avoid timing races where a barrier, bounded queue state or explicit cancellation can establish the condition.

### Lifecycle flow

Required for owned/disposable/hosted resources.

Examples:

- start -> active -> stop state;
- dispose is idempotent;
- disposed crypto/session objects reject reuse;
- async dispatcher cancellation releases its run ownership;
- caller-owned key buffers are copied when the API promises ownership isolation.

### Security-negative flow

Required for authenticated/encrypted profiles.

Cover at least:

- wrong PSK;
- wrong session;
- wrong direction;
- modified body/ciphertext/tag;
- replay;
- too-old replay-window input;
- malformed but authenticated control fields where constructible;
- authentication failure must not mutate replay/rate state.

## Test layering

### `DHMP.Protocol.Tests`

Pure wire/local-policy logic with no OS/network dependency.

Protect: wire contract, packet processor, control codec/negotiation, Pmax/pacing, congestion/path policy.

### `DHMP.Server.Tests`

Server storage, ownership and concurrency primitives.

Protect: preallocated receive-region lifecycle, bounded pool concurrency, typed/model boundaries, freshness state, bounded asynchronous overload behavior, congestion snapshot/advisor invariants.

### `DHMP.AspNetCore.Tests`

Client/server integration plus hosting/startup integration.

Protect: sender/receiver composition, cancellation/budget behavior, adaptive client integration, license startup gate and runtime lifecycle.

### `DHMP.RawIpv6.Tests`

Privilege-free raw-backend contract tests.

Protect: IPv6-only configuration, source-address peer routing, decoder trust boundary, PMTU budgeting, bounded multi-peer registration.

These tests must not require CAP_NET_RAW/root. Actual raw-socket runtime validation remains capability-aware/manual.

### `DHMP.Security.Tests`

Cryptographic profile behavior and negative cases.

Protect: PSK ownership/lifecycle, authenticated security control, AEAD data path, directional keys, replay windows, rolling secure loss telemetry, congestion feedback authentication, RTT/path-probe authentication, protected sender boundary.

### `DHMP.Licensing.Tests`

Offline license parser/signature/application binding.

Protect malformed input, signature tampering, issuer trust and caller-owned verification-key behavior.

## CI policy

The normal test workflow runs every managed test project on both Ubuntu and Windows.

Raw IPv6 runtime tests requiring OS capability are separate from normal CI.

All source and tests compile with warnings treated as errors.

## Regression rule

When a defect is found:

1. reproduce it with a failing focused test;
2. fix the smallest responsible production boundary;
3. keep the regression test permanently;
4. run the full Linux/Windows matrix;
5. do not weaken the test merely to make CI green unless the test assumption itself is proven incorrect and the intended behavior is documented.

## Performance tests

Correctness tests and benchmarks are separate.

A microbenchmark must never replace a correctness assertion. Benchmark failures must not be interpreted as protocol correctness failures unless the benchmark also validates the tested contract.

Physical throughput/latency claims require real NIC/path measurements, not only memory or hosted-runner ceilings.
