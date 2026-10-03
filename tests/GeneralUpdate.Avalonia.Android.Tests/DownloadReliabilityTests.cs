using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using GeneralUpdate.Avalonia.Android.Models;
using GeneralUpdate.Avalonia.Android.Services;
using Xunit;

namespace GeneralUpdate.Avalonia.Android.Tests;

public sealed class DownloadReliabilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gu-retry-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _bytes = Enumerable.Range(0, 8192).Select(i => (byte)i).ToArray();
    private UpdatePackageInfo Package => TestBootstrap.Package with
    {
        Sha256 = Convert.ToHexString(SHA256.HashData(_bytes)),
        FileName = "update.apk",
        FileSize = _bytes.Length
    };
    private static HttpDownloadOptions Policy => new() { MaxRetryAttempts = 3, RetryBaseDelay = TimeSpan.Zero };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TransientHeadOrGetFailure_RetriesWholeAttempt(bool failHead)
    {
        var heads = 0;
        var gets = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            var head = request.Method == HttpMethod.Head;
            var count = head ? ++heads : ++gets;
            return Task.FromResult(head == failHead && count < 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Response(head));
        }));
        using var downloader = Create(http);
        var result = await downloader.DownloadAsync(Package, null);
        Assert.True(result.Success, result.Exception?.ToString());
        Assert.Equal(3, heads);
        Assert.Equal(failHead ? 1 : 3, gets);
        Assert.Equal(_bytes, await File.ReadAllBytesAsync(result.FilePath!));
    }

    [Theory]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task UnsupportedHead_FallsBackToGet(HttpStatusCode status)
    {
        var gets = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
                return Task.FromResult(new HttpResponseMessage(status));
            gets++;
            return Task.FromResult(Response(false));
        }));
        using var downloader = Create(http);
        Assert.True((await downloader.DownloadAsync(Package, null)).Success);
        Assert.Equal(1, gets);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 1)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 3)]
    public async Task RetryPolicy_StopsAtPermanentFailureOrAttemptLimit(HttpStatusCode status, int expectedGets)
    {
        var gets = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Head) return Task.FromResult(Response(true));
            gets++;
            return Task.FromResult(new HttpResponseMessage(status));
        }));
        using var downloader = Create(http);
        var result = await downloader.DownloadAsync(Package, null);
        Assert.False(result.Success);
        Assert.Equal(UpdateFailureReason.NetworkError, result.FailureReason);
        Assert.Equal(expectedGets, gets);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(8192)]
    public async Task InterruptedBody_RetryResumesFromFlushedPartialFile(int receivedBytes)
    {
        var gets = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Head) return Task.FromResult(Response(true));
            gets++;
            if (gets == 1)
            {
                var interrupted = Response(false);
                interrupted.Content.Dispose();
                interrupted.Content = new StreamContent(new InterruptedStream(_bytes, receivedBytes));
                interrupted.Content.Headers.ContentLength = _bytes.Length;
                return Task.FromResult(interrupted);
            }
            Assert.Equal(receivedBytes, request.Headers.Range!.Ranges.Single().From);
            var resumed = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(_bytes[receivedBytes..])
            };
            resumed.Content.Headers.ContentRange = new ContentRangeHeaderValue(receivedBytes, _bytes.Length - 1, _bytes.Length);
            return Task.FromResult(resumed);
        }));
        using var downloader = Create(http);
        var result = await downloader.DownloadAsync(Package, null);
        Assert.True(result.Success, result.Exception?.ToString());
        Assert.Equal(receivedBytes == _bytes.Length ? 1 : 2, gets);
        Assert.Equal(_bytes, await File.ReadAllBytesAsync(result.FilePath!));
        Assert.False(File.Exists(result.FilePath + ".part"));
        Assert.False(File.Exists(result.FilePath + ".part.json"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutAndUserCancellation_HaveDifferentResults(bool userCanceled)
    {
        using var cancellation = new CancellationTokenSource();
        var gets = 0;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Head) return Response(true);
            gets++;
            if (userCanceled) cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Cancellation must interrupt the request.");
        }));
        using var downloader = Create(http, Policy with { DownloadTimeout = TimeSpan.FromMilliseconds(100) });
        var result = await downloader.DownloadAsync(Package, null, cancellation.Token);
        Assert.False(result.Success);
        Assert.Equal(userCanceled ? UpdateState.Canceled : UpdateState.Failed, result.State);
        Assert.Equal(userCanceled ? UpdateFailureReason.Canceled : UpdateFailureReason.NetworkError, result.FailureReason);
        Assert.Equal(1, gets);
    }

    [Fact]
    public async Task ExternalClientAndRequestOptions_PreserveHandlerAndTimeout()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests++;
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization.Parameter);
            if (request.RequestUri!.AbsolutePath == "/check")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Package) });
            return Task.FromResult(Response(request.Method == HttpMethod.Head));
        }))
        { Timeout = TimeSpan.FromSeconds(77) };
        var policy = Policy with { AuthProvider = new BearerTokenAuthProvider("test-token") };
        using var source = HttpUpdatePackageClient.Create(http, policy, new SystemVersionComparer(),
            new UpdateServerOptions { RequestUrl = "https://example.test/check", UseJsonEndpoint = true });
        Assert.NotNull(await source.GetLatestAsync("1.0.0"));
        using var downloader = Create(http, policy);
        Assert.True((await downloader.DownloadAsync(Package, null)).Success);
        Assert.Equal(3, requests);
        Assert.Equal(TimeSpan.FromSeconds(77), http.Timeout);
        Assert.Throws<ArgumentException>(() => UpdateHttpClientFactory.Create(http,
            Policy with { UseProxy = true, Proxy = new WebProxy("http://localhost:8080") }, out _));
    }

    [Fact]
    public async Task CancellationDuringBackoff_StopsWithoutAnotherAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        var gets = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Head) return Task.FromResult(Response(true));
            gets++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }));
        using var downloader = Create(http, Policy with { RetryBaseDelay = TimeSpan.FromSeconds(5) });
        var downloading = downloader.DownloadAsync(Package, null, cancellation.Token);
        Assert.False(downloading.IsCompleted);
        cancellation.Cancel();
        var result = await downloading;
        Assert.Equal(UpdateState.Canceled, result.State);
        Assert.Equal(1, gets);
    }

    [Fact]
    public async Task MetadataTimeout_ReportsNetworkFailureNotCancellation()
    {
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Timeout must interrupt the request.");
        }));
        using var source = HttpUpdatePackageClient.Create(http, Policy with { RequestTimeout = TimeSpan.FromMilliseconds(100) },
            new SystemVersionComparer(), new UpdateServerOptions { RequestUrl = "https://example.test/check", UseJsonEndpoint = true });
        using var bootstrap = TestBootstrap.Create(source: source);
        var result = await bootstrap.ValidateAsync("1.0.0");
        Assert.False(result.Success);
        Assert.Equal(UpdateFailureReason.NetworkError, result.FailureReason);
        Assert.Equal(UpdateState.Failed, bootstrap.GetSnapshot().State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExhaustedTransportExceptions_AreReportedAsNetworkFailures(bool timeout)
    {
        var attempts = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            attempts++;
            throw timeout ? new TimeoutException("transport timeout") : new HttpRequestException("connection lost");
        }));
        using var downloader = Create(http);
        var result = await downloader.DownloadAsync(Package, null);
        Assert.False(result.Success);
        Assert.Equal(UpdateFailureReason.NetworkError, result.FailureReason);
        Assert.Equal(3, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bootstrap_DisposesOwnedClientButNotBorrowedClient(bool ownsClient)
    {
        var handler = new Handler((request, _) => Task.FromResult(Response(request.Method == HttpMethod.Head)));
        using var http = new HttpClient(handler);
        var downloader = new HttpResumableApkDownloader(http, new PhysicalFileStorage(),
            new AndroidUpdateOptions { DownloadDirectoryPath = _directory }, httpOptions: Policy, ownsClient: ownsClient);
        var source = new HttpUpdatePackageClient(http);
        using var bootstrap = TestBootstrap.Create(source: source, downloader: downloader);
        bootstrap.Dispose();
        bootstrap.Dispose();
        Assert.Equal(ownsClient ? 1 : 0, handler.DisposeCount);
    }

    [Fact]
    public async Task InternallyCreatedClient_TransfersOwnershipAndIsDisposed()
    {
        var http = UpdateHttpClientFactory.Create(null, Policy, out var ownsClient);
        Assert.True(ownsClient);
        using var downloader = new HttpResumableApkDownloader(http, new PhysicalFileStorage(),
            new AndroidUpdateOptions { DownloadDirectoryPath = _directory }, httpOptions: Policy, ownsClient: ownsClient);
        using var bootstrap = TestBootstrap.Create(downloader: downloader);
        bootstrap.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => http.GetAsync("https://example.test/"));
    }

    private HttpResumableApkDownloader Create(HttpClient http, HttpDownloadOptions? policy = null) =>
        new(http, new PhysicalFileStorage(), new AndroidUpdateOptions { DownloadDirectoryPath = _directory },
            httpOptions: policy ?? Policy);

    private HttpResponseMessage Response(bool head)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(head ? [] : _bytes)
        };
        response.Content.Headers.ContentLength = _bytes.Length;
        response.Headers.ETag = new EntityTagHeaderValue("\"stable\"");
        response.Headers.AcceptRanges.Add("bytes");
        return response;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int DisposeCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
        protected override void Dispose(bool disposing) { if (disposing) DisposeCount++; base.Dispose(disposing); }
    }

    private sealed class InterruptedStream(byte[] bytes, int limit) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= limit) throw new IOException("Simulated lost connection.");
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, limit - (int)Position)], cancellationToken);
        }
    }
}
