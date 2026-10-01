# DHMP core loopback quickstart

This sample demonstrates the **DHMP V1 fixed-record API semantics** without opening a socket.

It intentionally uses an in-process `IDhmpPacketSender` so that a developer can understand records, batching, local send policy and server publication without needing Linux raw-socket privileges.

Run:

```sh
dotnet run --project samples/DHMP.CoreLoopback/DHMP.CoreLoopback.csproj
```

Expected output includes records `41` and `42`.

This is **not a network benchmark and not a reachability test**. The current native transport is the experimental Linux raw-IPv6 backend documented in the repository deployment guides.
