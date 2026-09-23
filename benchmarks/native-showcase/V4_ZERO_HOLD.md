# Showcase v4 — zero-hold maximum-speed benchmark

This suite removes the artificial 10 µs consumer hold from the headline comparison. The consumer validates an acquired state and releases it immediately; there is no clock-based delay in the hot consumer loop.

## Profile

- 32-byte logical payload
- 12 KiB reusable receive workspace
- 256 KiB reusable sender batch
- Ring-3 FRONT / MIDDLE / BACK publication
- 32-bit atomic MIDDLE token
- zero artificial consumer hold
- 500 ms warmup before each measured interval
- 1.2 second measured interval
- five runs per path with rotated order
- sender / receiver / consumer pinned to separate CPUs when available
- payload validation enabled
- framing validation enabled
- localhost Linux/C architecture benchmark

## Ten comparison baselines

1. Raw TCP with fixed 32-byte records
2. TCP with a one-byte varint length for the 32-byte payload
3. TCP with a 4-byte big-endian length
4. WebSocket binary framing, unmasked server-to-client shape
5. HTTP/1.1 chunk framing
6. HTTP/2 DATA-frame framing
7. gRPC message envelope carried in an HTTP/2 DATA-frame shape
8. MQTT QoS 0 PUBLISH framing with a fixed one-byte topic
9. NATS PUB framing with a fixed subject
10. UDP using batched sendmmsg / recvmmsg datagrams

These are transport/framing hot-path comparisons inside one native harness. HTTP, WebSocket, gRPC, MQTT and NATS rows are not complete production server/client framework stacks and should not be described as such.

## Important interpretation

The previous 10 µs hold benchmark answered a slow-consumer / conflation question. This v4 suite answers the maximum-speed question with that artificial application delay removed.

The localhost host remains noisy. For example, DHMP ranged from 51.48 M to 154.97 M logical frames/s across the five rotated passes. Publish medians and ranges are therefore retained rather than hiding run-to-run variability.

Raw fixed TCP and DHMP have essentially the same fixed-record wire work in this harness; small differences between them should be treated as measurement variance, not as DHMP somehow making TCP itself faster.

The publication rate is a batch/freshness metric, not a per-frame processor rate: Latest publishes at most one newest state per receive batch. Receiver CPU nanoseconds per logical input frame is included separately.
