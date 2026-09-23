# DHMP benchmark fairness policy

DHMP benchmark results are only useful if the comparison is reproducible and the compared implementations are described exactly.

## Non-negotiable rules

1. **One frozen harness per cross-protocol generation.** If common benchmark code, socket configuration, batching, CPU placement, warmup, timing, compiler flags, consumer behavior, or validation changes, the generation changes.
2. **Rerun every row when the common harness changes.** Never carry an old HTTP/WebSocket/TCP result into a new DHMP generation.
3. **Matched A/B for DHMP optimizations.** Change one intended variable and keep the rest of the experiment fixed.
4. **Never call a framing microbenchmark a full protocol stack.** A native HTTP/2 DATA-frame parser is not Kestrel/HTTP/2. A gRPC envelope parser is not a production gRPC server.
5. **Show ranges, not only the median.** Shared localhost results can vary materially.
6. **Keep security equal.** Plain DHMP must not be compared with HTTPS/TLS as if encryption were free. DHMPS/TLS should be compared with TLS-enabled baselines.
7. **Keep semantics equal.** `Latest` may skip obsolete application states; `Every` may not. Do not compare dropped work with preserved work without saying so.
8. **Keep payload and workload equal.** Same logical payload, message count, transform, validation, and application work.
9. **Keep transport settings equal unless the experiment is explicitly a maximum-tuned implementation comparison.**
10. **Publish source, raw results, summary, and methodology.** If one of these is missing, the result is not a headline benchmark.
11. **Do not mix processor-only throughput with network throughput.**
12. **Do not add independent optimization percentages together.**

## Current frozen native framing generation

The current cross-protocol framing generation is **showcase v6**.

Profile:

- 32-byte logical records;
- localhost Linux/native C;
- plain (non-TLS) transport rows;
- 12 KiB receive workspace;
- 256 KiB sender batch;
- zero artificial consumer hold;
- 500 ms warmup;
- 1.2 s measured interval;
- five rotated runs;
- CPU pinning when available;
- payload/framing validation.

All rows in the headline table come from this generation.

## What the v4 rows actually are

| README label | Actual implementation under test | Full production stack? |
| --- | --- | --- |
| DHMP Adaptive / Latest | Native C DHMP fixed-contract/Ring-3 Latest transport path over TCP | DHMP native architecture path, not .NET application stack |
| Raw TCP / fixed frame | Native C TCP byte stream interpreted as fixed 32-byte records | No higher protocol |
| TCP / varint length | Native C TCP + one-byte length prefix | No |
| TCP / 4-byte length | Native C TCP + 4-byte big-endian length prefix | No |
| WebSocket / binary framing | Native C WebSocket binary framing shape, unmasked server-to-client form | **No** |
| HTTP/1.1 / chunk framing | Native C HTTP/1.1 chunk-framing/parser hot path | **No** |
| HTTP/2 / DATA framing | Native C HTTP/2 DATA-frame framing hot path | **No** |
| gRPC / HTTP/2 framing | Native C gRPC message envelope inside an HTTP/2 DATA-frame shape | **No** |
| MQTT QoS0 / PUBLISH | Native C QoS0 PUBLISH framing with fixed one-byte topic | **No** |
| NATS / PUB framing | Native C NATS PUB framing with fixed subject | **No** |
| UDP / batched datagrams | Native C batched UDP datagrams | No application protocol |

These rows answer: **how much hot-path framing/parsing work is present in this one native harness?**

They do **not** answer: **is DHMP faster than a complete ASP.NET Core HTTP/2 server, grpc-dotnet service, MQTT broker, NATS server, browser WebSocket stack, or other production framework?**

## Full-stack comparison status

These comparisons need their own frozen harness and should remain marked unmeasured until they are actually run.

| Target | Fair reference implementation | Status |
| --- | --- | --- |
| Raw TCP | .NET Socket / NetworkStream fixed-record implementation | Not yet rerun against current Adaptive .NET engine |
| HTTP/1.1 | ASP.NET Core/Kestrel client-server path | Not yet benchmarked against current Adaptive engine |
| HTTP/2 | ASP.NET Core/Kestrel HTTP/2 path | Not yet benchmarked against current Adaptive engine |
| gRPC | grpc-dotnet / ASP.NET Core | Not yet benchmarked against current Adaptive engine |
| WebSocket | System.Net.WebSockets / ASP.NET Core | Not yet benchmarked against current Adaptive engine |
| MQTT | Real .NET MQTT client/broker implementation selected and version-pinned | Not yet benchmarked |
| NATS | Official/version-pinned .NET client + NATS server | Not yet benchmarked |
| HTTPS / secure HTTP2 | TLS enabled on both sides | Not yet rerun against current DHMPS engine |
| DHMPS | Current DHMP contract over standard TLS | Needs fresh same-generation secure comparison |

## Publication rule

The root README may show:

- **native framing comparison** when labelled exactly as such;
- **integrated DHMP A/B results** when the baseline is the matched old DHMP path;
- **processor microbenchmarks** when clearly labelled processor-only;
- **full-stack protocol comparison** only after real full-stack implementations have been run together under one frozen harness.

If DHMP loses a fair benchmark, publish the loss.


## v6 normalization

The retained native framing comparison now uses **one 32-byte logical message per framing unit**. This replaced the older mixed-granularity setup where some framing paths amortized headers across large batches.

See [V6_FAIR_PER_MESSAGE.md](../benchmarks/native-showcase/V6_FAIR_PER_MESSAGE.md).
