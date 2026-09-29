# Direct-IP implementation plan

## First: establish a real packet backend

- Implement the explicit packet sender/receiver boundary for the target OS.
- Configure session/peer association and exact payload/header delivery behavior.
- Define bounded receive storage, buffer release and shutdown.
- Validate packet lengths and malformed inputs before any state/publication changes.
- Define and test loss, duplication, reordering and Latest freshness rules.
- Negotiate/configure path MTU including all IP and security overhead; never split a message.

## Then: measure and tune

- Batch records within a packet and packet submissions per kernel boundary.
- Preinitialize fixed IP-header fields where the backend owns header construction.
- Reuse buffers and minimize copies without violating ownership.
- Evaluate registered buffers, polling and kernel bypass only with measurable justification.
- Compare kernel-loopback, NIC and physical two-host measurements independently.
- Measure latency and CPU as well as throughput and memory.
- Develop pacing/congestion behavior beyond the prototype fixed-window send budget.

## Security and release gates

Select a standard reviewed direct-packet security design. Validate authentication, resource
limits and hostile inputs before production release. No custom cryptography or implicit
stream-security fallback is part of this plan.
