# DHMP Direct Transport Direction

Status: engineering direction, 2026-09-29.

## Decision

DHMP is no longer designed on the assumption that TCP, UDP, HTTP, QUIC, WebSocket, gRPC, or another application/transport protocol must sit underneath it.

The protocol contract and Stream Processor remain transport-independent. TCP/TLS support remains useful as a compatibility/deployment transport, but it must not define DHMP's architecture or performance ceiling.

The experimental high-performance direction is a **direct packet transport for DHMP over IP**, with IPv6 currently the preferred research path. This is not yet a claim that a production DHMP/IP transport outperforms TCP; that requires real end-to-end measurements.

## Why this direction exists

Current same-generation measurements expose a large difference between DHMP's software processing capacity and the existing socket transports. These benchmark classes are deliberately not presented as equivalent network measurements:

| Benchmark scope | 32-byte Latest workload | Result |
| --- | ---: | ---: |
| Mock DHMP packet ceiling, batch 44 | logical offered payload | ~50.98 GB/s / ~1.593 B msg/s |
| Mock IPv4 framing, batch 44 | logical offered payload | median ~28.21 GB/s / ~881.6 M msg/s |
| Mock IPv6 framing, batch 44 | logical offered payload | median ~28.95 GB/s / ~904.7 M msg/s |
| Current raw TCP loopback | real loopback socket path | median 5.500 GB/s / 171.864 M msg/s |
| Current DHMP/TCP loopback | real loopback socket path | median 5.206 GB/s / 162.685 M msg/s |

The mock results do **not** prove network throughput. They show that the fixed-contract DHMP processing model has substantially more software headroom than the currently measured socket path.

IPv6 is the current direct-IP research candidate because the mock IPv6 path reached ~28.95 GB/s at batch 44 versus ~28.21 GB/s for the mock IPv4 implementation. That result is specific to these implementations and includes no kernel, driver or NIC.

## Architectural rule

Keep these layers independent:

```text
DHMP protocol contract
        |
DHMP Stream Processor
        |
DHMP packet transport interface
   +----+------------------+
   |                       |
compatibility          high-performance research
TCP / TLS              direct IP / IPv6
   |                       |
OS socket stack        minimal packet-I/O path
```

A transport may carry DHMP, but must not leak its semantics into the core contract. In particular, DHMP does not gain TCP-style delivery ACKs, retransmission history, stream head-of-line semantics, or application-message fragmentation merely because one implementation can run over TCP.

## Invariants that do not change

- Fire-and-forget remains fundamental.
- One logical application message fits completely in the negotiated fixed contract.
- No DHMP application-message fragmentation/reassembly.
- Oversize sends are rejected before entering the send path.
- `Latest` may discard stale unconsumed state.
- `Sequential` remains bounded FIFO without adding a delivery guarantee.
- The Stream Processor remains payload-opaque.
- The .NET typed boundary remains separate from the Stream Processor.
- Security must use a standard, reviewed mechanism; DHMP will not invent custom cryptography.
- TCP/TLS compatibility support is retained; it is not the definition of DHMP.

## Measurement ladder

Do not jump from an in-memory ceiling directly to a protocol performance claim. Measure every boundary independently:

1. **DHMP core / mock packet ceiling** — fixed-contract processing only.
2. **IP framing ceiling** — IPv4 and IPv6 header construction/parsing in memory.
3. **Kernel boundary** — quantify syscall, packet allocation/copy, scheduling and kernel packet-I/O costs without TCP/UDP semantics where the environment permits.
4. **Driver/NIC path** — real packet movement through the host networking stack.
5. **Two physical endpoints** — sender and receiver on separate machines with a link faster than the tested implementation.
6. **Direct DHMP/IP vs raw TCP** — same hardware, payload, message semantics, CPU allocation, duration and consumer.
7. **Security path** — benchmark the selected standardized security layer independently and end-to-end.

GitHub-hosted runners may not provide the privileges or deterministic hardware required for steps 3-5. A privileged/self-hosted Linux benchmark machine is therefore expected for the direct packet experiments.

## Immediate plan

### Phase A — freeze the current evidence

Preserve the existing mock-IP, mock-IPv4 and mock-IPv6 contracts. Do not silently change them. Keep their results classified as logical software ceilings.

### Phase B — build a packet-transport abstraction

Separate the DHMP Stream Processor from TCP-specific I/O. The interface should expose bounded packet/span ownership and preserve the existing `Latest` and `Sequential` contracts without introducing per-message allocations or synchronization.

TCP/TLS becomes one transport implementation. Direct IPv6 becomes another experimental implementation.

### Phase C — kernel-cost benchmark

Use a Linux high-performance packet-I/O path suitable for controlled benchmarking. Measure TX and RX independently first, then coupled. Record syscall/batch cost, packet rate, payload rate, CPU/core allocation, loss and queue behavior.

Do not optimize the Stream Processor around kernel limitations; optimize the packet-I/O implementation independently.

### Phase D — physical two-host benchmark

Run two dedicated machines. Avoid loopback as the headline result. Use fixed 32-byte messages and MTU-safe batches, with batch 44 / 1408-byte DHMP payload retained as a reference point. Measure offered, received and lost messages separately.

### Phase E — fair TCP comparison

Freeze one harness that can run direct DHMP/IP and raw fixed TCP under equivalent conditions. Alternate test order, run long enough to suppress JIT/scheduler noise, force identical minimal typed observation, and publish medians plus run distributions.

### Phase F — productionization

Only after the real datapath is understood:
- define direct-IP discovery/addressing/configuration;
- define capability negotiation;
- define bounded overload behavior;
- select standardized security;
- test MTU/path-MTU behavior without violating the no-fragmentation rule;
- document NAT/firewall/middlebox deployment constraints;
- retain TCP/TLS as the broadly deployable compatibility option.

## Performance target versus claim

The research target remains ambitious: investigate whether a direct DHMP datapath can approach the software ceilings and materially outperform the current raw-TCP reference for small fixed-contract Latest traffic.

The earlier ~9x/10x numbers are **engineering targets**, not public network-performance claims. A multiplier becomes a DHMP performance claim only after it is reproduced end-to-end against the same-generation raw-TCP control on equivalent physical hardware.

## Benchmark integrity

Every result must state whether it is:
- processor/in-memory logical throughput;
- mock IP framing throughput;
- kernel/loopback packet throughput;
- NIC/link throughput; or
- physical end-to-end application payload throughput.

Never combine those scopes into a single ranking as if they measured the same thing.
