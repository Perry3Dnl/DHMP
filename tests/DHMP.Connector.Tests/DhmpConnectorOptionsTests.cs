using System.Net;
using DHMP.Connector;
using DHMP.Protocol;
using DHMP.Security;
using Xunit;

namespace DHMP.Connector.Tests;

public sealed class DhmpConnectorOptionsTests
{
    [Fact]
    public void Plaintext_requires_explicit_opt_in()
    {
        var options = new DhmpConnectorOptions(
            IPAddress.IPv6Loopback,
            recordSize: 32,
            Guid.NewGuid())
        {
            EnableExperimentalProtocolNumbers = true
        };

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(
                options.Validate);

        Assert.Contains(
            "AllowUnprotectedPayloads",
            error.Message);
    }

    [Fact]
    public void Protected_configuration_builds_without_raw_socket_access()
    {
        using var key =
            new DhmpPreSharedKey(
                7,
                new byte[DhmpPreSharedKey.KeySizeBytes]);

        var options = new DhmpConnectorOptions(
            IPAddress.IPv6Loopback,
            recordSize: 32,
            Guid.NewGuid())
        {
            PreSharedKey = key,
            EnableExperimentalProtocolNumbers = true
        };

        using var connector =
            new AsyncDisposableAdapter(
                new DhmpConnector(options));

        Assert.Equal(
            32,
            connector.Value.RecordSize);

        Assert.Equal(
            IPAddress.IPv6Loopback,
            connector.Value.LocalAddress);

        Assert.Empty(
            connector.Value.Connections);
    }

    [Fact]
    public void Payload_budget_must_fit_one_protected_record()
    {
        using var key =
            new DhmpPreSharedKey(
                9,
                new byte[DhmpPreSharedKey.KeySizeBytes]);

        var options = new DhmpConnectorOptions(
            IPAddress.IPv6Loopback,
            recordSize: 1230,
            Guid.NewGuid())
        {
            MaximumPayloadBytes = 1240,
            PreSharedKey = key,
            EnableExperimentalProtocolNumbers = true
        };

        Assert.Throws<ArgumentException>(
            () => new DhmpConnector(options));
    }

    [Fact]
    public void Connector_defaults_to_smooth_pacing_and_sequential_receive()
    {
        var options = new DhmpConnectorOptions(
            IPAddress.IPv6Loopback,
            recordSize: 32,
            Guid.NewGuid())
        {
            AllowUnprotectedPayloads = true,
            EnableExperimentalProtocolNumbers = true
        };

        Assert.Equal(
            DhmpRatePolicy.SmoothPacing,
            options.RatePolicy);

        Assert.Equal(
            DhmpProcessingMode.Sequential,
            options.ReceiveMode);
    }

    [Fact]
    public void Duplicate_source_resolution_requires_psk_and_field()
    {
        var noKey = new DhmpConnectorOptions(
            IPAddress.IPv6Loopback,
            recordSize: 32,
            Guid.NewGuid())
        {
            AllowUnprotectedPayloads = true,
            DuplicatePeerHandling =
                DhmpDuplicatePeerHandling.ResolveWithConnectionId,
            ConnectionIdField =
                new DhmpConnectionIdField(0)
        };

        Assert.Throws<InvalidOperationException>(
            noKey.Validate);

        using var key =
            new DhmpPreSharedKey(
                11,
                new byte[DhmpPreSharedKey.KeySizeBytes]);

        var noField = new DhmpConnectorOptions(
            IPAddress.IPv6Loopback,
            recordSize: 32,
            Guid.NewGuid())
        {
            PreSharedKey = key,
            DuplicatePeerHandling =
                DhmpDuplicatePeerHandling.ResolveWithConnectionId
        };

        Assert.Throws<InvalidOperationException>(
            noField.Validate);
    }

    [Fact]
    public void Duplicate_source_connection_field_must_fit_record()
    {
        using var key =
            new DhmpPreSharedKey(
                12,
                new byte[DhmpPreSharedKey.KeySizeBytes]);

        var options = new DhmpConnectorOptions(
            IPAddress.IPv6Loopback,
            recordSize: 16,
            Guid.NewGuid())
        {
            PreSharedKey = key,
            DuplicatePeerHandling =
                DhmpDuplicatePeerHandling.ResolveWithConnectionId,
            ConnectionIdField =
                new DhmpConnectionIdField(9)
        };

        Assert.Throws<ArgumentOutOfRangeException>(
            options.Validate);
    }

    private sealed class AsyncDisposableAdapter :
        IDisposable
    {
        public AsyncDisposableAdapter(
            DhmpConnector value)
        {
            Value = value;
        }

        public DhmpConnector Value { get; }

        public void Dispose() =>
            Value.DisposeAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
    }
}
