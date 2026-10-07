# DHMP connection transport resolver

Status: active pre-1.0 direction.

## Goal

Applications establish a DHMP connection; they do not hard-code a socket backend.

The connection resolver evaluates local platform capability, attempts the preferred
network path, and selects the fastest implemented path that actually completes the
DHMP compatibility handshake.

Current order in `Auto` mode:

1. native DHMP Raw IPv6;
2. DHMP carried inside UDP as a compatibility path.

AF_XDP remains a measured/probed research fast path. It is not selected by the
connection resolver until it has a complete connection backend with the same
lifecycle, control-plane and receive-routing guarantees.

## Wire invariant

Transport selection does not change the DHMP V1 application record.

Native path:

```text
[ IPv6 ][ DHMP fixed record ]
```

UDP compatibility path:

```text
[ IP ][ UDP ][ DHMP fixed record ]
```

The UDP header is lower-layer carrier overhead. DHMP still prepends zero bytes and
appends zero bytes to the V1 data record. Compatibility/security setup remains on a
separate control socket/port.

## Resolution

Local capability is not treated as network reachability.

For an initiating connection in `Auto` mode the connector:

1. checks whether the native Raw IPv6 backend is locally usable;
2. sends a 16-byte **Poke** on that path and requires an exact byte-for-byte echo;
3. when Mini Poke succeeds, sends a 1,200-byte Full Echo Poke and measures the round trip on the initiator's monotonic clock;
4. if native Poke times out or fails for a reachability/socket reason, repeats Poke over the UDP compatibility path;
5. only after Poke selects a live path does the normal DHMP compatibility handshake run;
6. once a path completes compatibility negotiation, security setup and steady-state data stay on that selected path.

Poke is deliberately not authentication and does not add bytes to normal DHMP V1 data packets. The responder validates the fixed Poke prefix and token, then returns the received Poke bytes unchanged. Parsing is Span-based; async socket boundaries use Memory only for buffer lifetime correctness.

An accepting connector listens for both implemented paths and accepts the first one
that successfully completes compatibility negotiation.

Negotiation rejection (for example a schema or wire-contract mismatch) is not a
reason to hide the error by silently switching transports.

## Platform scope

The managed UDP compatibility backend supports IPv4 and IPv6 address families and
does not require raw-socket privileges. The current native Raw IPv6 backend remains
Linux-only and still requires the explicit experimental protocol-number opt-in.

This means unsupported raw-socket platforms can still run DHMP through the
compatibility carrier, while Linux hosts with a reachable native path keep the
lower-overhead native mode.

## Ports and path budget

The default compatibility ports are:

- data: 47530/UDP;
- control: 47531/UDP.

They are application defaults, not IANA-registered DHMP service ports, and are
configurable through `DhmpConnectorOptions`.

The default UDP DHMP payload ceiling is 1232 bytes so IPv6 minimum-MTU operation can
account for the 40-byte IPv6 header and 8-byte UDP header. Auto mode constrains the
shared DHMP policy to the smallest enabled backend ceiling so changing path does not
change record interpretation.

## NAT and firewall scope

UDP compatibility is intended to improve traversal of networks that reject unknown
IPv6 Next Header values. It is not a claim that every firewall allows UDP.

The runtime records the observed UDP data endpoint for a configured peer and uses it
for subsequent sends, which supports common client-side source-port translation after
the peer has sent data. Full NAT discovery, relay service, connection migration and
general Internet hole punching are not yet implemented.

## API observability

`DhmpConnection.Transport` reports the selected path:

- `RawIpv6`;
- `UdpCompatibility`.

`DhmpConnector.GetTransportCandidates(remoteAddress)` reports local candidates and
diagnostics. A candidate being locally available never means that the remote path is
reachable; only a completed DHMP handshake establishes that.


## Poke API and measurements

`DhmpConnector.PokeAsync(...)` runs the same pre-handshake path selection without establishing a DHMP session. The result reports the selected transport, Mini Poke RTT, Full Echo RTT and Full Echo byte count.

For an initiated connection, `DhmpConnection.InitialPoke` exposes the Poke result that selected its transport. Accepted/server-side connections leave this null because that side answered the Poke.

The Full Report benchmark contains a separate local Poke processing section. Those rows measure Span validation plus exact echo processing and are explicitly not Internet RTT or sustained network-throughput claims.
