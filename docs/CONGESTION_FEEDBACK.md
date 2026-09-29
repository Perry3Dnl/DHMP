# DHMP authenticated congestion feedback

Status: active experimental .NET profile.

DHMP keeps congestion feedback off the data path. Headerless V1 data packets are unchanged.

Authenticated congestion feedback is available only when the PSK security profile is active. Plain V1 and unauthenticated compatibility Control V1 do not get a remote throttle primitive.

## Purpose

The receive side already has explicit bounded overload behavior:

- Latest replaces obsolete pending work;
- Sequential rejects new work when its bounded queue is full.

Those local overload events can now be converted into authenticated pacing feedback.

The receiver does not set an absolute sender rate. It recommends a multiplicative scale on the sender's current rate.

The sender remains bounded by its own local minimum and maximum Pmax policy.

## Receiver recommendation

`DhmpCongestionAdvisor` compares two `DhmpReceiveDispatchSnapshot` values.

Current rules:

- Sequential saturation/drop -> Hard pressure, 500 permille (50%);
- Latest pending replacement or high queue pressure -> Soft pressure, 750 permille (75%);
- no new overload pressure -> None, 1000 permille (100%).

The feedback also carries pressure evidence:

- pending batch count;
- dispatcher capacity;
- cumulative lost pending work from drops/replacements.

These values are observability evidence, not delivery guarantees.

## Sender adaptation

`DhmpAdaptiveRateController` is local sender state.

On authenticated pressure:

```text
current rate
    × authenticated scale
    ↓
clamp to local minimum
```

The remote peer cannot increase the sender beyond the sender's configured local maximum.

When authenticated no-pressure feedback is received, the sender recovers gradually according to its configured recovery percentage instead of jumping immediately to Pmax.

Example:

```text
local Pmax = 100,000 msg/s
current    = 100,000

Hard / 50%
    ↓
50,000

Hard / 50%
    ↓
25,000

No pressure
    ↓
27,500    (10% local recovery)

No pressure
    ↓
30,250
...
never above 100,000
```

## Authenticated feedback packet

Feedback uses experimental control protocol / Next Header `254`.

It is exactly 64 bytes:

```text
Offset  Size  Field
0       4     Magic = ASCII "DHMF"
4       1     Feedback version = 1
5       1     Pressure: None / Soft / Hard
6       2     Rate scale permille, unsigned big-endian
8       16    Security session identifier
24      8     Directional feedback sequence
32      4     Pending batches
36      4     Dispatcher capacity
40      8     Cumulative lost pending work
48      16    Truncated HMAC-SHA256 authentication tag
```

The HMAC key is not the raw PSK. A dedicated directional feedback-authentication key is derived with HKDF-SHA256 from the PSK and security session identifier.

Separate keys are derived for initiator->responder and responder->initiator feedback.

## Replay and spoofing behavior

Each direction has its own 64-bit feedback sequence.

The receiver uses the same bounded replay-window concept as protected data:

- duplicate authenticated feedback is rejected;
- feedback older than the replay window is rejected;
- limited control-packet reordering is accepted;
- unauthenticated feedback cannot advance replay state.

A packet from the wrong direction or another security session fails authentication.

This prevents an unauthenticated third party from sending a fake "slow down" instruction.

## Client integration

A smoothly paced client may be created with a `DhmpAdaptiveRateController`.

```csharp
var controller = new DhmpAdaptiveRateController(
    maximumMessagesPerSecond: sendPolicy.Pmax,
    minimumMessagesPerSecond: 1_000);

var client = new DhmpClient(
    sender,
    wire,
    sendPolicy,
    controller);
```

`DhmpRawIpv6CongestionChannel.RunAdaptiveReceiveLoopAsync(...)` authenticates incoming feedback and applies it to the controller.

Adaptive control requires `DhmpRatePolicy.SmoothPacing`. RejectWindow remains a purely local hard-budget policy.

## Scope limits

This is receiver-driven application overload feedback plus adaptive pacing.

It is not yet a complete Internet congestion-control algorithm.

Still missing before making stronger congestion-safety claims:

- packet-loss/ECN/path feedback;
- RTT/feedback timing model;
- feedback-loss behavior;
- fairness evaluation between competing flows;
- physical multi-flow tests;
- denial-of-service and control-frequency analysis;
- independent security review.

Do not describe this profile as TCP-equivalent congestion control.
