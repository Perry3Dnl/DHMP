# DHMP bounded overload behavior

Status: active .NET behavior.

This document defines how asynchronous application handoff remains bounded after a DHMP packet has already passed protocol validation.

It does not add reliability, retransmission or hidden buffering to the wire protocol.

## Ownership boundary

The packet processor publishes borrowed memory synchronously.

If application work must continue asynchronously, that borrowed memory cannot be retained directly.

`DhmpBoundedReceiveDispatcher` therefore:

1. copies the published batch into owned pooled storage;
2. applies an explicit bounded overload policy;
3. invokes the asynchronous application consumer later;
4. clears and returns the owned buffer after consumption.

The raw/network receive loop never waits for application queue capacity.

## LatestReplace

Use with Latest workloads.

Capacity is always one pending batch.

When no work is pending:

```text
incoming latest state
        ↓
pending slot
```

When one pending batch already exists:

```text
old pending state  -> replaced
newer state        -> pending slot
```

Work that is already executing is not cancelled.

Counters:

- `AcceptedBatches`: every batch accepted into/replacing the pending slot;
- `ReplacedBatches`: pending batches replaced before consumption;
- `ConsumedBatches`: batches delivered to the async consumer.

This matches the DHMP Latest objective: prefer useful recent state over queue growth.

## SequentialReject

Use with Sequential workloads.

A fixed number of pending batches is configured.

```text
incoming
   ↓
[0][1][2]...[N-1]
```

When the queue is full, new work is rejected/dropped immediately.

Existing queued batches are never overwritten and remain in accepted arrival order.

Counters:

- `AcceptedBatches`: batches admitted to the bounded queue;
- `SaturationDrops`: new batches rejected because the queue was full;
- `ConsumedBatches`: batches delivered to the async consumer.

There is no hidden retry and no unbounded queue.

If an application requires guaranteed delivery, it needs a different application/profile contract rather than turning Sequential into TCP-like recovery.

## Mapping from receive policy

`DhmpBoundedReceiveDispatcher.FromReceivePolicy(...)` maps:

- `DhmpProcessingMode.Latest` -> `LatestReplace`;
- `DhmpProcessingMode.Sequential` -> `SequentialReject`.

An application can construct the dispatcher directly only when it intentionally wants a different local overload policy.

## Failure behavior

An application-consumer exception propagates from `RunAsync`.

DHMP does not retry failed application work automatically.

Cancellation stops the run loop. Pending owned buffers are released when the dispatcher is disposed.

## Multi-peer use

Each `DhmpRawIpv6PeerBinding` can use its own dispatcher callback:

```text
source IPv6
   ↓
peer binding
   ↓
DhmpServer
   ↓
dispatcher.Publish
   ↓
bounded owned async handoff
```

This keeps overload policy local to each peer/session while preserving one shared raw IPv6 receive socket.
