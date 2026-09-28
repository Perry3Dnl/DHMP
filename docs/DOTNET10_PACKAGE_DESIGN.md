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

The target is one DHMP-specific line for the normal server case.

## Package boundaries

| Package | Responsibility |
| --- | --- |
| `DHMP.Protocol` | wire contract, invariants, payload guard, Pmax budget |
| `DHMP.Client` | client connection/send API |
| `DHMP.Server` | server listener, sessions, receive/dispatch runtime |
| `DHMP.AspNetCore` | DI + hosted lifecycle + configuration |
| future `DHMP` | convenience/meta package if a single install is useful |

`DHMP.Protocol` MUST NOT depend on ASP.NET Core. Client MUST NOT depend on Server. Server MUST NOT depend on Client. `DHMP.AspNetCore` is composition only.

## Hard invariants

The .NET API must preserve the protocol rules:

1. one application message fits one negotiated DHMP payload;
2. no application fragmentation/reassembly;
3. fire-and-forget, with no DHMP ACK/replay;
4. Pmax-derived bounded send rate;
5. no unbounded reliability queue.

## API direction

Defaults should work. Advanced settings remain optional. The normal developer should not need to understand rings, slabs, SIMD, socket buffers, affinity, or benchmark-derived tuning.

The ASP.NET package will own lifecycle through DI / hosted services so developers do not manually start or stop a DHMP listener.


## Processing pipeline

DHMP deliberately separates network/package extraction from model materialization and from developer code:

```text
transport stream
      |
      v
DHMP Stream Processor
  bytes -> complete fixed-contract package
      |
      v
DHMP Package Exchange
  bounded post-processor handoff
      |
      v
DHMP Model Processor<T>
  package -> negotiated connection model T
      |
      v
application code
```

These are separate processing stages and MUST remain separate in code and benchmarks.

### DHMP Stream Processor

The Stream Processor is the network hot path. It reads the incoming transport stream, recognizes complete fixed-size packages and publishes them to the Package Exchange. It MUST NOT construct developer models, invoke application callbacks, perform business logic, or wait for slow application work.

### DHMP Package Exchange

The Package Exchange is the bounded handoff after stream/package processing. A package in this exchange is complete but is not yet the developer model. It lets the Stream Processor publish completed work and immediately continue reading the stream while another DHMP processor consumes completed packages.

The exchange MUST be bounded. It is not a reliability queue and MUST NOT silently grow to preserve delivery.

### DHMP Model Processor

The Model Processor belongs to the DHMP library but is outside the stream hot path. Because one DHMP connection has exactly one immutable negotiated model contract, its processor can be selected/prepared once at connection setup. It consumes a complete package, materializes the correct model `T`, and only then hands that model to application code.

Per-package type discovery, reflection-based dispatch, or application callbacks MUST NOT be added to the Stream Processor.

### Application processor

Developer/application processing starts after model materialization. Database work, game logic, service calls, and other application work are not DHMP Stream Processor work and must not block the stream hot path.

### Processing modes

`Latest` and `Sequential` describe how completed packages cross the post-processor handoff; neither is a delivery guarantee.

- `Latest`: older unconsumed state may be replaced by newer state.
- `Sequential`: complete received packages are offered to the Model Processor in receive order, within bounded capacity.

The concrete exchange implementations remain an optimization task and must be benchmarked rather than assumed.

## Next implementation slice

Port the retained fixed-contract receive/send path behind `DHMP.Server` and `DHMP.Client`, then make `AddDHMP()` register the server as an `IHostedService`. After correctness tests, benchmark the managed implementation before adding convenience APIs.
