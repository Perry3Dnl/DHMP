# DHMP Networking 1 — Unity preview

The package includes the **complete Demo Arena**: a third-person player, walking and jumping, shared movement simulation, client prediction and reconciliation, remote-player interpolation, a connection screen, and the same self-hostable server implementation used by the example.

Version `0.1.0-preview.1` is an implementation preview, not a sale-ready release. The publisher's arena endpoint is deliberately empty until an actual server has been provisioned and tested. This repository change does not deploy that server.

## Open the included demo

1. Use a Unity 6 desktop project. Install this folder using **Window > Package Manager > + > Install package from disk**, selecting `package.json`, or install the generated `.tgz` using **Install package from tarball**.
2. On **DHMP Networking 1**, import the **Demo Arena** sample. The sample is included inside the package under `Samples~/DemoArena`.
3. Set **Project Settings > Player > Active Input Handling** to **Input System Package (New)** or **Both**, then restart Unity if prompted.
4. Open the imported `DemoArena.unity` scene, or choose **DHMP > Open imported Demo Arena**.
5. Select the imported `DhmpDemoSettings.asset`. Set your explicit local IPv6 address and deliberately enable the experimental IPv6 / unprotected demo-payload options for this controlled test. `::1` is only for a same-host loopback experiment.
6. Press Play, enter a player name, and connect to your own server's IPv6 address. **Join the DHMP demo server** becomes available when `demoServerIpv6` is configured with a real endpoint.

Controls: **WASD** movement, **mouse** camera, **Space** jump, **Esc** cursor/menu. Guest names are display names, not authenticated accounts.

The desktop raw-socket process needs the relevant operating-system permission: Linux `CAP_NET_RAW`; Windows elevation. Permission failures and missing settings are shown in the connection screen. Mobile, consoles, browsers, and macOS are not implemented targets of this preview.

## Build and host

Install Unity's Windows build support and Linux Dedicated Server build support as needed. Use **DHMP > Build Demo > Windows Client (Development)** or **Linux Server (Development)**. A build compiles the imported sample into `Builds/DHMP-Client` or `Builds/DHMP-Server`. The menu checks configuration before building and does not change your project's input settings.

The sample includes [hosting instructions](Samples~/DemoArena/Hosting/README.md), a launch script and a systemd service example. Buyers can run the same example on their own infrastructure. Only the demonstration endpoint is publisher-operated.

## Architecture and current boundary

- The reusable `DHMP.Unity.Core` assembly targets .NET Standard 2.1, independently of the existing .NET 10 binaries. Tests compare its portable Control V1 codec and license verification with the reference implementation.
- Data stays directly on experimental IPv6 Next Header **253**, with Control V1 separately on **254**. Each data packet contains only complete 128-byte **DUNA/1 application records**. Entity identifiers, input acknowledgements and session tokens belong to that explicitly documented application schema, not a new DHMP header.
- The demo simulates at 30 Hz and publishes snapshots at 10 Hz. Each input packet may carry the latest three unacknowledged application inputs, within the peer's advertised receive ceiling. The server advances at most one simulation step per server tick. It ignores client-supplied positions.
- The authoritative motor and arena geometry are shared between client and server. The sample motor is intentionally an arena character controller, not a replacement for arbitrary Unity Rigidbody physics.
- Memory/admission are bounded: maximum 32 peers, 32 queued commands per peer, 128 pending predicted inputs, nine records per packet and bounded receive polling. Default capacity is 16, not a benchmarked concurrency promise.
- One active player per source IPv6 address. Use distinct IPv6 addresses for multiple clients. Separate DHMP applications need separate service addresses: the wire has no transport ports.
- This preview explicitly opts into **plaintext lab traffic**. Control V1 checks compatibility; observable session tokens reject stale application sessions but do not authenticate users or encrypt traffic. It does not yet integrate the existing DHMS V2 security profile, automatic Internet reachability or congestion feedback. A public service launch remains gated on those integrations and operational testing.

See the included [DUNA/1 schema](Documentation~/arena-profile.md) and [validation checklist](Documentation~/validation.md).

## Startup licensing

The explicit Development mode is accepted only in the Editor or a Development Build. A release build must select Licensed Application and provide the existing `DHMP1` application license, trusted public verification key and correct ApplicationId. Client and server identities are separate. Checks run at startup, not in the packet loop. Unsupported cryptography fails closed. The package contains no issuer private keys or fabricated production licenses.

Environment overrides: `DHMP_LOCAL_IPV6`, `DHMP_DEMO_IPV6`, `DHMP_APPLICATION_ID`, `DHMP_LICENSE_KEY`. Runtime licenses are unrelated to multiplayer guest identity.

## Validation

The repository's `Unity package preview` workflow compiles this exact portable source against .NET Standard 2.1, exercises it on .NET 10 on Linux/Windows, checks byte interoperability, runs a Linux raw IPv6 loopback session, and packages the sample with its metadata. Those jobs do **not** compile or launch the Unity Editor or an IL2CPP player. Unity/physical-host acceptance remains a separate required step.
