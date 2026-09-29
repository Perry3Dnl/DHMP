namespace DHMP.Security;

public enum DhmpSecuritySuite : byte
{
    PskChaCha20Poly1305HkdfSha256 = 1
}

public enum DhmpSecurityRole
{
    Initiator = 0,
    Responder = 1
}

public enum DhmpSecurityControlType : byte
{
    Offer = 1,
    Accept = 2,
    Reject = 3
}

public enum DhmpSecurityRejectReason : byte
{
    None = 0,
    UnsupportedSuite = 1,
    KeyIdMismatch = 2,
    InvalidSession = 3
}
