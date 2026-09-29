namespace DHMP.Protocol;

public enum DhmpControlRejectReason : byte
{
    None = 0,
    UnsupportedWireVersion = 1,
    RecordSizeMismatch = 2,
    SchemaMismatch = 3,
    ReceiveLimitTooSmall = 4,
    UnexpectedMessage = 5
}
