# Preview validation and remaining acceptance

## Automated checks in the repository

- Compile the distributed portable source as .NET Standard 2.1 with C# 9.
- Run portable tests on Linux and Windows under .NET 10.
- Compare Control V1 bytes and UUID byte order against `DHMP.Protocol`.
- Compare signed DHMP1 license acceptance with `DHMP.Licensing`; reject altered and wrong-application licenses.
- Reject malformed complete packets atomically, nonfinite floats and invalid reserved fields.
- Exercise shared-motor speed, diagonal normalization, jumping, landing and boundaries.
- Exercise application join, duplicate input, stale-session rejection, reconciliation, multi-packet snapshots, disconnect and idle cleanup.
- Exercise actual Linux raw IPv6 253/254 sockets with the portable runtime on loopback.
- Validate importable package structure, sample references, metadata uniqueness and included hosting files; create the `.tgz`.

These checks are not Unity Editor or IL2CPP compilation, a rendered-scene test, Windows native socket evidence, physical networking, security qualification or an Internet benchmark.

## Required Unity acceptance

1. Import into a clean Unity 6 project with the new Input System enabled. Check for compiler/import errors and missing references/materials.
2. Open the sample. Confirm settings errors are actionable and the unconfigured official endpoint is disabled.
3. Start a permitted server and connect a permitted client. Check name, movement, camera, jump, platform landing, input-to-snapshot timing and real payload counters.
4. Connect a second client from a distinct source IPv6. Observe both players, including later join, smooth remote movement and leave/timeout cleanup.
5. Test packet loss/reordering and stalls. Verify movement stays server-owned, pending inputs remain bounded and failures permit a fresh connection.
6. Leave/re-enter Play Mode, including with domain reload disabled. Verify sockets are closed and callbacks/avatars are not duplicated.
7. Compile/run the Windows client and Linux Dedicated Server. Test Mono and IL2CPP separately; confirm license-crypto and native P/Invoke behavior on each selected backend.
8. Check both render pipelines the product intends to support. The sample includes a referenced unlit shader to avoid relying on a stripped runtime shader lookup.

## Required product acceptance

- Integrate the existing DHMS V2 security profile and appropriate public-service admission behavior.
- Provision the real demonstration server and set the sample endpoint only after successful client connections.
- Run two-physical-host tests and tests across different providers/routers with appropriate raw-socket permissions.
- Replace development licensing with real per-application keys for any release binaries.
- Finalize commercial distribution terms, supported Unity/platform matrix, support process and measured capacity.
- Check the final archive includes the sample, server source/build menus and hosting instructions.

Do not describe a code-only preview or memory/loopback measurement as a completed commercial product or physical Internet performance.
