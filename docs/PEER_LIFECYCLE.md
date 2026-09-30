# Peer receive lifecycle

Status: implemented managed boundary; privileged two-host/session-manager validation remains open.

`DhmpRawIpv6PeerRouter` still maps one source IPv6 address to one registered V1 binding. This change adds local receive ownership, not wire session identifiers, discovery, reconnect traffic or guaranteed delivery.

## Registration and route leases

Each registration tracks active routes independently. A route lease spans decoder execution, synchronous application publication and plaintext cleanup. Retirement prevents new leases; completion means all previously acquired leases have exited, including exceptional exits.

The payload/framing core is unchanged. Lease synchronization belongs to the peer-routing ownership boundary; no per-packet allocation is added. Concurrent callers need separate scratch buffers and must respect their decoder/callback's concurrency contract. The concrete multi-peer socket loop remains a single consumer.

## Removal

- `Remove(address)` immediately unregisters the current binding and starts retirement. Its boolean result does not mean active routes have finished; use only when receive-resource lifetime is managed separately.
- `RemoveAsync(address)` immediately unregisters the binding, then returns that binding after its active routes finish. Unknown/already absent addresses return null. Await this operation before disposing resources used by those incoming routes.

Do not call `Remove` and then expect a later `RemoveAsync` to find the already removed registration. Keep and await the original asynchronous operation.

Removal frees a registered-peer slot immediately. Another binding can be registered while retired work drains, but its state and resources must remain independent of the old session.

## Replacement

`ReplaceAsync(replacement)` replaces an existing address under the registration gate without increasing the configured registered-peer count. Unknown addresses, the same binding instance and bindings exceeding the listener ceiling are rejected before changing the current registration.

The new binding becomes available before the returned task completes. Routes leased to the old registration may finish there; the method returns that old binding only after it drains. A routing lookup that encounters a retired snapshot resolves the current registration again.

New protected sessions must use fresh handshake-derived keys/IDs and fresh per-session application state. Tests verify that old-session protected packets do not authenticate after replacement. Unprotected headerless V1 packets carry no session identifier: a delayed old packet with a compatible record shape cannot be distinguished solely by source address. Do not claim cross-session freshness for unprotected V1.

Caller-owned decoder, callback, dispatcher, freshness filter and send/control state are not automatically copied, reset or disposed. If old and new bindings intentionally share resources, draining the old binding does not authorize disposing resources still used by the new one.

## Shutdown and cancellation

1. Stop admitting application work for the retiring session.
2. Start/await `RemoveAsync`, or install a fully established fresh binding through `ReplaceAsync` and await the retired result.
3. Separately stop/join outstanding sends, feedback/control loops and other users of the same security session.
4. Dispose/drain the old asynchronous application dispatcher according to its own ownership contract.
5. Dispose the caller-owned old decoder/security session only after all its users finish.

The router drain covers incoming routes only; it does not establish remote disconnect or join outgoing/control work. A blocked application callback can keep retirement pending. The caller may bound its wait with `retirementTask.WaitAsync(token)`, but cancelling that wait does not undo removal/replacement or make resource disposal safe. Retain the original task and join it before cleanup.

A callback may start an asynchronous retirement operation and return. It must not synchronously wait for its own retirement: the lease is released only after that callback exits.

The peer limit bounds registered bindings. Retiring resources remain caller-owned until drained; serialize lifecycle changes or bound outstanding retirement operations in the application rather than accumulating an unbounded retirement backlog.

## Plaintext failure boundary

Single-peer receive and multi-peer routing share the same decode/publication cleanup path. Oversized network payloads are rejected before decoder invocation. After decoder invocation, the whole supplied plaintext scratch span is zeroed on success, failed authentication, invalid decoded lengths/record shapes, decoder exceptions and application exceptions. Exceptions remain visible; failed application work is not retried.

Retirement completion occurs after plaintext cleanup. It is not safe to retain borrowed receive spans outside their callback; asynchronous work still requires owned/copy storage.
