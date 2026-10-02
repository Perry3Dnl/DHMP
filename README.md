<p align="center">
  <img src="assets/dhmp-logo.webp" alt="DHMP logo" width="512">
</p>

# DHMP — Direct Headerless Message Protocol

**High-speed, fixed-record messaging directly over IPv6 — built for real-time systems where receiving the newest useful state can matter more than recovering every older packet.**

DHMP is a standalone message protocol with an intentionally tiny data path:

```text
application records
      ↓
     DHMP
      ↓
     IPv6
```

**V1 adds zero DHMP header bytes to normal data packets.** The payload is simply one or more complete fixed-size application records. No per-message framing, no stream reconstruction, no mandatory acknowledgements, and no hidden retransmission queue.

### What DHMP is built to do

- **Move fixed-size records with minimal protocol overhead.** One message is one record; one IPv6 packet can carry one or many complete records.
- **Choose freshness or completeness per workload.** `Latest` publishes only the newest record in a received packet; `Sequential` publishes every complete record in order.
- **Run directly over IPv6.** The active implementation does not use TCP or UDP as a compatibility transport.
- **Protect traffic when needed.** The optional PSK profile provides ChaCha20-Poly1305 protection, authenticated setup, replay defense and authenticated control traffic.
- **Adapt to the path instead of assuming a packet size.** Authenticated DPLPMTUD can discover a usable path MTU, apply it live to the raw sender, subtract security overhead and align the final client ceiling to complete records.
- **Stay bounded under pressure.** Local pacing, receiver pressure feedback, bounded receive queues and explicit overload behavior are built into the current implementation.
- **Keep reliability optional.** Applications can use no confirmation, application-owned lightweight confirmation, or full-record echo without changing the V1 data framing.
- **Support real server compositions.** The repository includes source-IPv6 multi-peer routing, experimental ASP.NET server-to-server integration, lifecycle handling, licensing gates and raw-socket test infrastructure.

### Why DHMP exists

Most mainstream transports are designed to solve broad, general-purpose networking problems. DHMP deliberately specializes.

For workloads such as real-time state replication, simulation, telemetry and tightly controlled server-to-server messaging, an older update may already be worthless by the time it is retransmitted. DHMP allows those systems to keep the transport primitive small and move delivery guarantees, generations, confirmation and application semantics into explicit opt-in policy.

That makes this a valid DHMP workload:

```text
state #840   ── lost
state #841   ── delayed
state #842   ── arrives

Latest → publish #842
```

The protocol does **not** silently turn that into a reliable ordered byte stream.

### Current capabilities

| Area | Current DHMP implementation |
| --- | --- |
| Data plane | Headerless fixed-record V1 |
| Processing | `Latest` and `Sequential` |
| Native transport | Experimental Linux raw IPv6 |
| Security | Optional PSK ChaCha20-Poly1305 profile |
| Path sizing | Authenticated DPLPMTUD + live sender adaptation |
| Rate behavior | Hard Pmax, smooth pacing and authenticated receiver pressure feedback |
| Freshness | Optional application-owned generation filter |
| Confirmation | None, lightweight application-owned confirmation, or full-record echo |
| Multi-peer | Bounded source-IPv6 routing |
| Application integration | Experimental ASP.NET server-to-server profile |
| Testing | Linux/Windows managed CI, raw IPv6 namespace rehearsal and reproducible benchmark suite |

### Where DHMP fits best today

DHMP currently makes the most sense when you control both endpoints and can deliberately configure the IPv6 path:

**real-time game/server state · simulation · telemetry · distributed state · specialized backend-to-backend links · LAN/datacenter research · high-frequency machine-to-machine messaging**

It is intentionally **not** trying to become TCP with a different name.

> **Development status:** DHMP is still active R&D and is not published on NuGet. The current native backend uses IANA experimental IPv6 Next Header values `253/254`, requires Linux raw-socket privileges, and does not yet claim arbitrary router/ISP/firewall traversal. Physical two-host/NIC validation, broader reachability testing, complete Internet congestion/fairness validation and independent security review remain open. The current package workflow creates temporary local packages for validation only and does not publish them.

### Try the core semantics without raw-socket privileges

The repository includes a tiny in-process sample that demonstrates fixed records, batching, local send policy and server publication without requiring Linux raw-socket capabilities:

