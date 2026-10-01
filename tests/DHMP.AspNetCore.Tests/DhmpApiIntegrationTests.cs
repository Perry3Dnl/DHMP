using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DHMP.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DHMP.AspNetCore.Tests;

public sealed class DhmpApiIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static DhmpApiOptions Settings(bool accept = true) => new()
    {
        LocalAddress = "2001:db8::1", RemoteAddress = "2001:db8::2", ApiOrigin = "https://api.example/",
        PreSharedKeyBase64 = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
        EnableExperimentalProtocolNumbers = true,
        AcceptRequests = accept, RequestTimeout = TimeSpan.FromSeconds(5)
    };

    [Fact]
    public void RawApiExperimentalBindingIsDisabledByDefault()
    {
        var settings = Settings();
        settings.EnableExperimentalProtocolNumbers = false;

        var error = Assert.Throws<InvalidOperationException>(() => settings.Validate());

        Assert.Contains(
            "EnableExperimentalProtocolNumbers=true",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneRegistrationRoutesExistingFactoryCallsThroughExistingMinimalApiPipeline()
    {
        using var issuer = new TestLicenseIssuer();
        var pair = new PacketPair();
        await using var server = BuildApp(issuer, pair.Second, accept: true);
        await using var frontend = BuildApp(issuer, pair.First, accept: false);
        server.Services.GetRequiredService<DhmpApiPipeline>();
        server.MapPost("/echo", async (HttpContext context, ScopedMarker marker) =>
        {
            context.Response.StatusCode = 201;
            context.Response.Headers["X-Scope"] = marker.Id.ToString();
            context.Response.Headers["X-Query"] = context.Request.Query["q"].ToString();
            context.Response.OnStarting(() => { context.Response.Headers["X-Starting"] = "yes"; return Task.CompletedTask; });
            using var reader = new StreamReader(context.Request.Body);
            await context.Response.WriteAsync(await reader.ReadToEndAsync(context.RequestAborted), context.RequestAborted);
        });
        await server.StartAsync(Token);
        await frontend.StartAsync(Token);
        using var client = frontend.Services.GetRequiredService<IHttpClientFactory>().CreateClient();
        string payload = new('x', 10000); // Multiple complete application records.
        using var first = await client.PostAsync("https://api.example/echo?q=unchanged", new StringContent(payload), Token);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(payload, await first.Content.ReadAsStringAsync(Token));
        Assert.Equal("unchanged", first.Headers.GetValues("X-Query").Single());
        Assert.Equal("yes", first.Headers.GetValues("X-Starting").Single());
        using var second = await client.PostAsync("https://api.example/echo?q=next", new StringContent("second"), Token);
        Assert.NotEqual(first.Headers.GetValues("X-Scope").Single(), second.Headers.GetValues("X-Scope").Single());
        Assert.True(pair.First.Sent > 2);
        await frontend.StopAsync(Token);
        await server.StopAsync(Token);
        Assert.True(pair.First.Disposed);
        Assert.True(pair.Second.Disposed);
    }

    [Fact]
    public async Task MiddlewareAuthorizationStatusAndHeadersArePreserved()
    {
        using var issuer = new TestLicenseIssuer();
        var pair = new PacketPair();
        await using var server = BuildApp(issuer, pair.Second, true);
        await using var frontend = BuildApp(issuer, pair.First, false);
        server.Use(async (context, next) =>
        {
            if (context.Request.Headers.Authorization != "Bearer sample") { context.Response.StatusCode = 401; return; }
            await next(context);
        });
        server.MapGet("/item/{id}", (int id) => Results.Json(new { id }));
        await server.StartAsync(Token); await frontend.StartAsync(Token);
        using var client = frontend.Services.GetRequiredService<IHttpClientFactory>().CreateClient();
        using var denied = await client.GetAsync("https://api.example/item/42", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "sample");
        using var allowed = await client.GetAsync("https://api.example/item/42", Token);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var json = JsonDocument.Parse(await allowed.Content.ReadAsByteArrayAsync(Token));
        Assert.Equal(42, json.RootElement.GetProperty("id").GetInt32());
        await frontend.StopAsync(Token); await server.StopAsync(Token);
    }

    [Fact]
    public async Task ExistingControllerModelBindingAndResponseSerializationWorkUnchanged()
    {
        using var issuer = new TestLicenseIssuer();
        var pair = new PacketPair();
        await using var server = BuildApp(issuer, pair.Second, true);
        await using var frontend = BuildApp(issuer, pair.First, false);
        server.MapControllers();
        await server.StartAsync(Token); await frontend.StartAsync(Token);
        using var client = frontend.Services.GetRequiredService<IHttpClientFactory>().CreateClient();
        using var response = await client.PostAsJsonAsync("https://api.example/api/dhmp-test", new DhmpApiTestBody("existing-controller"), Token);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("existing-controller", (await response.Content.ReadFromJsonAsync<DhmpApiTestBody>(Token))!.Value);
        await frontend.StopAsync(Token); await server.StopAsync(Token);
    }

    [Fact]
    public async Task MinimalApiJsonModelBindingRecognizesApplicationRequestBody()
    {
        using var issuer = new TestLicenseIssuer();
        var pair = new PacketPair();
        await using var server = BuildApp(issuer, pair.Second, true);
        await using var frontend = BuildApp(issuer, pair.First, false);
        server.MapPost("/json", (DhmpApiTestBody body) => Results.Json(body));
        await server.StartAsync(Token); await frontend.StartAsync(Token);
        using var client = frontend.Services.GetRequiredService<IHttpClientFactory>().CreateClient();
        string value = new('x', 16000);
        using var response = await client.PostAsJsonAsync("https://api.example/json", new DhmpApiTestBody(value), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(value, (await response.Content.ReadFromJsonAsync<DhmpApiTestBody>(Token))!.Value);
        using var empty = await client.PostAsync("https://api.example/json", new ByteArrayContent([]), Token);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        await frontend.StopAsync(Token); await server.StopAsync(Token);
    }

    [Fact]
    public async Task ApplicationExceptionReturnsGeneric500WithoutRetryOrLeakedDetails()
    {
        using var issuer = new TestLicenseIssuer();
        var pair = new PacketPair();
        await using var server = BuildApp(issuer, pair.Second, true);
        await using var frontend = BuildApp(issuer, pair.First, false);
        int calls = 0;
        server.MapGet("/failure", (Func<string>)(() => { calls++; throw new InvalidOperationException("private-secret-detail"); }));
        await server.StartAsync(Token); await frontend.StartAsync(Token);
        using var client = frontend.Services.GetRequiredService<IHttpClientFactory>().CreateClient();
        using var response = await client.GetAsync("https://api.example/failure", Token);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("private-secret-detail", await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(1, calls);
        await frontend.StopAsync(Token); await server.StopAsync(Token);
    }

    [Fact]
    public async Task ShutdownJoinsBlockedBackendBeforeCompletingRetirement()
    {
        var settings = Settings();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var exchange = new DhmpApiExchange(settings, async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(Token);
        }, (_, _) => Task.FromResult(new byte[] { 1 }));
        Task<byte[]> pending = exchange.RequestAsync(new byte[] { 1 }, Token);
        await entered.Task.WaitAsync(Token);
        Task retirement = exchange.DisposeAsync().AsTask();
        Assert.False(retirement.IsCompleted);
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await retirement.WaitAsync(Token);
        Assert.Equal(0, exchange.PendingCount);
    }

    [Fact]
    public async Task InvalidLicensePreventsTransportCreation()
    {
        using var issuer = new TestLicenseIssuer();
        var packet = new PacketPair().First;
        await using var app = BuildApp(issuer, packet, false, invalidLicense: true);
        await Assert.ThrowsAsync<DhmpLicenseException>(() => app.StartAsync(Token));
        Assert.Equal(0, packet.Opened);
    }

    [Fact]
    public async Task UnmappedOriginsUseOriginalHandlerButMappedFailureNeverFallsBack()
    {
        var settings = Settings();
        var exchange = new FailingExchange();
        var original = new RecordingHandler();
        using var handler = new DhmpApiHttpMessageHandler(exchange, settings) { InnerHandler = original };
        using var invoker = new HttpMessageInvoker(handler);
        using var outside = new HttpRequestMessage(HttpMethod.Get, "https://other.example/path");
        using var response = await invoker.SendAsync(outside, Token);
        Assert.Equal(1, original.Calls);
        using var mapped = new HttpRequestMessage(HttpMethod.Post, "https://api.example/mutation");
        await Assert.ThrowsAsync<IOException>(() => invoker.SendAsync(mapped, Token));
        Assert.Equal(1, original.Calls);
        Assert.Equal(1, exchange.Calls);
    }

    [Fact]
    public async Task RequestBodyLimitIsEnforcedBeforeAnyPacketSubmission()
    {
        var settings = Settings(); settings.MaximumBodyBytes = 16;
        var exchange = new FailingExchange();
        using var handler = new DhmpApiHttpMessageHandler(exchange, settings);
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example/path") { Content = new ByteArrayContent(new byte[17]) };
        await Assert.ThrowsAsync<InvalidDataException>(() => invoker.SendAsync(request, Token));
        Assert.Equal(0, exchange.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostRequestOrResponseTimesOutWithoutRetry(bool loseResponse)
    {
        var settings = Settings(); settings.RequestTimeout = TimeSpan.FromMilliseconds(100);
        int packets = 0, calls = 0;
        DhmpApiExchange? client = null;
        await using var server = new DhmpApiExchange(settings, (_, _) => ValueTask.CompletedTask,
            (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(new byte[] { 1 }); });
        client = new DhmpApiExchange(settings, (record, _) =>
        {
            packets++;
            if (loseResponse) server.Receive(record.Span);
            return ValueTask.CompletedTask;
        }, (_, _) => Task.FromResult(new byte[] { 1 }));
        await using (client)
        {
            await Assert.ThrowsAsync<TimeoutException>(() => client.RequestAsync(new byte[] { 2 }, Token));
            Assert.Equal(1, packets);
            Assert.Equal(loseResponse ? 1 : 0, calls);
            Assert.Equal(0, client.PendingCount);
            Assert.Equal(0, client.AssemblyCount);
        }
    }

    [Fact]
    public async Task FragmentReorderingAndDuplicationStillExecuteExactlyOnce()
    {
        var settings = Settings(); int executions = 0;
        var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new DhmpApiExchange(settings, (record, _) => { response.TrySetResult(record.ToArray()); return ValueTask.CompletedTask; },
            (bytes, _) => { executions++; return Task.FromResult(bytes[..16]); });
        byte[] message = Enumerable.Range(0, 4000).Select(i => (byte)(i % 256)).ToArray();
        Guid id = DhmpApiRecord.RequestId(1);
        var records = Enumerable.Range(0, 4).Select(i => DhmpApiRecord.Encode(1, id, message, i)).ToArray();
        foreach (int index in new[] { 3, 1, 1, 0, 2, 2, 0, 3 }) server.Receive(records[index]);
        await response.Task.WaitAsync(Token);
        Assert.Equal(1, executions);
        Assert.Equal(0, server.AssemblyCount);
        // Even fresh encrypted packets carrying the same complete application request cannot execute it again.
        foreach (var record in records) server.Receive(record);
        Assert.Equal(0, server.AssemblyCount);
    }

    [Fact]
    public async Task ConflictingFragmentsAreDiscardedAndCannotRestartRequestExecution()
    {
        var settings = Settings(); int calls = 0;
        await using var exchange = new DhmpApiExchange(settings, (_, _) => ValueTask.CompletedTask,
            (_, _) => { calls++; return Task.FromResult(new byte[] { 1 }); });
        byte[] data = new byte[2000]; Guid id = DhmpApiRecord.RequestId(1);
        var first = DhmpApiRecord.Encode(1, id, data, 0);
        exchange.Receive(first); first[32] = 1; exchange.Receive(first);
        exchange.Receive(DhmpApiRecord.Encode(1, id, data, 1));
        Assert.Equal(0, calls); Assert.Equal(0, exchange.AssemblyCount); Assert.Equal(1, exchange.RejectedRecords);
    }

    [Fact]
    public async Task IncompleteAssemblyExpiresWithinItsOriginalDeadline()
    {
        var settings = Settings(); var clock = new ManualClock();
        await using var exchange = new DhmpApiExchange(settings, (_, _) => ValueTask.CompletedTask,
            (_, _) => Task.FromResult(new byte[] { 1 }), clock);
        exchange.Receive(DhmpApiRecord.Encode(1, DhmpApiRecord.RequestId(1), new byte[2000], 0));
        Assert.Equal(1, exchange.AssemblyCount);
        clock.Now += settings.RequestTimeout;
        exchange.Expire();
        Assert.Equal(0, exchange.AssemblyCount);
        exchange.Receive(DhmpApiRecord.Encode(1, DhmpApiRecord.RequestId(1), new byte[2000], 1));
        Assert.Equal(0, exchange.AssemblyCount);
    }

    [Fact]
    public async Task RequestAdmissionCancellationAndRetirementRemainBounded()
    {
        var settings = Settings(); settings.MaximumInFlight = 1;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var exchange = new DhmpApiExchange(settings, (_, _) => { entered.TrySetResult(); return ValueTask.CompletedTask; },
            (_, _) => Task.FromResult(new byte[] { 1 }));
        Task<byte[]> pending = exchange.RequestAsync(new byte[] { 1 }, Token);
        await entered.Task.WaitAsync(Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => exchange.RequestAsync(new byte[] { 1 }, Token));
        await exchange.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.Equal(0, exchange.PendingCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => exchange.RequestAsync(new byte[] { 1 }, Token));
    }

    [Theory]
    [InlineData(0)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(24)] [InlineData(28)] [InlineData(30)] [InlineData(1199)]
    public void MalformedApplicationRecordIsRejectedBeforeAssembly(int offset)
    {
        var record = DhmpApiRecord.Encode(1, DhmpApiRecord.RequestId(1), new byte[10], 0);
        record[offset] ^= 0xff;
        Assert.False(DhmpApiRecord.TryParse(record, 5000, out _));
    }

    [Fact]
    public void RequestExecutionReplayWindowIsBoundedAcrossLongSessions()
    {
        var window = new DhmpApiRequestWindow();
        Assert.False(window.TryAccept(0));
        for (ulong i = 1; i <= 100000; i++) Assert.True(window.TryAccept(i));
        Assert.False(window.TryAccept(1));
        Assert.False(window.TryAccept(100000));
        var reordered = new DhmpApiRequestWindow();
        Assert.True(reordered.TryAccept(1025)); Assert.True(reordered.TryAccept(2));
        Assert.False(reordered.TryAccept(1)); Assert.False(reordered.TryAccept(2));
        Assert.True(reordered.TryAccept(1026)); Assert.False(reordered.TryAccept(2));
    }

    [Theory]
    [InlineData("https://other.example/")]
    [InlineData("http://api.example/")]
    [InlineData("https://api.example:8443/")]
    public void OriginMappingDoesNotSilentlyCaptureOtherDestinations(string address) => Assert.False(Settings().Matches(new Uri(address)));

    [Theory]
    [InlineData("//evil.example/path")]
    [InlineData("/path#fragment")]
    [InlineData("/path\\escape")]
    public async Task AuthorityAndAmbiguousTargetsAreRejected(string target)
    {
        var pipeline = new DhmpApiPipeline(Options.Create(Settings()));
        byte[] bytes = await DhmpApiEnvelope.SerializeAsync(new() { Method = "GET", Target = target }, 5000, Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => pipeline.DispatchAsync(bytes, Token));
    }

    [Fact]
    public void InvalidPeerAndMemoryBudgetFailValidation()
    {
        var settings = Settings(); settings.Validate();
        settings.RemoteAddress = "127.0.0.1";
        Assert.Throws<InvalidOperationException>(() => settings.Validate());
        settings = Settings(); settings.MaximumInFlight = 128; settings.MaximumMessageBytes = 2097152;
        Assert.Throws<InvalidOperationException>(() => settings.Validate());
        settings = Settings(); settings.AdditionalIpv6HeaderBytes = 32;
        Assert.Throws<InvalidOperationException>(() => settings.Validate());
    }

    private static WebApplication BuildApp(TestLicenseIssuer issuer, PacketTransport transport, bool accept, bool invalidLicense = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        Guid appId = Guid.NewGuid();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DHMP:ApplicationId"] = appId.ToString(), ["DHMP:PublicVerificationKeyBase64"] = Convert.ToBase64String(issuer.PublicKey),
            ["DHMP:Api:LocalAddress"] = "2001:db8::1", ["DHMP:Api:RemoteAddress"] = "2001:db8::2",
            ["DHMP:Api:ApiOrigin"] = "https://api.example/", ["DHMP:Api:PreSharedKeyBase64"] = Settings().PreSharedKeyBase64,
            ["DHMP:Api:AcceptRequests"] = accept.ToString(), ["DHMP:Api:RequestTimeout"] = "00:00:05"
        });
        builder.Services.AddSingleton<IServer>(new StubServer());
        builder.Services.AddSingleton<IDhmpApiTransportFactory>(new PacketFactory(transport));
        builder.Services.AddScoped<ScopedMarker>();
        builder.Services.AddControllers().AddApplicationPart(typeof(DhmpApiTestController).Assembly);
        builder.Services.AddDHMP(invalidLicense ? "invalid" : issuer.Issue(appId));
        return builder.Build();
    }
    private sealed class ScopedMarker { public Guid Id { get; } = Guid.NewGuid(); }
    private sealed class StubServer : IServer
    {
        public IFeatureCollection Features { get; } = new FeatureCollection();
        public StubServer() => Features.Set<IServerAddressesFeature>(new ServerAddressesFeature());
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }
    private sealed class PacketFactory(PacketTransport transport) : IDhmpApiTransportFactory
    {
        public Task<IDhmpApiTransport> OpenAsync(DhmpApiOptions options, CancellationToken token)
        { token.ThrowIfCancellationRequested(); transport.Opened++; return Task.FromResult<IDhmpApiTransport>(transport); }
    }
    private sealed class PacketPair
    {
        internal PacketTransport First { get; } = new();
        internal PacketTransport Second { get; } = new();
        internal PacketPair() { First.Peer = Second; Second.Peer = First; }
    }
    private sealed class PacketTransport : IDhmpApiTransport
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>(); // Test-only paired packet boundary.
        internal PacketTransport Peer { get; set; } = null!;
        internal int Sent, Opened;
        internal bool Disposed;
        public ValueTask SendAsync(ReadOnlyMemory<byte> record, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Sent++; Peer._incoming.Writer.TryWrite(record.ToArray()); return ValueTask.CompletedTask; }
        public async Task RunAsync(Action<ReadOnlySpan<byte>> publish, CancellationToken token)
        { await foreach (var packet in _incoming.Reader.ReadAllAsync(token)) publish(packet); }
        public ValueTask DisposeAsync() { Disposed = true; _incoming.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }
    private sealed class FailingExchange : IDhmpApiExchange
    {
        internal int Calls;
        public Task<byte[]> RequestAsync(byte[] message, CancellationToken cancellationToken)
        { Calls++; return Task.FromException<byte[]>(new IOException("DHMP failed")); }
    }
    private sealed class ManualClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now.UtcTicks;
    }
}

public sealed record DhmpApiTestBody(string Value);

[ApiController]
[Route("api/dhmp-test")]
public sealed class DhmpApiTestController : ControllerBase
{
    [HttpPost]
    public ActionResult<DhmpApiTestBody> Post(DhmpApiTestBody body) => StatusCode(202, body);
}
