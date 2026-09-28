# .NET 10 package design

## Goal

The public developer experience must stay small while client, server, protocol, and ASP.NET integration remain physically separable.

### Intended ASP.NET Core server setup

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDhmp();

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

## Next implementation slice

Port the retained fixed-contract receive/send path behind `DHMP.Server` and `DHMP.Client`, then make `AddDhmp()` register the server as an `IHostedService`. After correctness tests, benchmark the managed implementation before adding convenience APIs.
