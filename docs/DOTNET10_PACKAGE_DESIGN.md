# .NET 10 package design

## Goal

The public developer experience must stay small while client, server, protocol, and ASP.NET integration remain physically separable.

### Intended ASP.NET Core server setup

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDHMP();

var app = builder.Build();
app.Run();
```

## Package boundaries

| Package | Responsibility |
| --- | --- |
| `DHMP.Protocol` | wire contract, invariants, payload guard, Pmax budget |
| `DHMP.Client` | client connection/send API |
| `DHMP.Server` | server listener, sessions, receive/dispatch runtime |
| `DHMP.AspNetCore` | DI + hosted lifecycle + configuration |
| future `DHMP` | convenience/meta package if useful |

`DHMP.Protocol` MUST NOT depend on ASP.NET Core. Client MUST NOT depend on Server. Server MUST NOT depend on Client. `DHMP.AspNetCore` is composition only.

## Hard invariants

1. one application message fits one negotiated DHMP payload;
2. no application-message fragmentation/reassembly;
3. fire-and-forget, with no DHMP ACK/replay;
4. Pmax-derived bounded send rate;
5. no unbounded reliability queue.

TCP/TLS may split a fixed DHMP package across receive buffers. Reconstructing that one fixed package is transport-stream boundary handling, not DHMP application fragmentation.

## Scope boundary

The .NET NuGet/framework ends at the efficient transition from a complete DHMP package span to a usable typed data view. What an application does with that data afterwards is explicitly outside DHMP performance scope.

DHMP benchmarks therefore MUST NOT include game logic, callbacks, databases, application worker scheduling, copying into developer-owned state, ECS updates, or other downstream processing when reporting protocol/framework throughput. Those operations may be substantially slower and are the developer/application's responsibility.

The framework may provide a safe API for consuming the typed view, but application execution time is not a DHMP throughput metric.

## Receive architecture

```text
TCP / TLS receive storage
        |
        v
DHMP Stream Processor
  fixed-size framing only
  payload remains opaque
        |
        | borrowed contiguous package span
        | (base, count, package size)
        v
.NET DHMP boundary
        |
        | zero-copy ReadOnlySpan<T>
        v
Latest or Sequential typed view
        |
        +---- DHMP/.NET PERFORMANCE SCOPE ENDS HERE ---->
        |
        v
application-owned processing
```

### DHMP Stream Processor

The Stream Processor is protocol-side framing. It determines complete fixed-contract packages in arbitrary TCP/TLS chunks and handles at most the partial package at a chunk boundary.

It MUST NOT inspect payload fields, calculate payload checksums, construct C# models, choose application workers, invoke callbacks, or perform business logic.

For a contiguous run of complete packages, the preferred output is one batch/span descriptor rather than one descriptor per package. Package boundaries inside the span are implicit from the negotiated fixed package size.

### Borrowed span boundary

The fast path is a **borrowed span**, not a payload copy. Conceptually the handoff is:

```text
{ base address / receive region, package count, fixed package size }
```

The receiver owns the underlying storage. The consumer may use the span only during its valid lease/lifetime. The receive region MUST NOT be reused or overwritten until the consumer releases it.

This ownership rule is the central coupling constraint between the protocol processor and the .NET implementation. It must be solved without making the Stream Processor perform model materialization or worker dispatch.

A span descriptor is metadata; it does not transfer ownership of each package and does not imply a reliability queue.

### Partial package

When a transport chunk ends inside one fixed package, only those partial bytes are copied into the connection's fixed-size carry storage. Once the next chunk completes that package, it can be exposed as one complete package. Complete packages MUST NOT be copied merely because another package crossed a transport boundary.

### .NET model boundary

For compatible unmanaged fixed-layout contracts, .NET should expose complete package storage as a zero-copy `ReadOnlySpan<T>` (or an equivalent lifetime-safe internal view). It MUST NOT copy every package into a second `T[]` merely to materialize the model.

Contract validation is established once for the connection; per-package reflection or schema discovery is not part of this path.

### Latest

`Latest` may discard stale unconsumed packages. When a receive span contains multiple packages, the framework can select the newest complete package directly. It does not need to enqueue every preceding package first.

The underlying receive storage still cannot be reused while the selected model/span is being consumed.

### Sequential

`Sequential` offers every complete package in receive order. It remains bounded and fire-and-forget: a slow consumer MUST NOT cause an unbounded reliability queue.

The preferred unit of handoff is a span/batch. If bounded receive storage is exhausted, the configured overflow policy must act explicitly rather than silently allocating an ever-growing queue.

## Ownership implementation direction

Use a small bounded pool/ring of receive regions. Each region moves through a simple lifecycle:

```text
FREE -> RECEIVING -> BORROWED -> FREE
```

Only the receive owner writes a `FREE/RECEIVING` region. A published `BORROWED` region is immutable. Releasing the borrow returns the region to `FREE`.

Do not add per-package reference counting. Ownership should be tracked per receive region/span so synchronization cost scales with publications, not message count.

For `Latest`, a newer unconsumed region may replace an older one according to the mode's discard semantics, provided a region actively borrowed by a consumer is never overwritten.

For `Sequential`, region publication is FIFO and bounded.

## Benchmark evidence and interpretation

The protocol framing benchmark shows that fixed framing itself is far above the original 50 GB/s processor target on the current GitHub runner; fragmented Sequential framing remained in the hundreds of logical GB/s. These are logical framing-capacity measurements, not network or memory bandwidth.

The independent .NET boundary benchmark measured roughly 52 GB/s for span acceptance / Latest span selection, about 15 GB/s while actually reading model fields from every package through a zero-copy model view, and about 5.7 GB/s when copying into a second model buffer. These are benchmark-family measurements, not end-to-end network throughput.

Therefore the implementation direction is:
- keep payload opaque in the Stream Processor;
- publish batches/spans, not per-package work;
- preserve zero-copy across the .NET boundary where lifetime permits;
- avoid a mandatory second model buffer;
- benchmark region ownership/publication before choosing the concrete ring implementation.

## Next implementation slice

Implement and benchmark the bounded receive-region ownership boundary first:

1. preallocated receive regions;
2. one publication per complete package span;
3. zero-copy managed model view while borrowed;
4. explicit release back to the receive pool;
5. separate `Latest` and `Sequential` overflow semantics;
6. deliberately fragmented receive chunks in correctness/performance tests.

Worker scheduling and application callback execution are outside the DHMP/.NET throughput benchmark scope and must be benchmarked separately by applications that choose to use them.
