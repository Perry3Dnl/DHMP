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
- Keep session/control-plane state out of the hot data packet unless a later wire version explicitly defines otherwise.
- The current raw IPv6 research binding uses Next Header 253. Treat it as experimental and configurable, not a permanent assignment.

## Architecture boundaries

- Use `DhmpSessionContract` as the explicit protocol/session agreement.
- Keep direct-IP packet I/O, session establishment, packet processing, storage ownership and application execution separate.
- Batch publication and bounded memory are the baseline.
- No per-message allocation or synchronization in the framing core.
- Do not remove real cross-thread ownership protection merely to improve a benchmark.
- Latest may discard obsolete state; Sequential preserves received arrival order within its bounded processing path.
- Neither mode adds delivery ACKs, retransmission or recovery.
- Native IP can lose, duplicate and reorder packets. Do not claim freshness, ordering, authentication, congestion safety or interoperability that has not been implemented.

## Repository rules

- Keep all .NET projects and namespaces under the canonical `DHMP.*` spelling.
- Never create case-only alternative project paths.
- License verification happens at startup. ASP.NET integration is host/license integration, not a DHMP data transport.
- Keep `tools/check_architecture.py` in CI.
- Existing benchmark contracts are historical evidence for their exact scope. Do not relabel memory or loopback results as physical network throughput.
- Historical compatibility code remains in git history. Do not make it active again merely to reproduce an old result.
