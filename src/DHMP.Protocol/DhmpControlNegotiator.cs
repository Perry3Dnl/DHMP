namespace DHMP.Protocol;

/// <summary>
/// Pure control-plane compatibility logic. It performs no network I/O.
/// </summary>
public static class DhmpControlNegotiator
{
    public static DhmpHelloEvaluation EvaluateHello(
        DhmpPeerProfile localProfile,
        DhmpControlMessage hello)
    {
        localProfile.Validate();
        hello.Validate();

        if (hello.Type != DhmpControlMessageType.Hello)
        {
            return Reject(
                localProfile,
                hello.CorrelationId,
                DhmpControlRejectReason.UnexpectedMessage);
        }

        if (hello.DataWireVersion != localProfile.WireContract.Version)
        {
            return Reject(
                localProfile,
                hello.CorrelationId,
                DhmpControlRejectReason.UnsupportedWireVersion);
        }

        if (hello.RecordSize != localProfile.WireContract.RecordSize)
        {
            return Reject(
                localProfile,
                hello.CorrelationId,
                DhmpControlRejectReason.RecordSizeMismatch);
        }

        if (hello.SchemaId != localProfile.SchemaId)
        {
            return Reject(
                localProfile,
                hello.CorrelationId,
                DhmpControlRejectReason.SchemaMismatch);
        }

        if (hello.MaximumReceivePayloadBytes < hello.RecordSize)
        {
            return Reject(
                localProfile,
                hello.CorrelationId,
                DhmpControlRejectReason.ReceiveLimitTooSmall);
        }

        var remoteProfile = new DhmpPeerProfile(
            new DhmpWireContract(
                hello.RecordSize,
                hello.DataWireVersion),
            hello.MaximumReceivePayloadBytes,
            hello.SchemaId);

        return new DhmpHelloEvaluation(
            DhmpControlMessage.Accept(
                localProfile,
                hello.CorrelationId),
            remoteProfile);
    }

    public static DhmpNegotiatedPeer CompleteResponse(
        DhmpPeerProfile localProfile,
        DhmpSendPolicy localSendPolicy,
        uint expectedCorrelationId,
        DhmpControlMessage response)
    {
        localProfile.Validate();
        localSendPolicy.Validate(localProfile.WireContract);
        response.Validate();

        if (response.CorrelationId != expectedCorrelationId)
            throw new DhmpProtocolException(
                "DHMP control response correlation does not match the outstanding HELLO.");

        if (response.Type == DhmpControlMessageType.Reject)
            throw new DhmpNegotiationException(response.RejectReason);

        if (response.Type != DhmpControlMessageType.Accept)
            throw new DhmpProtocolException(
                "Expected a DHMP ACCEPT or REJECT control response.");

        if (response.DataWireVersion != localProfile.WireContract.Version)
            throw new DhmpProtocolException(
                "Accepted DHMP response changed the wire version.");

        if (response.RecordSize != localProfile.WireContract.RecordSize)
            throw new DhmpProtocolException(
                "Accepted DHMP response changed the record size.");

        if (response.SchemaId != localProfile.SchemaId)
            throw new DhmpProtocolException(
                "Accepted DHMP response changed the application schema identifier.");

        var remoteProfile = new DhmpPeerProfile(
            new DhmpWireContract(
                response.RecordSize,
                response.DataWireVersion),
            response.MaximumReceivePayloadBytes,
            response.SchemaId);

        return CreateNegotiatedPeer(
            remoteProfile,
            localSendPolicy);
    }

    public static DhmpNegotiatedPeer CreateNegotiatedPeer(
        DhmpPeerProfile remoteProfile,
        DhmpSendPolicy localSendPolicy)
    {
        remoteProfile.Validate();
        localSendPolicy.Validate(remoteProfile.WireContract);

        int recordSize = remoteProfile.WireContract.RecordSize;
        int rawMaximum = Math.Min(
            localSendPolicy.MaximumPayloadBytes,
            remoteProfile.MaximumReceivePayloadBytes);
        int effectiveMaximum =
            rawMaximum / recordSize * recordSize;

        if (effectiveMaximum < recordSize)
            throw new DhmpNegotiationException(
                DhmpControlRejectReason.ReceiveLimitTooSmall,
                "Remote DHMP receive capability cannot accept one complete record.");

        return new DhmpNegotiatedPeer(
            remoteProfile,
            new DhmpSendPolicy(
                localSendPolicy.Pmax,
                effectiveMaximum,
                localSendPolicy.RatePolicy));
    }

    private static DhmpHelloEvaluation Reject(
        DhmpPeerProfile localProfile,
        uint correlationId,
        DhmpControlRejectReason reason)
        => new(
            DhmpControlMessage.Reject(
                localProfile,
                correlationId,
                reason),
            null);
}
