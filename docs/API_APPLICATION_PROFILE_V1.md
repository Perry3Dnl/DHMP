# DAPI application profile version 1

Status: experimental .NET API application schema. Both endpoints explicitly opt in through the API integration. This does not change DHMP V1 packet framing or claim compatibility with an arbitrary HTTP server.

DHMP's framing core receives opaque, whole 1200-byte records. DAPI/1 interprets their contents above that core. Chunking and request identity below belong to this application schema; neither is a hidden DHMP packet header or a protocol-owned delivery mechanism. Only the PSK-protected DHMP path is selected by the integration.

## Fixed application record

All multi-byte numeric fields use big-endian encoding.

| Offset | Bytes | Application field |
| --- | --- | --- |
| 0 | 4 | ASCII `DAPI` |
| 4 | 1 | Profile version 1 |
| 5 | 1 | 1 request, 2 response |
| 6 | 2 | Reserved, must be zero |
| 8 | 8 | Non-zero request sequence, monotonically allocated by its originator |
| 16 | 8 | Reserved correlation space, must be zero |
| 24 | 4 | Positive total serialized message bytes |
| 28 | 2 | Zero-based chunk index |
| 30 | 2 | Chunk count, exactly ceil(total / 1168) |
| 32 | 1168 | Message chunk; unused tail must be zero |

Each full record fits one protected raw-IP payload; IP fragmentation is not intentionally used. Multiple application records form a buffered application message, without carrying partial DHMP record bytes between packets. A response echoes the originating request sequence. Request and response assembly keys are separate, so simultaneous calls in both directions do not share state.

Counter allocation/packet sends are serialized locally. A bounded 1024-request application replay window rejects completed, invalidated, expired and too-old request IDs for the current session; active assembly continues independently until its original monotonic deadline. At uint64 exhaustion a new security/application session is required. Incoming responses are admitted only for an outstanding local request. Old protected-session packets fail before reaching this application decoder.

## Serialized envelope

Message content is System.Text.Json UTF-8 with fields `Method`, `Target`, `Status`, `Headers` (string to string-array dictionary) and `Body` (base64 JSON bytes).

Requests use method, origin-form path/query target, ordinary headers and body. Responses use a 100–599 status, headers and body. The origin is configured out of band and is never selected by an incoming request. Hop-by-hop, proxy-authorization, Host and supplied Content-Length headers are excluded; body length is calculated from the received bytes. Body/message/header counts and header syntax are validated before application publication. Version/reserved/length/count/index/padding mismatches are rejected.

Example: request sequence 1, two message bytes `01 02`, one chunk, produces this 32-byte record prefix followed by `01 02` and 1166 zero padding bytes:

```text
4441504901010000000000000000000100000000000000000000000200000001
```

This is a codec conformance vector, not a valid JSON request envelope.

No request retry, chunk retransmission, fallback or remote cancellation message is defined. Failure semantics, limits, authentication boundaries and application shutdown are specified in [ASP.NET API integration](ASP_NET_API_INTEGRATION.md). Those limits make the profile suitable for controlled initial testing, not a universal replacement for every HTTP feature or an independently reviewed security protocol.
