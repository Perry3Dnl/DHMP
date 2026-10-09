# Standalone host for the included Unity Demo Arena

This small .NET 10 process runs the same `ArenaServer` and `ArenaMotor` used by the Unity Dedicated Server build. It needs no Unity installation. The Unity client connects directly using the same DHMP/DUNA record contract.

The source and independently buildable host project are also [included in the sold package's hosting sample](../../Packages/com.dhmp.networking/Samples~/DemoArena/Hosting/README.md). The `~` folder suffix keeps host-only .NET 10 code out of Unity's script importer.

Controlled same-host experiment:

```sh
dotnet build samples/DHMP.UnityArenaHost/DHMP.UnityArenaHost.csproj -c Debug
# Run these in separate terminals, each with the OS permission to open raw IPv6 sockets:
dotnet samples/DHMP.UnityArenaHost/bin/Debug/net10.0/DHMP.UnityArenaHost.dll serve --bind ::1 --experimental-plaintext --development
dotnet samples/DHMP.UnityArenaHost/bin/Debug/net10.0/DHMP.UnityArenaHost.dll connect --bind ::1 --server ::1 --experimental-plaintext --development
```

The connection command must receive a matching Welcome and an authoritative snapshot acknowledging applied input before reporting `connection_verified`. It then disconnects. It is an actual raw IPv6 connection, not an HTTP health endpoint or an in-memory substitute. A loopback result still does not establish remote Internet reachability.

For a remote lab, use real native IPv6 addresses, add `--allow-client <client-source-IPv6>` to the server and configure that same local source address in the Unity client. Incoming control/data from other sources is discarded before arena admission. Plaintext source filtering is not authentication.

A Debug host requires explicit `--development`; Release refuses that flag and verifies the configured offline application license at startup. `--experimental-plaintext` is also required until the security profile is integrated.

The manual [server deployment workflow](../../.github/workflows/deploy-unity-arena.yml) uses the existing deployment account and pinned SSH host key. It deploys a separate container, performs a local raw-socket preflight and retains the previous container for rollback. It does not route DHMP through the web proxy or change firewall rules. Remote client reachability must be tested separately.
