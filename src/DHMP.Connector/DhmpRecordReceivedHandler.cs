namespace DHMP.Connector;

/// <summary>
/// Synchronous zero-copy receive callback. The record span is valid only for the duration of the callback.
/// Copy it if the application needs to retain the bytes.
/// </summary>
public delegate void DhmpRecordReceivedHandler(
    DhmpConnection connection,
    ReadOnlySpan<byte> record);
