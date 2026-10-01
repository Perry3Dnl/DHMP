# Raw IPv6 deployment and reachability

Status: experimental pre-1.0 deployment profile.

DHMP V1's data-plane format is independent of any permanent IP protocol-number assignment. The current Linux raw-IPv6 research backend uses IPv6 Next Header values `253` for data and `254` for control because IANA reserves those values for experimentation and testing.

Official references:

- IANA Protocol Numbers: https://www.iana.org/assignments/protocol-numbers
- RFC 4727: https://www.rfc-editor.org/rfc/rfc4727.html

RFC 4727 states that these experimental values are appropriate only for explicitly configured experiments and must not be shipped as implementation defaults. It also warns that production networks do not necessarily support experimental IP-header code points.

## Runtime policy

`DHMP.RawIpv6` therefore does **not** silently activate 253/254.

A caller that deliberately runs the current raw-IPv6 experiment must opt in:

```csharp
var options = DhmpRawIpv6Options.FromPathMtu(
    localAddress,
    remoteAddress,
    pathMtu: 1280,
    enableExperimentalProtocolNumbers: true);
```

Without that explicit flag, the raw sender, receiver and control channel refuse to open the experimental binding.

The opt-in means only: "this application knowingly participates in an explicitly configured DHMP experiment." It does not make 253/254 DHMP-owned numbers and does not imply Internet reachability.

## What routers may do

An IPv6 router can forward a packet based on the destination address without understanding the upper-layer protocol. That does not guarantee end-to-end delivery.

Firewalls, security appliances, host firewalls, cloud filters, ISP equipment and other middleboxes may permit familiar upper-layer protocols such as TCP, UDP and ICMPv6 while dropping an unknown or experimental Next Header.

Accordingly, these are separate questions:

1. **Can DHMP process records quickly?** — measured by the CPU/memory benchmarks.
2. **Can the host kernel send and receive raw DHMP packets?** — exercised by the Linux raw-IPv6 tests.
3. **Can a particular network path carry the experimental Next Header?** — must be measured on that path.
4. **Can DHMP operate safely and fairly on a shared production network?** — not yet established.

A successful local or same-VM test does not answer questions 3 or 4.

## Release gate

A production DHMP release must not present 253 or 254 as permanent DHMP assignments.

Before claiming general native-Internet deployment support, the project needs at minimum:

- a documented permanent protocol-number strategy or standardized/assigned binding;
- physical two-host tests;
- multiple consumer-router/firewall tests;
- cloud-provider path tests;
- ISP/path diversity tests;
- long-duration loss, reordering and overload testing;
- an explicit firewall configuration guide;
- a reachability/fallback strategy if native DHMP is blocked.

A permanent protocol number would remove the experimental-number problem, but it would **not** guarantee traversal through arbitrary firewalls or middleboxes.

## Reachability test matrix

Future deployment testing should record reachability separately from throughput:

| Path | Data 253 | Control 254 | Loss/reorder | Notes |
| --- | --- | --- | --- | --- |
| Linux namespace -> namespace | Tested | Tested | Measured | Same host only |
| Physical LAN host -> host | Pending | Pending | Pending | Real NIC/switch |
| Consumer router | Pending | Pending | Pending | Stateful firewall behavior |
| Cloud VM -> cloud VM | Pending | Pending | Pending | Provider-specific filtering |
| Residential ISP -> public server | Pending | Pending | Pending | Public Internet |
| Mobile/hotspot path | Pending | Pending | Pending | Carrier filtering |
| VPN path | Pending | Pending | Pending | Tunnel-dependent |

This matrix is intentionally not a speed leaderboard. Its purpose is to determine where the native raw-IP profile actually works.
