# Stable-base release gate

Decision: 2026-09-30. Finish and validate the existing feature set before release; performance fine-tuning follows that work.

This is an acceptance plan, not a claim that the gates below have passed. An implemented feature, a green managed test and a validated physical network path are different levels of evidence. Release progress is tracked by these gates rather than a percentage of files or features.

## Scope

The first stable base targets the existing .NET 10 implementation and Linux direct-IPv6 backend, for explicitly configured peers. All existing optional features remain in the stabilization scope: application-generation freshness, local/adaptive pacing, bounded dispatch, source-address routing, known-PMTU budgeting and the PSK security profile.

Base V1 remains headerless. Security overhead is confined to the selected PSK profile. Neither mode promises delivery, retransmission or sender ordering across reordered packets.

Discovery, additional operating-system backends, additional sessions per source IPv6 address, dynamic PMTU discovery, new security suites and a licensing website are future feature work. Their absence must be explicit in release documentation. Existing known-PMTU handling and pacing must still fail safely within their stated limits. The present pacing profile is not complete network congestion control; deployment scope must remain explicit until network safety and fairness have been validated.

## Newly authorized application integration

On 2026-09-30 the developer requested automatic server-to-server .NET API integration. The opt-in [DAPI/1 integration](ASP_NET_API_INTEGRATION.md) is now additional experimental scope. Managed client/server pipeline composition, bounded chunking, deduplication and lifecycle tests are required; physical two-host API traffic, load/degraded behavior, security review and clean package consumption remain open gates. Do not count this new integration as completed stable-base evidence solely from core tests.

## Feature acceptance matrix

| Existing feature | Required acceptance evidence | Current evidence / open gate |
| --- | --- | --- |
| Fixed-record core, Sequential and Latest | Whole-packet rejection for invalid input; exact ceiling boundaries; borrowed publication ownership; matching sender/receiver schema | Implemented with managed tests; retain conformance vectors and full CI evidence |
| Client and direct-IP sender | Serialized-send contract; cancellation; backend failure; exact local Pmax; no implicit retry or remote-delivery claim | Managed hardening tests plus drained protected-send shutdown exist; real-backend two-host smoke runner prepared, physical composition execution remains open |
| Control V1 compatibility | Compatible peers and explicit rejects; unrelated/malformed input; caller cancellation; documented handling of lost control packets | Bounded ten-second configurable one-shot deadline and privilege-free exchange lifecycle tests added; actual raw-socket/path and session-recovery gates remain open |
| Source-address multi-peer routing | Maximum registration under concurrency; unknown peers rejected; removal during receive; isolation between peer contracts and keys | Drained removal/atomic replacement and exception-safe decode cleanup implemented with concurrency/key-isolation tests; physical multi-peer and full operational send/control lifetime gates remain open |
| Latest generation filter | Stale/duplicate/out-of-order application generations; wrap boundaries; reset on session replacement; no wire additions | Managed freshness tests exist; integration/session-reset gate remains open |
| Bounded asynchronous dispatch | Exact queue capacity; overload; concurrent publish; cancellation/restart; disposal during idle/active consumption; buffer release on consumer failure | First stabilization change addresses concurrent publication/disposal and graceful shutdown; CI evidence required |
| Known-PMTU packet budget | Whole-record alignment with and without security; minimum usable budget; oversize fails explicitly; no intentional fragmentation | Calculation tests exist; actual kernel/path errors and configured PMTU behavior need physical validation |
| Pacing, pressure feedback and telemetry | Local Pmax stays authoritative; authenticated/session-bound control only; stale/replayed feedback; loss/reordering; no throttle mutation on invalid input | Managed tests exist; measured path behavior and competing-traffic fairness remain open |
| PSK security profile | Direction/session/key isolation; replay; malformed authenticated fields; failed authentication leaves state unchanged; disposal/zeroization; session renewal and key/counter reuse analysis | V2 responder freshness/confirmation implemented with captured-handshake regression tests; synchronous crypto/control ownership and send retirement hardened; independent review and full operational/session lifecycle validation remain open |
| Offline licensing and host integration | Valid/malformed/tampered/wrong-application keys; startup failure; restart/stop; development policy; issuer trust; no production signing material in packages | Managed tests exist; production issuance process and final distribution terms remain open |

## Work order

1. Harden ownership and lifecycle boundaries. Start with the dispatcher shutdown regression, then audit sender, receiver, peer removal and crypto session lifetimes.
2. Validate control setup and session composition. Define bounded caller timeouts, cancellation, handling of lost control packets and clean session replacement using the existing wire profiles.
3. Validate limits and degraded behavior together: packet ceilings, security overhead, overload, pacing, stale state, duplicate/reordered/lost packets and authenticated feedback.
4. Exercise Linux raw sockets on two physical hosts, including protected/unprotected traffic, multiple peers, stop/restart, configured PMTU errors and prolonged runs. Capture hardware, permissions, packet counters, loss, latency and memory behavior.
5. Complete security review, public API/documentation consistency and package-consumer checks. Record exact supported platforms, privileges, deployment limits and licensing/distribution terms.
6. Only then tag the stable base and use measured bottlenecks to guide performance tuning.

## Required release evidence

- Architecture guard and the complete Ubuntu/Windows managed CI matrix pass for the exact release commit. Windows managed tests do not establish Windows raw-socket support.
- Each feature has happy, critical, boundary and applicable concurrency/lifecycle regression coverage. Any open correctness defect blocks the stable tag.
- The raw-IPv6 path has recorded two-physical-host validation; a skipped privileged test is not a pass.
- A prolonged-run report records its workload, duration, queue bounds, memory, stop/restart behavior and packet-accounting results. Set acceptance thresholds before the run.
- Network deployment boundaries and pacing/congestion limitations are documented and supported by path tests. Broad Internet deployment is not inferred from loopback/LAN success.
- The PSK profile remains experimental until independent review and its session-lifecycle gates pass. Passing unit tests alone does not qualify the full feature set as stable security.
- Packages can be consumed by a clean sample application; package metadata, versioning, public API and license documents agree with the implementation.

If a gate is blocked by hardware, permissions or an external review, record the blocker and retain it as open. Do not substitute a memory benchmark or an estimated release percentage.

The [hosted API rehearsal](HOSTED_API_REHEARSAL.md) supplies real raw-socket/API evidence on virtual interfaces. Its report explicitly cannot satisfy the physical two-host/NIC gate.
