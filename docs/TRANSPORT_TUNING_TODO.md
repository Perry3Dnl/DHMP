# Direct-IP implementation plan

The protocol direction is now fixed enough to build the missing network path around [DHMP wire contract V1](WIRE_CONTRACT_V1.md).

## Phase 1: harden the initial raw IPv6 backend

The first Linux sender/receiver now moves complete headerless DHMP payloads through raw IPv6 sockets. Remaining hardening work:

- validate real two-host peer/path behavior;
- validate raw-socket permissions and operational diagnostics;
- validate path-MTU/error behavior;
- keep packet buffers alive through asynchronous kernel ownership;
- make receive/send buffer counts bounded;
- strengthen shutdown/cancellation under concurrent I/O;
- retain bounded rejection/drop accounting under sustained overload;
- add separately validated non-Linux backends instead of assuming portability.

Do not add a compatibility transport to make this phase easier.

## Phase 2: control-plane/session establishment

The data plane assumes both endpoints already know the same `DhmpSessionContract`.

Add a separate control-plane mechanism for:

- version agreement;
- record size;
- Sequential/Latest mode;
- maximum packet payload;
- Pmax/pacing parameters;
- peer/path association;
- application schema identity if needed;
- future security profile.

Keep negotiation off the steady-state record-processing hot path.

## Phase 3: overload and network behavior

Define explicit bounded behavior for:

- receive-buffer exhaustion;
- send-buffer exhaustion;
- loss;
- duplicates;
- reordering;
- packet bursts;
- Latest replacement;
- Sequential queue saturation.

Do not convert these problems into hidden reliability or unbounded memory growth.

## Phase 4: MTU, pacing and congestion

Add:

- real path-MTU handling;
- MTU-aware batch sizing;
- pacing rather than only a one-second budget window;
- congestion/backpressure behavior suitable for direct IP;
- explicit accounting for future security overhead.

Normal operation should not depend on IP fragmentation.

## Phase 5: security

Select and specify a reviewed direct-packet security design.

Define authentication failure handling, replay considerations, nonce/counter requirements, key/session lifecycle and exact security overhead.

Any protocol-owned wire bytes introduced by security require an explicit version/profile change. Do not silently modify the V1 headerless data layout.

## Phase 6: optimization and measurement

Only after the real backend exists:

- minimize copies around kernel I/O;
- batch packet submissions where the OS allows it;
- preinitialize fixed IPv6 header fields when the backend owns header construction;
- evaluate registered buffers, polling or kernel bypass only with measured justification;
- measure CPU, latency, loss and memory in addition to throughput;
- separate loopback, NIC and physical two-host measurements.

Historical mock/stream benchmarks stay historical. They do not define production DHMP performance.