```sh
dotnet run --project samples/DHMP.CoreLoopback/DHMP.CoreLoopback.csproj
```

That sample demonstrates the protocol semantics only. It is not a network benchmark or a reachability test.

<!-- BEGIN FULL BENCHMARK RESULTS -->
## Latest validation and benchmarks — 1 October 2026

**790 test executions passed: 395 cases on Linux and Windows.** The full hosted suite also passed 23 network acceptance checks, including deliberate loss, restart, clean shutdown and the optional full-echo profile.

[Benchmark run](https://github.com/Perry3Dnl/DHMP/actions/runs/36832616901) · [Test run](https://github.com/Perry3Dnl/DHMP/actions/runs/36832616933) · [Full results and contracts](docs/FULL_BENCHMARK_2026_10_01.md) · [Raw measurement JSON](docs/benchmark-results/2026-10-01)

Five repetitions per case on one GitHub-hosted VM: **4 vCPU, AMD EPYC 7763 64-Core Processor, .NET 10**. All units are decimal. These are memory, kernel-loopback and virtual-network measurements; physical two-machine/NIC throughput remains unmeasured.

### High-speed core and mock measurements (GB/s)

These are the fast **in-memory logical offered rates**, measured without encryption, sockets or NICs. All rows below use batch 44: 44 fixed 32-byte records, 1408 offered payload bytes per packet, on the primary AMD-hosted runner.

| In-memory benchmark | Logical offered GB/s, median (min–max) | ns/packet, median |
| --- | ---: | ---: |
| Core Latest framing ceiling | 372.534 (346.784–375.469) | 3.780 |
| Mock IP packet construction | 44.676 (44.324–44.725) | 31.516 |
| Mock IPv4 framing | 28.442 (28.374–28.696) | 49.504 |
| Mock IPv6 framing | 31.211 (20.921–31.828) | 45.112 |

![High-speed core and mock measurements](docs/assets/full-suite-high-speed.svg)

The core ceiling validates the batch boundary and observes one int32 from the newest record. The mocks construct records and observe the newest identity; IPv4/IPv6 cases add their respective header work. **They do not inspect or transmit every offered byte**, so these logical rates are not full-payload processing or network bandwidth, and the different workloads are not equivalent speed rankings.

The earlier Intel-hosted mock IP run measured **51.446 GB/s median (44.500–53.468)** at the same batch size. Its [raw results](docs/benchmark-results/2026-10-01/initial-components-kernel.json) and [run](https://github.com/Perry3Dnl/DHMP/actions/runs/36831931127) are retained separately. Different hosted CPUs prevent treating the difference as a protocol regression.

### Protected one-way versus optional full echo: memory composition

Both cases use 1200-byte records and 1160 useful application bytes. Timing includes real protection/decryption and byte checks, with no sockets. Echo also sends the entire return record and matches its confirmation. Useful bytes are counted once.

| Mode | Useful MB/s, median (min–max) | Time per send/confirmation | Approx. allocated bytes/op |
| --- | ---: | ---: | ---: |
| Protected one-way | 262.9 (255.4–263.8) | 4.41 µs | 0 |
| Protected full echo | 113.4 (96.7–114.3) | 10.23 µs | 3,472 |

![Protected one-way versus full echo in memory](docs/assets/full-suite-memory.svg)

Full echo took **2.32×** the CPU time per operation in this harness. It remains opt-in; neither mode adds automatic retransmission.

### Full echo through actual protected raw IPv6 sockets

These peers run in two namespaces on the **same VM**, not on separate physical machines. Records are encrypted and their returned content is checked. Each sample lasts at least 0.5 seconds after warm-up, with rotated concurrency order. This is measured useful completion throughput, not a physical link capacity.

| Outstanding sends | Useful MB/s, median (min–max) | Median sample p50 RTT | Median sample p95 RTT |
| ---: | ---: | ---: | ---: |
| 1 | 12.8 (5.9–13.7) | 0.085 ms | 0.112 ms |
| 8 | 24.7 (16.3–25.8) | 0.130 ms | 0.283 ms |
| 32 | 28.9 (20.6–29.8) | 0.239 ms | 0.813 ms |

![Full echo through protected raw IPv6](docs/assets/full-suite-raw-echo.svg)

### Existing ASP.NET API path

The local HTTP driver calls the website, which calls its backend through protected raw DHMP. Measurements include that whole application path. The fixture intentionally caps each API sender at **500 records/s**; the 16 KB POST spans many records and therefore reflects configured pacing, not maximum protocol bandwidth. Five samples of 40 successful requests per route, after warm-up.

| API call | Requests/s, median | Median sample p50 | Median sample p95 |
| --- | ---: | ---: | ---: |
| GET | 488.9 | 2.323 ms | 2.538 ms |
| 16 KB JSON POST + echo response | 13.6 | 73.292 ms | 74.653 ms |

### Unprotected raw kernel loopback: verified delivery

Every delivered record's identity and bytes are checked. Each run offers 200,000 records of 32 bytes, without intentional pacing. Unique received payload and loss are reported separately; duplicate records never inflate throughput. Drain waiting is excluded from the active-rate denominator.

| Records/packet | Unique received MB/s, median (min–max) | Loss %, median (min–max) |
| ---: | ---: | ---: |
| 1 | 5.1 (4.7–6.8) | 0.00 (0.00–0.00) |
| 8 | 43.4 (37.8–44.4) | 0.00 (0.00–0.00) |
| 44 | 143.1 (141.7–144.3) | 0.00 (0.00–0.00) |

**The primary run had no observed kernel loss. An earlier run on a different hosted CPU lost 17.23% at batch 44 (15.80–19.58%).** Both sets of raw measurements are retained. This shows that unpaced sender/receiver/kernel overload can occur; a loss-free run does not establish reliable delivery. The protected echo and API measurements above are different workloads and cannot be ranked against this kernel test as equivalent modes.

Core, mock IPv4/IPv6, control-codec, generation-filter, security/feedback and key-derivation timings are retained in the [complete report](docs/FULL_BENCHMARK_2026_10_01.md). Large logical offered rates from the historical-contract memory harnesses are not network bandwidth. License, routing, replay, limits, cancellation and lifetime behavior are covered by tests; long-duration soak, physical networks, fairness and independent security review remain open.
<!-- END FULL BENCHMARK RESULTS -->

## V1 data-plane rule

DHMP V1 keeps the hot data path deliberately simple:

```text
IPv6 packet
  -> DHMP headerless payload
     -> fixed record
     -> fixed record
     -> fixed record
```

There are **zero DHMP header bytes** before the first record, between records or after the last record.

The current raw IPv6 **research** path uses IANA experimental IPv6 protocol / Next Header `253` for headerless DHMP data and `254` for DHMP control/handshake packets. Neither value is a permanent or standardized DHMP assignment. RFC 4727 reserves these values for explicitly configured experiments, so the .NET raw backend now requires `enableExperimentalProtocolNumbers: true` before opening them. **Router/firewall/public-Internet reachability is not guaranteed and is a separate validation target from throughput.** See [raw IPv6 deployment and reachability](docs/DEPLOYMENT_REACHABILITY.md).

### Native deployment safety defaults

The native raw-IPv6 backend deliberately fails closed around several assumptions that are easy to miss:

- experimental Next Header `253/254` requires explicit opt-in;
- plaintext receive without an integrity/authentication decoder requires explicit `allowUnprotectedPayloads: true`;
- wildcard local address `::` requires explicit `allowWildcardLocalAddress: true`, because DHMP V1 has no port field; prefer one explicit IPv6 address per DHMP service;
- unknown paths can use `ForUnknownPath(...)`, which budgets from the IPv6 minimum MTU of 1280 bytes rather than assuming 1500;
- Linux raw-socket permission failures identify the `CAP_NET_RAW` requirement;
- `DhmpRawIpv6HostProbe.Probe(enableExperimentalProtocolNumbers: true)` can preflight Linux, IPv6 support and the ability to open both experimental raw-socket bindings before application startup. It opens and closes sockets only; it does **not** prove router/firewall/ISP reachability.

These safeguards change API defaults, not the V1 data bytes. See [deployment safety](docs/DEPLOYMENT_SAFETY.md) and [deployment/reachability](docs/DEPLOYMENT_REACHABILITY.md).

Before data packets are exchanged, endpoints can run the implemented Control V1 HELLO/ACCEPT/REJECT handshake for an already configured IPv6 peer. It verifies wire version, fixed record size and schema identity, exchanges receive capability and clamps the local send ceiling. Pmax and Latest/Sequential remain local. Peer discovery is still future work; optional PSK possession authentication is provided by the separate experimental security profile.

## Active architecture

IP packet I/O -> complete headerless DHMP payload -> fixed-contract validation ->
Latest or Sequential publication -> typed/application boundary.

- One message is one fixed-size record.
- One IP packet carries one or more whole records.
- A record never continues in another packet.
- Invalid or incomplete packets are rejected as a whole.
- Sequential publishes complete records in receive order.
- Latest publishes the final record of the received packet.
- V1 has no protocol-owned cross-packet sequence field. Optional `DhmpLatestGenerationFilter` can use an application-owned 64-bit generation inside the record to drop stale/duplicate Latest state without adding DHMP bytes.
- DHMP adds no protocol-owned delivery ACK, retransmission, replay history or hidden reliable queue. Optional application-owned confirmation tracking can reuse an application's existing ID without adding DHMP wire bytes; full-record echo remains a separate opt-in application profile.
- Buffer ownership must be explicit across asynchronous boundaries.
- Pmax is local sender policy. `RejectWindow` preserves the hard fixed-window budget; `SmoothPacing` spaces packet submissions over time. With the PSK profile, authenticated receiver overload feedback and rolling secure-path loss telemetry can adapt SmoothPacing downward and recover gradually. RTT is measured but not yet used as an independent throttle signal; this is still not a complete network congestion-control algorithm.

## What exists today

| Component | Status |
| --- | --- |
| Headerless fixed-record packet processor | Implemented |
| `DhmpWireContract` + separate send/receive policies | Implemented |
| Client packet facade | Uses an explicitly supplied direct-IP sender |
| Server packet facade | Processes already-received complete IP payloads |
| Licensing and ASP.NET host integration | Offline validation at host startup |
| Raw IPv6 kernel experiment | Experimental loopback harness |
| `DHMP.RawIpv6` Linux backend | Experimental direct data/control binding; 253/254 require explicit opt-in and are not permanent DHMP assignments |
| Compatibility/capability negotiation | Implemented HELLO/ACCEPT/REJECT; discovery still pending |
| Multi-peer server routing | Source IPv6 routing with drained removal/replacement; one registered V1 session per address |
| Cross-packet Latest freshness | Implemented opt-in application-generation filter; no extra DHMP wire bytes |
| Application-owned lightweight confirmation | Implemented opt-in tracker; application supplies the ID/confirmation schema, with zero DHMP wire bytes added |
| Full-record echo confirmation | Implemented opt-in DECO/1 application profile; no retransmission and no DHMP framing change |
| Smooth local pacing | Implemented opt-in Pmax pacing with experimental authenticated pressure adaptation |
| IPv6 path-MTU budgeting | Known-PMTU budgeting + authenticated DPLPMTUD search/confirmation + live raw/protected/client payload adaptation; periodic maintenance/raise timer still pending |
| Bounded async receive overload | Implemented: Latest replaces one pending batch; Sequential rejects when bounded queue is full |
| PSK secure packet profile | Experimental ChaCha20-Poly1305/HKDF/HMAC profile with V2 challenge/confirm setup; independent review pending |
| Authenticated receiver backpressure | Implemented experimental pressure feedback + bounded adaptive pacing |
| Secure path telemetry | Implemented rolling protected-packet loss window + authenticated RTT probe/echo |
| Full network congestion control and forward-secret/public-key security | Still to specify/build |

Removing the old transports does **not** mean a production-ready network stack already exists. The repository now intentionally favors a clean protocol boundary over temporary compatibility.

## .NET packages

All active projects target .NET 10 and use the canonical `DHMP.*` spelling.

| Project | Responsibility |
| --- | --- |
| DHMP.Protocol | Wire contract, local policies, packet processor, control codec/negotiation, budget and direct-IP sender boundary |
| DHMP.Client | Per-session sending facade |
| DHMP.Server | Per-session receiving facade and typed/buffer ownership building blocks |
| DHMP.Licensing | Offline key verification |
| DHMP.AspNetCore | Dependency injection and license/startup gating |
| DHMP.RawIpv6 | Linux raw-IPv6 data path, bounded source-address multi-peer routing, compatibility and PSK security handshakes |
| DHMP.Security | Experimental PSK packet protection, replay window and protected sender wrapper |

`AddDHMP(applicationId, licenseKey, publicVerificationKey)` configures the license gate. It does not bind an endpoint or create a hidden transport.

A `DhmpWireContract`, `DhmpSendPolicy` and an `IDhmpPacketSender` are required for a client. A server uses the same wire contract with its own `DhmpReceivePolicy`. `DhmpRawIpv6PacketSender` is the first concrete sender implementation. The optional `DHMP.Security` profile can wrap that sender and decode before the server; it adds 24 bytes per protected data packet. Creating raw IPv6 sockets on Linux requires appropriate raw-socket privileges/capabilities.

## Validation

```sh
python3 tools/check_architecture.py
dotnet test tests/DHMP.Protocol.Tests -c Release
dotnet test tests/DHMP.AspNetCore.Tests -c Release
dotnet test tests/DHMP.Licensing.Tests -c Release
dotnet test tests/DHMP.RawIpv6.Tests -c Release
dotnet test tests/DHMP.Security.Tests -c Release
```

CI guards the architecture, unit/integration tests and compilation of the direct-IP backend. Physical two-host measurements and privileged raw-socket validation remain separate. The [first two-host smoke run](docs/TWO_HOST_SMOKE.md) provides a low-rate real-backend runner and acceptance criteria. See [session shutdown](docs/SESSION_SHUTDOWN.md) for outgoing/control resource ownership.

## Measurements and history

[Benchmark scope and retained evidence](docs/BENCHMARKS.md) separates memory experiments, kernel experiments and physical network results. There is no published production direct-IP speedup claim.

Earlier stream-framing and compatibility results remain in [git history](https://github.com/Perry3Dnl/DHMP/tree/7b85bd961b12d433ed8fd3ea3b5f623dc47a20e7). They are historical evidence, not the active runtime architecture.

## Documentation

- [DHMP wire contract V1](docs/WIRE_CONTRACT_V1.md)
- [DHMP control plane V1](docs/CONTROL_PLANE_V1.md)
- [DHMP PSK security setup V2](docs/SECURITY_PSK_V2.md)
- [Bounded overload behavior](docs/OVERLOAD_BEHAVIOR.md)
- [Peer receive lifecycle and retirement](docs/PEER_LIFECYCLE.md)
- [Authenticated congestion feedback](docs/CONGESTION_FEEDBACK.md)
- [Test strategy](docs/TEST_STRATEGY.md)
- [Authoritative direct-IP direction](docs/DIRECT_TRANSPORT_DIRECTION.md)
- [Current implementation status](docs/CURRENT_STATUS.md)
- [Stable-base release acceptance plan](docs/STABLE_BASE_RELEASE.md)
- [Protocol design notes](docs/PROTOCOL_DRAFT.md)
- [Architecture and ownership](docs/ARCHITECTURE_COMPARISON.md)
- [.NET API design](docs/DOTNET10_PACKAGE_DESIGN.md)
- [Conformance](docs/CONFORMANCE.md)
- [Licensing](docs/LICENSING_DESIGN.md)
- [Authenticated Datagram PLPMTUD search](docs/DPLPMTUD.md)
- [Direct-IP implementation plan](docs/TRANSPORT_TUNING_TODO.md)

## Opt-in automatic .NET API integration

`builder.Services.AddDHMP(licenseKey)` can load configured peer/license settings and route factory-created calls for one API origin over authenticated raw DHMP IPv6, reusing the backend ASP.NET pipeline. Existing controllers and client calls can remain unchanged within the supported buffered API profile. This new experimental integration requires both endpoints, deployment configuration and Linux raw-socket privileges; it is not a published/stable NuGet release or a browser transport. See [configuration and example](docs/ASP_NET_API_INTEGRATION.md) and the explicit [DAPI/1 application schema](docs/API_APPLICATION_PROFILE_V1.md).

The [GitHub-hosted API rehearsal](docs/HOSTED_API_REHEARSAL.md) exercises real raw sockets and ASP.NET apps across two isolated virtual network environments, including loss and restart. This remains separate from the required physical two-host validation.

Optional [full-echo confirmation (DECO/1)](docs/ECHO_CONFIRMATION_PROFILE_V1.md) can verify matching received records above the unchanged V1 framing core. It is explicit opt-in and adds no automatic retransmission or application-execution guarantee.
