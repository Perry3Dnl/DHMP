# DHMP Networking 1: Unity package and included demo

The product direction is a self-hostable Unity networking package with the complete Demo Arena included in the sold package. The publisher operates a demonstration endpoint; customer game hosting remains customer-operated. The included server runs the same arena/application code as the demo client.

The first implementation is under [Packages/com.dhmp.networking](../Packages/com.dhmp.networking/README.md). It includes the importable Unity sample, portable .NET Standard 2.1 code, desktop native raw IPv6 I/O, server-authoritative arena movement, prediction/reconciliation, remote interpolation, startup licensing, build menus and hosting examples.

It preserves the [headerless V1 contract](WIRE_CONTRACT_V1.md). The separately specified [DUNA/1 application schema](../Packages/com.dhmp.networking/Documentation~/arena-profile.md) contains the game state and input acknowledgements. No legacy transport fallback is added and the existing .NET 10 projects retain their current targets.

This is `0.1.0-preview.1`, not a published product. The publisher's arena address is not configured, and no server is deployed by this change. The [validation checklist](../Packages/com.dhmp.networking/Documentation~/validation.md) distinguishes portable-runtime/raw-loopback evidence from the still-required Unity player/platform and real Internet tests. Public-service security integration and commercial release qualification remain open.

Commands from the repository root:

```sh
dotnet run --project tests/DHMP.Unity.Tests/DHMP.Unity.Tests.csproj -c Release
python3 tools/check_unity_package.py
python3 tools/package_unity.py
```

The `Unity package preview` workflow also runs raw IPv6 loopback sessions on Linux and Windows with the required permission and uploads the importable archive. It does not publish to NuGet or the Unity Asset Store.

## First verified preview

On 2026-10-06, [workflow run 37421481560](https://github.com/Perry3Dnl/DHMP/actions/runs/37421481560) passed for code commit `ad4cfc3ba495c6ae6e08f402579bead1dac39e7e`: 43 portable-runtime checks on each OS, package/reference checks and real raw IPv6 loopback sessions on both OSes. This covers the code below the Unity-facing components; no Unity executable has been built or played as part of that run.
