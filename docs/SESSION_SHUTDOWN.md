# Session send/control shutdown

Managed ownership hardening, 2026-09-30. This does not add disconnect packets, retries, a session manager or delivery confirmation.

## Synchronous security operations

`DhmpPskChaCha20Poly1305Session` serializes data protection, data decode and authenticated control through separate ownership gates. Outbound counter allocation and encryption are one operation, so concurrent calls cannot reuse a data nonce. Feedback counter allocation and control replay-window updates are serialized as well. Send and receive can proceed independently.

`Dispose` joins in-progress synchronous crypto operations before disposing ciphers and clearing shared key/session storage. A racing operation either completes before disposal or throws `ObjectDisposedException` after it. Locks do not span socket awaits or application callbacks. Session telemetry snapshots remain readable after disposal.

This does not replace application lifecycle management: stop scheduling new work and join tasks before final cleanup. Concurrent callers require separate destination buffers. `DhmpClient` still requires serialized sends for its local rate/pacing state. No packet-arrival ordering guarantee is added.

## Protected sender retirement

`DhmpProtectedPacketSender` implements `IAsyncDisposable`. Calling `DisposeAsync` immediately stops admission, then waits for every admitted send to leave the backend and clear/return its pooled packet storage. Invalid input, cancellation, protection exceptions and backend failure release their admission leases. Send errors remain on their send tasks; retirement does not retry them or conceal them.

Repeated disposal awaits the same drain. The wrapper does not dispose its caller-owned backend or security session. Retirement completion means local buffer use ended; it does not prove remote delivery.

An uncooperative backend can keep retirement pending. A caller can cancel its own wait with `retirementTask.WaitAsync(token)`, but must retain and ultimately join the original task before disposing shared resources. Cancelling the wait neither rolls back retirement nor cancels an existing send. Cancel sends through their original cancellation token. A backend must not synchronously await disposal of a wrapper whose own send it is executing.

## Application shutdown order

1. Stop scheduling sends, feedback and path-probe work; cancel their loops through application tokens.
2. Start receive retirement through `RemoveAsync`, or cancel a single-peer receive loop.
3. Await `protectedSender.DisposeAsync()` and observe all outstanding send tasks. Join outstanding control/feedback loops and handshakes separately. For unprotected sends, join their tasks directly.
4. Await receive retirement/loop completion and drain the application dispatcher.
5. Dispose the raw socket backends/control channels, then the caller-owned security session after all users have finished.

Raw socket `Dispose` remains an abort of I/O, not an awaitable session-wide drain. Use cancellation and join the socket tasks for graceful shutdown. Existing handshake deadlines are unchanged.

The [two-host smoke run](TWO_HOST_SMOKE.md) exercises the concrete sender/receiver and this cleanup order. Full multi-peer replacement, control-loop composition, prolonged operation and independent security review remain release gates.
