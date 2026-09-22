# Current development status

Updated: 2026-09-22

## What has been established

The prototype has progressed from a fixed-layout socket experiment into a broader DHMP transport design.

### Fixed-contract/headerless hot path

The connection handshake establishes the schema identity and fixed logical frame size. Steady-state DHMP traffic therefore does not require a per-frame DHMP header or length field.

### Arbitrary negotiated frame sizes

The experiment has been exercised at 32, 64, 128, 256, 512 and 1024-byte logical frames.

### Every and Latest receive policies

The streaming tests demonstrated the original DHMP state model: the producer does not wait for an application reply, and Latest can intentionally conflate stale queued state while preserving the newest complete frame.

### Plain and secure transport

Both DHMP and DHMPS are treated as first-class concepts.

DHMPS is DHMP carried over standard TLS. TLS is a transport/security wrapper rather than metadata inserted into every logical DHMP application frame.

### Two delivery semantics

The current model is:

- **Unconfirmed** — no DHMP-level proof/replay requirement.
- **Verified** — same no-wait/no-ACK hot data stream, with retained history and resume/replay at verification or reconnect boundaries.

The earlier experiment using a cumulative ACK every 64 frames was rejected because it changed the intended DHMP hot-path model. The current experiment has **zero periodic ACKs**.

## Benchmark conclusions so far

### DHMP versus lean binary TCP

A full localhost sequential request/reply comparison used 10,000 warmups, 100,000 iterations and 5 passes.

DHMP and a raw TCP + 4-byte-length binary baseline are close, as expected because both are very lean. DHMP's measured median-rate lead over that baseline varied by frame size and was effectively a tie at some sizes.

This is an important result: DHMP should not be presented as magically faster than all binary TCP. Its value is the combination of fixed-contract framing, low allocations, streaming semantics, Latest behavior and eventually easy developer integration.

### DHMP/DHMPS versus HTTP/HTTPS

A quick persistent-localhost comparison using identical opaque request/response bodies showed substantially higher steady-state request/response rates for DHMP than HTTP and for DHMPS than HTTPS in that specific sequential workload.

This is promising for controlled backend-to-backend communication, but it is not by itself evidence that DHMP is a universal HTTP replacement. HTTP/2 and gRPC also provide multiplexing, routing/tooling and higher-level ecosystem features that the current benchmark does not reproduce.


### Reusable-slab data-pump model

A newer benchmark now matches the original DHMP model more closely than the frame-at-a-time socket experiments.

The sender and receiver use reusable 256 KiB slabs so socket-operation boundaries do not have to match logical frame boundaries. In **Latest** mode, obsolete complete frames can be skipped by fixed-size arithmetic while only the newest complete state is published.

The 32-byte quick run demonstrated the mechanism clearly: DHMP Latest received 8,192 logical frames per socket read and skipped 99.9878% of stale states instead of dispatching each one individually.

The exact burst throughput from this run is not yet treated as a sustained performance claim because the byte-budgeted timed regions can be only a few milliseconds long. The next performance step is a fixed-duration multi-second run; the important result so far is that DHMP can amortize one socket operation across thousands of logical frames while retaining constant-size reusable memory.

### Processor-path Gen-2 search

The transport/slab work has progressed far enough that processor/publication behavior is now a primary optimization target.

A native Linux/C architecture lab is used for fast algorithm screening. The retained test format uses two runs per point at 16 B, 32 B, 256 B and 1 KiB. `Every` paths are measured with zero simulated consumer work; `Latest` paths use 10 µs of simulated work.

The current result is not one universal winner. Tiny-frame, maximum-ingest, and maximum-publication paths differ. This points toward an implementation-level adaptive selector after handshake/contract negotiation, while the wire semantics remain simply `Every` or `Latest`.

The Gen-2 native results are algorithm-comparison data only; they should not be compared numerically with the Windows/.NET benchmarks.

### No-ACK Verified recovery experiment

Normal development run:

- 2,000 warmup iterations
- 20,000 application messages
- 3 passes
- frame sizes: 32, 256, 1024 bytes
- 3 hard connection interruptions per faulted pass
- 8 deliberately uncommitted frames at each interruption
- zero periodic ACKs

The sender continues streaming until the dead connection becomes visible, so the real uncertain tail can be larger than the deliberately uncommitted eight frames.

Observed median faulted behavior:

| Frame | Unconfirmed missing | Verified missing | Verified replayed | Verified recovery total |
| ---: | ---: | ---: | ---: | ---: |
| 32 B | 43 | 0 | 37 | 1.0343 ms |
| 256 B | 37 | 0 | 37 | 1.3325 ms |
| 1024 B | 35 | 0 | 37 | 0.9190 ms |

Verified recovered all 20,000 logical messages in all three cases without periodic hot-path acknowledgement traffic.

The exact sub-millisecond recovery numbers are localhost benchmark measurements and should not be generalized to real networks.

## Important discovery: bounded Verified mode

A clean Verified connection cannot retain every frame forever.

At 20,000 messages, retaining the full unverified stream means:

- 32-byte frames: 640,000 bytes
- 256-byte frames: 5,120,000 bytes
- 1024-byte frames: 20,480,000 bytes

Therefore Verified requires asynchronous verification/checkpoint boundaries.

The sender should continue sending while a small control exchange confirms an accepted position. Once confirmed, old retained history can be discarded.

This is currently the next major protocol experiment.

## Next work

1. Port the strongest Gen-2 processor candidates back into the .NET prototype and re-run them under the same Windows/.NET benchmark harness.
2. Implement bounded Verified history with configurable/asynchronous checkpoints.
3. Benchmark several checkpoint policies (time-based, byte-based and explicit application verification).
4. Measure control bytes, retained memory, clean-stream throughput and failure recovery separately.
5. Exercise the same model over DHMPS/TLS.
6. Separate protocol specification from the .NET implementation.
7. Rename remaining experimental `FixedWire` namespaces/projects only after the protocol model is stable.
8. Design higher-level .NET ergonomics (DI, typed clients, endpoint negotiation and ASP.NET-style hosting) without contaminating the DHMP data frame.
9. Continue native streaming/concurrent comparisons for gRPC/HTTP2 rather than relying only on sequential request/reply benchmarks.

## Positioning

DHMP is no longer being treated only as an HTTP/HTTPS replacement experiment.

Potential use cases include:

- service-to-service communication;
- current-state distribution;
- device/controller communication;
- game/server state;
- telemetry;
- cache synchronization;
- replication/change feeds;
- log shipping;
- edge/backend synchronization.

The protocol should remain small. Higher-level features should be layered on top rather than adding repeated metadata to every application frame.
