# Optional full-echo confirmation: DECO/1

Status: experimental opt-in application profile. The default DHMP client and headerless V1 framing are unchanged. This profile confirms matching bytes received by a peer; it is not reliable delivery, application execution confirmation, or a throughput guarantee.

## Selecting the option

Both peers explicitly select the DECO/1 application schema, the same fixed record size, and an authenticated session. Wrap the exclusively owned `DhmpClient` in `DHMP.Client.DhmpEchoConfirmation` and supply that session's public ID. The existing sender must protect packets with the authenticated session; a public session ID alone does not authenticate anything. On session replacement retire the old profile and create a new one; never reuse its session ID or reset counters within a live session.

```csharp
await using var echo = new DhmpEchoConfirmation(client, secureSession.SessionId,
    new DhmpEchoConfirmationOptions
    {
        MaximumInFlight = 32,
        MaximumConcurrentReceives = 8,
        ConfirmationTimeout = TimeSpan.FromSeconds(5)
    });
DhmpEchoReceipt receipt = await echo.SendAsync(content, cancellationToken);
```

For each complete owned record published by the authenticated receive path, await `echo.ReceiveAsync(record, cancellationToken)`. A request returns owned application content after submitting its echo; a response returns null and completes the matching local send. Route returned content to application processing separately. Use an existing bounded receive dispatcher, or retain/copy borrowed data before asynchronous use; do not await while holding a borrowed receive lease beyond its documented lifetime. Use Sequential publication; Latest intentionally discards records and is unsuitable for confirming all sends.

This profile is independent of the ASP.NET DAPI/1 integration. It is not automatically enabled by `AddDHMP`, cannot be mixed into DAPI/1 records, and is not negotiated or discovered automatically. Both endpoints must configure the schema before starting their session. All sends and echoes through this profile share its serialized client send path; do not concurrently use the wrapped client elsewhere.

## Application wire schema

Each DHMP application record has the session's fixed size, with a minimum of 41 and a maximum of 65535 bytes, subject to the smaller real path/security budget. A 1200-byte record can carry up to 1160 application content bytes. Each send fits one record; this profile does not assemble larger messages.

| Byte offset | Field |
| --- | --- |
| 0–3 | ASCII `DECO` |
| 4 | Application profile version 1 |
| 5 | Kind: 1 request, 2 full echo |
| 6–7 | Reserved zero |
| 8–23 | Authenticated session ID, big-endian Guid encoding |
| 24–31 | Sender-local monotonically increasing nonzero uint64 request sequence, big-endian |
| 32–35 | Positive content length, big-endian int32 |
| 36–39 | Reserved zero |
| 40 onward | Content, followed by zero padding to the complete fixed record size |

The receiver echoes the entire record, changing only kind 1 to kind 2. A sender confirms only an exact match of the pending sequence, session, length, content and padding. Unknown, late, duplicate, mismatched and malformed echoes never confirm another send. Echoes are never echoed again, preventing a reflection loop. Fields above belong to this explicitly selected application schema, not protocol-owned V1 metadata. No changes are made to DHMP packet framing or control messages.

## Failure and cost

Sends are submitted once. Loss of the request or echo produces uncertainty after the bounded deadline. Cancellation also leaves delivery uncertain. There is no retry, retransmission, repair, execution deduplication or guarantee that the application accepted/processed the returned content. Duplicate incoming requests may return duplicate application content; the consuming application must define its duplicate policy. Transport failures are surfaced; overloaded incoming confirmation work is dropped. Outbound admission rejects excess calls locally.

Defaults retain at most 32 outbound records and permit 8 concurrent echo operations. Limits are snapshotted at construction. Send/echo backend ownership is joined before profile disposal completes, even if a backend ignores cancellation. The profile does not own or dispose the underlying client/backend/session; retire it before disposing those resources. A backend that never returns can prevent retirement.

Every successfully received request generates one full-size return record: roughly twice the data volume across both directions, plus security/IP overhead. Full-duplex directional capacity, encryption, comparison, memory copies and pacing affect actual throughput. No 50 GB/s or 25 GB/s physical-network claim has been measured. Benchmark default and echo modes separately when appropriate hardware becomes available.
