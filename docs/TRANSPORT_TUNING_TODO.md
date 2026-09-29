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

## Phase 2: harden and extend Control V1

The initial configured-peer control plane is implemented:

- separate experimental protocol 254;
- fixed 32-byte HELLO/ACCEPT/REJECT packet;
- wire version and record-size compatibility;
- schema UUID compatibility;
- remote receive-capability exchange;
- correlation matching;
- local outbound ceiling clamped to remote capability.

Remaining work:

- peer discovery;
- authenticated peer identity;
- secure key establishment;
- retry/timeout policy for lost control packets;
- routing beyond one session per source IPv6 address;
- capability extension/versioning if future profiles need it.

Keep negotiation off the steady-state record-processing hot path.

## Multi-peer routing baseline

The Linux backend now includes bounded multi-peer routing:

- explicit maximum peer count;
- source IPv6 address as the V1 routing key;
- one wire/receive policy and optional security decoder per peer;
- unknown-peer drop accounting;
- malformed/protection rejection accounting;
- decrypted scratch clearing after publication.

V1 still cannot distinguish multiple sessions from the same source IPv6 address without adding a protocol-owned routing identifier, so that remains a future version/profile question rather than a hidden data-plane change.

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

Implemented baseline:

- known IPv6 PMTU -> raw protocol payload calculation;
- 40-byte IPv6 base-header accounting plus optional extension-header budget;
- security/envelope subtraction and whole-record alignment;
- `SocketError.MessageSize` surfaced as `DhmpPathMtuException`;
- opt-in smooth Pmax pacing.

Still to add:

- dynamic PMTU discovery/ICMPv6 feedback handling;
- automatic downward/upward path-budget adaptation;
- congestion/backpressure behavior suitable for direct IP;
- real-path validation that fragmentation is avoided.

Normal operation should not depend on IP fragmentation.

## Phase 5: harden the PSK security profile

The first explicit packet security profile is implemented:

- HMAC-SHA256 authenticated PSK setup on control protocol 254;
- fresh session identifier;
- HKDF-SHA256 directional key/nonce-prefix derivation;
- ChaCha20-Poly1305 data protection;
- 64-bit per-direction packet counters;
- bounded 64-packet replay window;
- 24-byte explicit protected-data overhead;
- decrypted receive-buffer clearing after synchronous publication.

Remaining security work:

- independent security review;
- PSK rotation/lifecycle and multi-key lookup;
- denial-of-service analysis;
- secure profile performance measurements;
- decide whether forward secrecy/public-key identity is required;
- physical two-host validation.

Base V1 remains headerless when the security profile is not selected. Do not silently insert the security envelope into plain V1.

## Phase 6: optimization and measurement

Only after the real backend exists:

- minimize copies around kernel I/O;
- batch packet submissions where the OS allows it;
- preinitialize fixed IPv6 header fields when the backend owns header construction;
- evaluate registered buffers, polling or kernel bypass only with measured justification;
- measure CPU, latency, loss and memory in addition to throughput;
- separate loopback, NIC and physical two-host measurements.

Historical mock/stream benchmarks stay historical. They do not define production DHMP performance.
