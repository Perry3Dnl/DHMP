namespace DHMP.Connector;

/// <summary>
/// Synchronous zero-copy callback for one registered plaintext BlindFire record.
/// The record span is valid only for the duration of the callback.
/// </summary>
public delegate void DhmpBlindFireReceivedHandler(
    DhmpBlindFireRegistration registration,
    ReadOnlySpan<byte> record);
