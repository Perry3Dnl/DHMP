namespace DHMP.Protocol;

/// <summary>
/// Parsed DHMP control-plane packet. This metadata exists only on the control binding,
/// never in the headerless V1 data payload.
/// </summary>
public readonly record struct DhmpControlMessage
{
    public DhmpControlMessage(
        DhmpControlMessageType type,
        byte dataWireVersion,
        int recordSize,
        int maximumReceivePayloadBytes,
        Guid schemaId,
        uint correlationId,
        DhmpControlRejectReason rejectReason = DhmpControlRejectReason.None)
    {
        if (type is not DhmpControlMessageType.Hello and
            not DhmpControlMessageType.Accept and
            not DhmpControlMessageType.Reject)
            throw new ArgumentOutOfRangeException(nameof(type));

        if (dataWireVersion == 0)
            throw new ArgumentOutOfRangeException(nameof(dataWireVersion));

        if (recordSize <= 0 || recordSize > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(recordSize));

        if (maximumReceivePayloadBytes < recordSize ||
            maximumReceivePayloadBytes > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumReceivePayloadBytes));

        if (correlationId == 0)
            throw new ArgumentOutOfRangeException(nameof(correlationId));

        if (!Enum.IsDefined(rejectReason))
            throw new ArgumentOutOfRangeException(nameof(rejectReason));

        if (type == DhmpControlMessageType.Reject &&
            rejectReason == DhmpControlRejectReason.None)
            throw new ArgumentException(
                "A reject message requires a rejection reason.",
                nameof(rejectReason));

        if (type != DhmpControlMessageType.Reject &&
            rejectReason != DhmpControlRejectReason.None)
            throw new ArgumentException(
                "Only reject messages may carry a rejection reason.",
                nameof(rejectReason));

        Type = type;
        DataWireVersion = dataWireVersion;
        RecordSize = recordSize;
        MaximumReceivePayloadBytes = maximumReceivePayloadBytes;
        SchemaId = schemaId;
        CorrelationId = correlationId;
        RejectReason = rejectReason;
    }

    public DhmpControlMessageType Type { get; }
    public byte DataWireVersion { get; }
    public int RecordSize { get; }
    public int MaximumReceivePayloadBytes { get; }
    public Guid SchemaId { get; }
    public uint CorrelationId { get; }
    public DhmpControlRejectReason RejectReason { get; }

    public static DhmpControlMessage Hello(
        DhmpPeerProfile profile,
        uint correlationId)
    {
        profile.Validate();

        return new(
            DhmpControlMessageType.Hello,
            profile.WireContract.Version,
            profile.WireContract.RecordSize,
            profile.MaximumReceivePayloadBytes,
            profile.SchemaId,
            correlationId);
    }

    public static DhmpControlMessage Accept(
        DhmpPeerProfile profile,
        uint correlationId)
    {
        profile.Validate();

        return new(
            DhmpControlMessageType.Accept,
            profile.WireContract.Version,
            profile.WireContract.RecordSize,
            profile.MaximumReceivePayloadBytes,
            profile.SchemaId,
            correlationId);
    }

    public static DhmpControlMessage Reject(
        DhmpPeerProfile localProfile,
        uint correlationId,
        DhmpControlRejectReason reason)
    {
        localProfile.Validate();

        return new(
            DhmpControlMessageType.Reject,
            localProfile.WireContract.Version,
            localProfile.WireContract.RecordSize,
            localProfile.MaximumReceivePayloadBytes,
            localProfile.SchemaId,
            correlationId,
            reason);
    }
}
