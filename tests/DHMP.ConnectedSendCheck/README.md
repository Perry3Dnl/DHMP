# Connected raw sender validation

Run via `.github/workflows/connected-send-check.yml` on a privileged Linux runner. This is an isolated test executable, not a selectable transport. The endpoint baseline copies the previous sender wrapper at d722667c; both wrappers use the current raw socket opener so the per-packet destination-call change is isolated.

The loopback fixture checks sliced pooled payloads, cancellation before send, silence for rejected/cancelled packets, dynamic path-budget updates/fallback, concurrent record contents/counts, repeated disposal and descriptor cleanup after failed Connect. It then measures complete endpoint and connected wrappers for 16 and 1408 bytes. Three fresh processes warm each candidate with 32768 packets and take seven samples in alternating order. Every measured sample verifies receiver counts and every payload byte.

The veth fixture pins a 1280-byte route MTU, checks actual MessageSize translation and acceptance of a smaller record, then raises the route/interface MTU and verifies the same connected socket can send a larger record without reopening.

Rate-limited veth egress exercises real raw buffer exhaustion. On the measured Linux host both wrappers report ENOBUFS, which is propagated as SocketException; this is not successful delivery or a retry mechanism. The fixture must observe the error to pass.

A separate local Unix datagram fixture forces genuinely outstanding connected Socket.SendAsync operations and checks cancellation-token preservation and socket disposal. This validates the shared managed socket API lifecycle. It does **not** prove pending raw IPv6 operations occurred. The raw sender retains its awaited cancellation-aware operation and full-packet checks.

## PMTU bug found during validation

The original opener set IPV6_DONTFRAG, but oversized 253 packets were still accepted on the 1280-byte path, for both before/after wrappers. Linux's [ip6_output.c](https://linux.googlesource.com/linux/kernel/git/viro/vfs/+/refs/heads/uaccess.s390/net/ipv6/ip6_output.c) only applies its dontfrag shortcut to selected IP protocol values, which exclude experimental bindings 253/254. [ip6_sk_ignore_df](https://code.googlesource.com/linux/torvalds/linux/+/21e4675d9305f6ccd20b95d943882d607c8ae288/include/net/ip6_route.h) also controls whether the path MTU may be exceeded. The opener now sets IPV6_MTU_DISCOVER=IPV6_PMTUDISC_DO in addition to DONTFRAG, enforcing the existing no-fragmentation contract for both bindings. The [Linux UAPI header](https://kernel.googlesource.com/pub/scm/linux/kernel/git/stable/linux-stable/+/53f1d9afb4c85c4d6a107420188d84ddf76ebbc0/include/uapi/linux/in6.h) defines the constants.

## Recorded measurement

Measured commit `e0a175250cd4888230ab098c4b2189e6e4149885`, [workflow run](https://github.com/Perry3Dnl/DHMP/actions/runs/37926732326). [Full sample data](results-2026-10-09.json).

| Record bytes | Endpoint median µs | Connected median µs | Lower median sender time |
|---|---:|---:|---:|
| 16 | 3.487 | 3.421 | 1.9% |
| 1408 | 3.492 | 3.370 | 3.5% |

All 2,752,512 measured packets were received and passed full payload verification. All measured async calls were complete successfully at observation. Timing covers kernel acceptance on local raw IPv6 loopback, not physical-NIC throughput or end-to-end delivery. Timing varied between processes and some individual process/size medians showed no improvement. This supports a modest possible gain, not a guaranteed 9% or larger production improvement. Raw pressure, PMTU and managed pending lifecycle checks passed separately.
