# Benchmark notes and reference results

These are experimental localhost results from the current .NET 10 prototype. They are useful for comparing revisions on the same machine; they are not universal performance claims.

## Full protocol comparison

Configuration: persistent localhost connection, no TLS, no compression, one sequential request/reply at a time, identical N-byte opaque application frame in request and response, 10,000 warmup iterations, 100,000 iterations, 5 passes.

Median round trips/second:

| Bytes | DHMP | TCP + 4-byte length binary | MessagePack/TCP | HTTP/1.1 binary | HTTP/2 binary | gRPC/Protobuf |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 32 | 42,565 | 38,046 | 24,168 | 20,535 | 12,806 | 11,460 |
| 64 | 53,433 | 50,496 | 45,625 | 23,393 | 10,742 | 11,357 |
| 128 | 44,022 | 44,101 | 32,159 | 18,973 | 12,134 | 10,235 |
| 256 | 43,794 | 43,014 | 39,797 | 22,910 | 15,578 | 10,625 |
| 512 | 56,336 | 51,670 | 44,455 | 23,035 | 15,236 | 11,006 |
| 1024 | 56,239 | 51,858 | 44,465 | 22,795 | 15,369 | 13,887 |

The main comparison to keep in perspective is DHMP versus raw length-prefixed binary TCP. Both are deliberately lean and often close.

## DHMP/DHMPS versus HTTP/HTTPS quick comparison

Configuration: persistent localhost connection, same opaque N-byte request/response body, no compression, TLS setup outside the steady-state timing, 500 warmups, 2,000 iterations, 3 passes.

Secure median round trips/second:

| Bytes | DHMPS | HTTPS/1.1 binary | HTTPS/2 binary |
| ---: | ---: | ---: | ---: |
| 32 | 16,712 | 6,219 | 6,419 |
| 64 | 35,605 | 10,909 | 10,284 |
| 128 | 42,763 | 11,567 | 15,128 |
| 256 | 35,124 | 14,377 | 11,728 |
| 512 | 38,932 | 12,830 | 13,255 |
| 1024 | 38,133 | 12,974 | 11,999 |

This quick test is directional only. The architecture and API/tooling differences between these transports are much larger than a single sequential latency benchmark captures.

## Delivery-mode no-ACK run

The raw CSV is stored under `benchmarks/results/delivery-mode-results-2026-09-22.csv`.

A benchmark bookkeeping issue currently reports retained-frame counters for Unconfirmed as well; those counters should not be interpreted as actual Unconfirmed protocol retention. The Verified replay/missing/recovery measurements are the relevant fields.
