# Application-owned lightweight confirmation

Status: experimental opt-in helper. **This does not define a DHMP wire profile and adds zero DHMP bytes.** Headerless V1 framing, the fixed-record contract and the default client remain unchanged.

## Goal

Some applications want evidence that a peer saw an important update without paying the CPU and comparison cost of the DECO/1 full-record echo profile.

The lightweight option intentionally does not define a DHMP ACK header, sequence field, magic value, kind byte or confirmation record layout. The application already owns the bytes inside each fixed DHMP record, so it also owns the identifier used for confirmation.

If an application already has a generation, message ID, command ID or transaction ID, reuse it.

`DhmpApplicationConfirmationTracker` only keeps bounded local pending state:

1. register a nonzero application-owned ID,
2. submit the application's complete record unchanged,
3. wait until the application receive path observes that same ID in peer return traffic,
4. call `TryConfirm(id)`,
5. complete the waiter or time out.

There is no retransmission.

## Important fixed-record consequence

DHMP sessions use a fixed record size. Therefore a standalone confirmation record sent on the same session is still one full fixed-size record on the wire even if it contains only an 8-byte ID.

For that reason the efficient patterns are:

- **piggyback** one or more confirmation IDs on normal return application records;
- **batch** many confirmation IDs into one application-owned record;
- use a separate application/session contract with a smaller fixed record only when the application genuinely wants a distinct confirmation channel.

The helper does not choose between those designs.

## Example

```csharp
await using var confirmations =
    new DhmpApplicationConfirmationTracker(
        client,
        new DhmpApplicationConfirmationOptions
        {
            MaximumInFlight = 32,
            ConfirmationTimeout = TimeSpan.FromSeconds(2)
        });

ulong messageId = state.Generation;

// MySchema owns the complete fixed-size record and decides where messageId lives.
byte[] record = MySchema.EncodeState(state, messageId);

Task<DhmpApplicationConfirmationReceipt> pending =
    confirmations.SendTrackedAsync(
        messageId,
        record,
        cancellationToken);

// Later, on the receive path:
if (MySchema.TryReadPeerConfirmation(incomingRecord, out ulong confirmedId))
    confirmations.TryConfirm(confirmedId);

DhmpApplicationConfirmationReceipt receipt = await pending;
```

Ordinary application records sharing that same `DhmpClient` should be sent through `SendUntrackedAsync` while the tracker is active so that the client's required serialized send path remains preserved.

The receive-side application can return confirmation IDs however its own schema permits. The tracker never parses, rewrites, pads or reserves application bytes.

## Security and meaning

An ID is only as trustworthy as the receive path that produced it. On an authenticated DHMP security session, the application can treat a valid decoded confirmation field as peer-authenticated application data. On an unprotected path, an attacker able to inject matching application records may spoof confirmation.

A successful receipt means only that the local application observed the expected ID in peer return traffic. It does not prove that the peer committed data to storage, applied a game-state transition, executed a command, or will not later discard the result.

Timeout or cancellation means delivery is unknown.

## Relationship to full echo

[DECO/1](ECHO_CONFIRMATION_PROFILE_V1.md) remains the stronger byte-for-byte option. It retains and compares the full record and therefore provides evidence that matching application bytes came back.

Application-owned confirmation is cheaper in local state and comparison work, and can be much cheaper in return bandwidth when IDs are piggybacked or batched. It deliberately provides weaker evidence.

Neither option changes the DHMP V1 wire framing and neither adds automatic retransmission.
