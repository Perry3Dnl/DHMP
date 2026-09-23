# Showcase v6 — audited fair per-message framing benchmark

Date: 2026-09-23

This generation replaces the previous README framing graph.

## Why v6 exists

The older showcase allowed WebSocket/HTTP/1.1/HTTP/2 to amortize one framing header over roughly 8 KiB / 256 logical records while other paths paid framing per 32-byte logical record. That was a useful bulk-framing experiment but not a clean per-message comparison.

v6 freezes a stricter rule:

> **One 32-byte logical application message per protocol framing unit.**

This makes the native framing-kernel comparison easier to interpret. It is still **not a full production-stack benchmark**.

## Audited wire shapes

| Path | Wire bytes per 32 B logical message | v6 framing unit |
| --- | ---: | --- |
| DHMP fixed contract | 32 B | fixed record, no repeated DHMP header |
| Raw TCP fixed32 | 32 B | fixed record |
| TCP varint length | 33 B | 1 B length + 32 B |
| TCP 4-byte length | 36 B | 4 B big-endian length + 32 B |
| WebSocket | 34 B | unmasked server-to-client binary frame, 2 B header + 32 B |
| HTTP/1.1 chunk | 38 B | `20\r\n` + 32 B + `\r\n` |
| HTTP/2 DATA | 41 B | 9 B DATA header + 32 B |
| gRPC/H2 shape | 46 B | 9 B DATA + 5 B gRPC envelope + 32 B |
| MQTT QoS0 | 37 B | minimal PUBLISH with 1-byte topic + 32 B |
| NATS PUB | 44 B | `PUB x 32\r\n` + 32 B + CRLF |
| UDP | 32 B application payload | one datagram; non-equivalent reference |

## Shared loopback configuration

- Linux x86-64 under KVM;
- reported CPU: AMD EPYC 9V74;
- 5 available logical CPUs, one NUMA node;
- GCC 14.2.0;
- compile: `-O3 -mavx2 -pthread -std=c11`;
- 32-byte logical message;
- 12 KiB receiver read target;
- ~256 KiB reusable sender wire cycle;
- 8 MiB actual send and receive socket buffers;
- TCP_NODELAY for every TCP path;
- sender CPU 0, receiver CPU 1, consumer CPU 2;
- 300 ms warmup;
- 0.8 s measurement per path;
- five rotated path orders;
- zero artificial consumer hold;
- identical payload sequence validation for every stream parser;
- identical newest-state publication step after each receive batch.

## Audit performed before the run

The v6 source passed:

- strict `-Wall -Wextra -Wpedantic -Wconversion -Wshadow` compilation with zero warnings;
- parser fragmentation tests at 1, 2, 3, 7, 31, 32, 33, 127, 511, 4096, 8191 and 12288 byte chunks;
- payload-corruption detection checks;
- framing-corruption detection checks;
- AddressSanitizer + UndefinedBehaviorSanitizer self-tests;
- CPU-affinity/configuration validation.

Source SHA-256 used for the retained run:

`4d78196ea02634da97d43966a6a510d54d6779f11b377b59a692cf757aca99b2`

## Validation result

Across all five loopback runs for all paths:

- framing errors: **0**
- payload-sequence errors: **0**
- consumer validation errors: **0**
- UDP payload errors: **0**
- configuration errors: **0**

The processor-only cross-check also reported zero framing and sequence errors.

## Important interpretation

This benchmark answers:

> What does one 32-byte-message framing shape cost in this one native implementation and machine?

It does **not** answer:

> Is DHMP faster than a complete ASP.NET Core HTTP/2 server, grpc-dotnet application, MQTT broker, NATS server, or browser WebSocket stack?

HTTP/1.1 chunks and HTTP/2 DATA frames are transport/framing constructs rather than application-message standards. Choosing one frame/chunk per logical record is an explicit v6 normalization rule, not a claim that every real HTTP application uses that write pattern.

The loopback host remains noisy. The throughput chart therefore publishes median **and min-max range**. Processor-only CPU is included as a second cross-check.

Raw TCP is expected to be the floor. In v6 it is indeed slightly faster than DHMP in the medians, which is a healthy sanity check rather than a problem to hide.


## Exact retained source

The exact audited source used for the retained v6 run is published as:

[showcase_v6_fair.c.gz.b64](showcase_v6_fair.c.gz.b64)

Reconstruct it with:

```sh
base64 -d showcase_v6_fair.c.gz.b64 | gzip -dc > showcase_v6_fair.c
sha256sum showcase_v6_fair.c
```

Expected SHA-256:

```text
4d78196ea02634da97d43966a6a510d54d6779f11b377b59a692cf757aca99b2
```

This is the exact source snapshot audited and used for the retained measurements above.
