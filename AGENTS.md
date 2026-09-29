# DHMP project direction

The active project is DHMP directly over IP; IPv6 is the current research target.
This decision supersedes the older byte-stream/compatibility design in git history.

- Do not add or restore TCP, UDP, HTTP, QUIC, WebSocket, gRPC or TLS-stream transport implementations,
  fallback adapters or workflows. A future direction change requires an explicit user instruction.
- Process complete IP payloads containing whole fixed-size messages. Never carry partial message
  bytes between packets. Reject empty, incomplete and oversized packet payloads.
- Keep protocol framing, transport I/O, storage ownership and application execution separate.
- Batch publication and bounded memory are the baseline. No per-message allocation or
  synchronization in the framing core. Do not remove actual cross-thread ownership protection.
- Latest may discard obsolete state; Sequential preserves received arrival order within its
  bounded processing path. Neither adds delivery ACKs, retransmission or recovery.
- Native IP can lose, duplicate and reorder packets. Do not claim wire freshness, ordering,
  authentication, congestion safety or interoperability that has not been implemented.
- Keep all .NET projects and namespaces under the canonical DHMP.* spelling. Never create
  case-only alternative project paths.
- License verification happens at startup. ASP.NET integration is a host/license integration,
  not an application-transport dependency or implicit network listener.
- Read docs/DIRECT_TRANSPORT_DIRECTION.md and docs/CURRENT_STATUS.md before architectural changes.
- Run tools/check_architecture.py and the .NET tests. Keep the architecture guard in CI.
- Existing mock-IP benchmark contracts are frozen evidence. Use a separately named A/B harness
  for an optimization; do not relabel memory measurements as physical network throughput.
- Historical code/results remain in git history. Do not make historical compatibility paths
  active again simply to reproduce a previous benchmark.
