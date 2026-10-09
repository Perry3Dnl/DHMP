# Raw IPv6 sender investigation

Research-only executable; it does not select or replace a production transport.

Run on a privileged Linux 64-bit host with the isolated namespace configured by `.github/workflows/raw-send-probe.yml`. The workflow builds .NET 10 and runs three fresh processes. Each process warms every candidate with 32,768 packets, then runs seven samples in alternating forward/reverse candidate order for 16-byte and 1,408-byte records. Each measured sample accepts 32,768 records.

The sender stopwatch covers calls through kernel acceptance. A concurrent raw IPv6 receiver separately checks packet counts, length, and every payload byte. This is a local loopback transport measurement, not a physical network throughput, delivery guarantee, or end-to-end application latency result.

Candidates include the existing DHMP raw sender, direct managed endpoint sends, a cached serialized SocketAddress, connected managed sends, synchronous managed sends, a native nonblocking send, and native sendmmsg batches of 32 separate records. The batch candidate preserves one record per packet; it changes submission granularity.

Native probes use a pinned reusable payload and prepare descriptor arrays once. They retain a SafeSocketHandle reference for the entire experiment. They do not implement the production cancellation/disposal contract. EAGAIN retries yield and are bounded; this is not an asynchronous readiness implementation. Descriptor setup for varying records is outside these timings. A native result must not be treated as a drop-in replacement result.

The driver consumes a ValueTask once. Pending operations use AsTask for a blocking research harness; immediate and pending-at-observation counts are reported separately. The existing DHMP wrapper additionally validates state, cancellation, payload budgets, and full-packet sends and translates MessageSize errors; bare socket/native candidates do not perform all those checks.

Results are reported per candidate, so comparisons include these scope differences. The raw JSON retains all samples and receiver counts.

## Results: 2026-10-09

Measured commit: `90887edd9b82ab932b020c5beb9679b974df94b2`. [.NET 10.0.12 Linux x64 workflow run](https://github.com/Perry3Dnl/DHMP/actions/runs/37924613331). Three fresh processes, 21 samples per candidate/size. See [raw JSON](results-2026-10-09.json) and [summary](summary-2026-10-09.json).

| Candidate | 16 bytes median µs | 1408 bytes median µs |
|---|---:|---:|
| current DHMP raw sender | 4.118 | 4.145 |
| Socket.SendToAsync | 4.033 | 4.087 |
| cached SocketAddress SendToAsync | 4.056 | 4.120 |
| connected Socket.SendAsync | 3.749 | 3.979 |
| Socket.SendTo Span synchronous | 4.140 | 4.985 |
| connected Socket.Send Span synchronous | 3.564 | 3.965 |
| native send nonblocking | 3.552 | 3.830 |
| native sendmmsg 32 | 3.476 | 3.916 |

At 16 bytes, pooled median sender time falls about 9% with connected managed async sends, 14% with native individual sends, and 16% with native batches compared with the existing DHMP sender. Native individual send is almost the same as connected managed synchronous send in this small-packet test. Removing the DHMP wrapper or pre-serializing the destination offers little benefit. These percentages describe lower sender time, not higher physical network throughput.

At 1408 bytes, candidate medians vary strongly across trials, including a late-process phase where several methods slow together. Pooled medians are descriptive and are not confidence intervals or a stable production speedup estimate. Sequential same-round ratios in the summary are diagnostics, not simultaneous paired controls.

All 11,010,048 measured packets (excluding warmup) were counted by the receiver; full lengths and payload contents passed. All 5,505,024 async calls were complete successfully at observation. Native probes had no EAGAIN or partial batches. Consequently these runs provide no evidence about congested asynchronous fallback performance or cancellation during an outstanding send.

## Decision

There is evidence of a modest small-packet improvement, not an order-of-magnitude improvement from replacing the C# send call. Start a production follow-up with connected managed Socket.SendAsync for the fixed-destination sender, subject to route/PMTU/error behavior compatibility. Keep a correct cancellation-aware asynchronous fallback. Native single-send is not justified as the default by the small extra gain shown here.

A native batch backend may be worth a separate experiment through an explicit batch API, with different record buffers and queue pressure. sendmmsg submits separate packets and requires handling partial batch acceptance; syscall reduction alone did not produce a proportional time reduction here. Do not hold individual low-latency sends waiting to fill a batch.

Before selecting either candidate in production, test arbitrary payload memory lifetimes, concurrent sends/disposal, actual pending cancellation and socket readiness, PMTU changes/MessageSize translation, and the real two-host raw IPv6 workload. The current experiment intentionally does not replace the production sender.
