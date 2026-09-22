# DHMP — Direct Headerless Message Protocol

DHMP is an experimental fixed-contract application transport focused on keeping repeated metadata and unnecessary application work out of the hot path.

The working idea is simple:

- negotiate the contract once when a connection/session is established;
- use the negotiated fixed frame size as framing;
- steady-state application traffic is raw frame bytes, with no per-frame DHMP header;
- keep the sender independent from application-level request/reply waiting;
- support both "process every frame" and "newest complete state wins" consumption;
- support plain **DHMP** and TLS-wrapped **DHMPS**;
- separate delivery semantics from consumption semantics.

This repository currently documents and benchmarks the protocol while the .NET implementation is still a prototype. Some experiment code and historical benchmark names still use the earlier internal name `FixedWire`; those names are intentionally being preserved until the protocol model is stable.

## Current delivery model

DHMP currently distinguishes two delivery semantics:

**Unconfirmed** — the sender continuously emits frames and does not require DHMP-level proof that the peer accepted them. TCP still performs normal transport reliability while the connection exists.

**Verified** — the normal data path is intentionally the same one-way stream: no per-frame ACK and no periodic ACK. The sender retains unverified history locally. If a session breaks, the reconnect handshake asks the receiver for its accepted stream position and replays only the uncertain tail. Explicit verification/checkpoint boundaries can later be used to release retained history.

These are independent from the receive policy:

- **Every** — expose every complete logical frame.
- **Latest** — if multiple complete state frames are queued, expose only the newest complete frame and skip obsolete states.

## Wire principle

Anything that can be established once should stay out of the per-frame hot path.

Steady state is conceptually:

```text
[frame][frame][frame][frame]...
```

not:

```text
[type][sequence][length][metadata][payload]
```

See [docs/PROTOCOL_DRAFT.md](docs/PROTOCOL_DRAFT.md) for the current protocol model and [docs/CURRENT_STATUS.md](docs/CURRENT_STATUS.md) for the development status and benchmark conclusions.

Raw benchmark CSVs and comparison charts are kept under [benchmarks/results](benchmarks/results), with methodology and caveats in [docs/BENCHMARKS.md](docs/BENCHMARKS.md).

## Status

This is experimental work, not a published interoperability specification yet. The next major design problem is **bounded Verified mode**: verification/checkpoint traffic must remain asynchronous and rare while allowing old retained history to be discarded.

Requires .NET 10 for the current benchmark prototype.
