using System.Net;
using System.Net.Sockets;
using DHMP.Protocol;

namespace DHMP.RawIpv6;

/// <summary>
/// Linux raw-IPv6 implementation of the DHMP packet-sender boundary.
/// The IPv6 kernel API owns the IPv6 header; DHMP supplies payload bytes only.
/// The native socket is configured not to insert IPv6 Fragment headers.
/// </summary>
public sealed class DhmpRawIpv6PacketSender : IDhmpDynamicPacketSender, IDhmpPathBudgetTarget, IDisposable
{
    private readonly Socket _socket;
    private readonly EndPoint _remoteEndPoint;
    private readonly bool _dynamicPathBudgetEnabled;
    private readonly int _dynamicAdditionalIpv6HeaderBytes;
    private int _currentMaximumPayloadBytes;
    private int _disposed;

    public DhmpRawIpv6PacketSender(DhmpRawIpv6Options options)
        : this(
            options,
            initialMaximumPayloadBytes: null,
            dynamicPathBudgetEnabled: false,
            dynamicAdditionalIpv6HeaderBytes: 0)
    {
    }

    private DhmpRawIpv6PacketSender(
        DhmpRawIpv6Options options,
        int? initialMaximumPayloadBytes,
        bool dynamicPathBudgetEnabled,
        int dynamicAdditionalIpv6HeaderBytes)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsureSupportedPlatform();
        options.EnsureExperimentalProtocolNumbersEnabled();

        MaximumPayloadBytes = options.MaximumPayloadBytes;
        _currentMaximumPayloadBytes =
            initialMaximumPayloadBytes ??
            MaximumPayloadBytes;
        _dynamicPathBudgetEnabled =
            dynamicPathBudgetEnabled;
        _dynamicAdditionalIpv6HeaderBytes =
            dynamicAdditionalIpv6HeaderBytes;

        _remoteEndPoint = new IPEndPoint(options.RemoteAddress, 0);

        _socket = DhmpLinuxRawIpv6Socket.Open(options.DataProtocolNumber);

        try
        {
            _socket.SendBufferSize = options.SocketBufferBytes;
            _socket.Bind(new IPEndPoint(options.LocalAddress, 0));
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    public int MaximumPayloadBytes { get; }

    public int CurrentMaximumPayloadBytes =>
        Volatile.Read(ref _currentMaximumPayloadBytes);

    public bool DynamicPathBudgetEnabled =>
        _dynamicPathBudgetEnabled;

    public int DynamicAdditionalIpv6HeaderBytes =>
        _dynamicAdditionalIpv6HeaderBytes;

    /// <summary>
    /// Create a sender whose live payload ceiling starts at the IPv6 minimum-path
    /// budget while retaining the options payload limit as the immutable hard ceiling.
    /// Authenticated DPLPMTUD may raise the live ceiling later.
    /// </summary>
    public static DhmpRawIpv6PacketSender ForDynamicPath(
        DhmpRawIpv6Options options,
        int additionalIpv6HeaderBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(options);

        var baseBudget =
            new DhmpIpv6PathBudget(
                DhmpIpv6PathBudget.MinimumIpv6Mtu,
                additionalIpv6HeaderBytes);

        if (baseBudget.MaximumProtocolPayloadBytes >
            options.MaximumPayloadBytes)
            throw new ArgumentException(
                "Configured raw sender ceiling is smaller than the IPv6 minimum-path payload budget.",
                nameof(options));

        return new DhmpRawIpv6PacketSender(
            options,
            baseBudget.MaximumProtocolPayloadBytes,
            dynamicPathBudgetEnabled: true,
            dynamicAdditionalIpv6HeaderBytes:
                additionalIpv6HeaderBytes);
    }

    internal void ApplyConfirmedPathBudget(
        DhmpIpv6PathBudget pathBudget)
    {
        if (!_dynamicPathBudgetEnabled)
            throw new InvalidOperationException(
                "This raw sender was not created for dynamic path-budget management.");

        if (pathBudget.AdditionalIpv6HeaderBytes !=
            _dynamicAdditionalIpv6HeaderBytes)
            throw new ArgumentException(
                "Path budget uses a different IPv6 extension-header allowance.",
                nameof(pathBudget));

        int next =
            pathBudget.MaximumProtocolPayloadBytes;

        if (next <= 0 ||
            next > MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(
                nameof(pathBudget),
                "Confirmed path budget exceeds the raw sender hard ceiling.");

        Volatile.Write(
            ref _currentMaximumPayloadBytes,
            next);
    }

    internal void FallBackToMinimumPathBudget()
    {
        if (!_dynamicPathBudgetEnabled)
            throw new InvalidOperationException(
                "This raw sender was not created for dynamic path-budget management.");

        var baseBudget =
            new DhmpIpv6PathBudget(
                DhmpIpv6PathBudget.MinimumIpv6Mtu,
                _dynamicAdditionalIpv6HeaderBytes);

        Volatile.Write(
            ref _currentMaximumPayloadBytes,
            baseBudget.MaximumProtocolPayloadBytes);
    }

    void IDhmpPathBudgetTarget.ApplyConfirmedPathBudget(
        DhmpIpv6PathBudget pathBudget)
        => ApplyConfirmedPathBudget(
            pathBudget);

    void IDhmpPathBudgetTarget.FallBackToMinimumPathBudget()
        => FallBackToMinimumPathBudget();

    public async ValueTask SendPacketAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        int currentMaximum =
            CurrentMaximumPayloadBytes;

        if (payload.IsEmpty || payload.Length > currentMaximum)
            throw new DhmpProtocolException(
                $"Raw IPv6 sender received an empty or oversized DHMP packet payload. Current live ceiling is {currentMaximum} bytes.");

        int sent;

        try
        {
            sent = await _socket.SendToAsync(
                payload,
                SocketFlags.None,
                _remoteEndPoint,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException error)
            when (error.SocketErrorCode == SocketError.MessageSize)
        {
            throw new DhmpPathMtuException(
                payload.Length,
                currentMaximum,
                error);
        }

        if (sent != payload.Length)
            throw new IOException(
                $"Raw IPv6 socket accepted {sent} of {payload.Length} DHMP payload bytes.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _socket.Dispose();
    }

    private static void EnsureSupportedPlatform()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(
                "The first DHMP raw IPv6 backend is Linux-only. Other OS backends require separately validated socket semantics.");
    }
}

