# DHMP project direction

The active project is DHMP directly over IP; IPv6 is the current implementation target.

Read `docs/DIRECT_TRANSPORT_DIRECTION.md`, `docs/WIRE_CONTRACT_V1.md` and `docs/CURRENT_STATUS.md` before architectural changes.

## Non-negotiable active direction

- Do not add or restore TCP, UDP, HTTP, QUIC, WebSocket, gRPC or TLS-stream DHMP data-plane implementations, fallback adapters or workflows unless the project direction is explicitly changed.
- DHMP V1 data packets are headerless. The DHMP payload contains application records only.
- Do not silently add protocol-owned packet headers, record headers, separators, trailers, sequence fields or timestamps to V1.
- Any future protocol-owned wire metadata requires an explicit versioned wire-contract change.
- Process complete IP payloads containing whole fixed-size records.
- Never carry partial record bytes between packets.
- Reject empty, incomplete and oversized packet payloads.
- Keep control-plane state out of the hot data packet unless a later wire version explicitly defines otherwise.
- Control V1 may validate compatibility/capabilities for a configured peer but must never be described as authentication.
- The separate PSK security profile may authenticate possession of the configured shared key. Do not describe it as forward-secret or independently reviewed.
- Raw-IPv6 PSK setup uses DHMS V2 challenge/confirm. Do not restore V1 OFFER/ACCEPT setup as a fallback: captured OFFERs can recreate keys with reset counters. Preserve both-nonce transcript binding and fresh responder challenges before session activation.
- Remote pacing feedback must be authenticated and session-bound before it can change `DhmpAdaptiveRateController`; never add an unauthenticated remote throttle path.
- Do not add per-data-packet ACK/retransmission just to estimate loss. The secure profile may use its existing authenticated packet counter as a rolling loss signal. Treat RTT as observability until a measured policy is justified.
- The current raw IPv6 experimental profile reserves protocol / Next Header 253 for headerless data and 254 for control. Treat both as experimental, not permanent assignments.

## Architecture boundaries

- Use `DhmpWireContract` for protocol compatibility. Keep `DhmpSendPolicy` and `DhmpReceivePolicy` local to endpoints; do not merge them back into wire identity.
- Keep direct-IP packet I/O, session establishment, peer routing, packet protection, packet processing, storage ownership and application execution separate.
- V1 multi-peer routing may key on source IPv6 address. Do not add a hidden per-packet session ID merely to support multiple sessions from the same source address.
- Base V1 stays headerless. Security overhead belongs only to an explicitly selected security profile and must be accounted for separately.
- Batch publication and bounded memory are the baseline.
- No per-message allocation or synchronization in the framing core.
- Do not remove real cross-thread ownership protection merely to improve a benchmark.
- Latest may discard obsolete state; Sequential preserves received arrival order within its bounded processing path.
- Cross-packet freshness in V1 may only come from explicitly application-owned record fields/profiles such as `DhmpLatestGenerationFilter`; do not add hidden DHMP sequence bytes.
- `SmoothPacing` alone is local pacing. Authenticated `DHMF` receiver-overload feedback may adapt it, but do not call the current profile complete network congestion control until loss/RTT/ECN/fairness behavior is implemented.
- Neither mode adds delivery ACKs, retransmission or recovery.
- Native IP can lose, duplicate and reorder packets. Do not claim protocol-owned freshness, ordering, authentication, congestion safety or interoperability that has not been implemented.
- Treat `DhmpIpv6PathBudget` as calculation from a known PMTU, not as dynamic PMTU discovery. Normal DHMP operation should not intentionally rely on IPv6 fragmentation.

## Repository rules

- Keep all .NET projects and namespaces under the canonical `DHMP.*` spelling.
- Never create case-only alternative project paths.
- License verification happens at startup. ASP.NET integration is host/license integration, not a DHMP data transport.
- Keep `tools/check_architecture.py` in CI.
- Existing benchmark contracts are historical evidence for their exact scope. Do not relabel memory or loopback results as physical network throughput.
- Historical compatibility code remains in git history. Do not make it active again merely to reproduce an old result.
