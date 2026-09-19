using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Enums;
using GeneralUpdate.Avalonia.Android.Models;
using GeneralUpdate.Avalonia.Android.Services;
using Xunit;

namespace GeneralUpdate.Avalonia.Android.Tests;

public sealed class HttpUpdatePackageClientTests
{
    private const string Endpoint = "https://example.com/Upgrade/Verification";
    private static readonly string Hash = new('a', 64);
    private static readonly UpdatePackageRequest VerificationRequest = new()
    {
        Version = "1.0.0",
        AppKey = "test-app",
        Platform = 42,
        ProductId = "test-product"
    };

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task ValidateAsync_QueriesServerThenAppliesPrecheck(bool skip, bool forced, bool expectedUpdate)
    {
        var order = new List<string>();
        using var http = new HttpClient(new TestHandler(async (request, ct) =>
        {
            order.Add("request");
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Endpoint, request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("1.0.0", body.RootElement.GetProperty("version").GetString());
            Assert.Equal(42, body.RootElement.GetProperty("platform").GetInt32());
            return JsonResponse($$"""
                {"code":200,"body":[
                  {"version":"2.0.0","url":"https://example.com/app.apk","hash":"{{Hash}}",
                   "updateLog":"Latest release","isForcibly":{{(forced ? "true" : "false")}}}
                ]}
                """);
        }));
        using var bootstrap = CreateBootstrap(http);
        bootstrap.AddListenerUpdatePrecheck(args =>
        {
            order.Add("precheck");
            Assert.Equal("2.0.0", args.PackageInfo.Version);
            Assert.Equal("Latest release", args.PackageInfo.Description);
            Assert.Equal("1.0.0", args.CurrentVersion);
            Assert.True(args.Result.UpdateFound);
            return skip;
        });
        bootstrap.AddListenerValidate += (_, _) => order.Add("validate");

        var result = await bootstrap.ValidateAsync("1.0.0");

        Assert.True(result.Success);
        Assert.Equal(expectedUpdate, result.UpdateFound);
        Assert.Equal(expectedUpdate ? UpdateState.UpdateAvailable : UpdateState.Completed, result.State);
        Assert.Equal(result.State, bootstrap.GetSnapshot().State);
        Assert.NotNull(result.PackageInfo);
        Assert.Equal(forced ? new[] { "request", "validate" }
            : skip ? new[] { "request", "precheck" }
            : new[] { "request", "precheck", "validate" }, order);
    }

    [Theory]
    [InlineData("{\"code\":200,\"body\":[]}", HttpStatusCode.OK, UpdateFailureReason.None)]
    [InlineData("{\"code\":500,\"body\":[]}", HttpStatusCode.OK, UpdateFailureReason.InvalidMetadata)]
    [InlineData("{", HttpStatusCode.OK, UpdateFailureReason.InvalidMetadata)]
    [InlineData("{}", HttpStatusCode.Unauthorized, UpdateFailureReason.NetworkError)]
    public async Task ValidateAsync_QueryFailuresAndEmptyResults_NeverInvokePrecheck(
        string json, HttpStatusCode status, UpdateFailureReason failureReason)
    {
        using var http = CreateHttp(json, status);
        using var bootstrap = CreateBootstrap(http);
        var failed = 0;
        bootstrap.AddListenerUpdatePrecheck(_ => throw new InvalidOperationException("Unexpected precheck."));
        bootstrap.AddListenerValidate += (_, _) => throw new InvalidOperationException("Unexpected validation event.");
        bootstrap.AddListenerUpdateFailed += (_, _) => failed++;

        var result = await bootstrap.ValidateAsync("1.0.0");

        Assert.Equal(failureReason == UpdateFailureReason.None, result.Success);
        Assert.False(result.UpdateFound);
        Assert.Equal(failureReason, result.FailureReason);
        Assert.Equal(result.State, bootstrap.GetSnapshot().State);
        Assert.Equal(result.Success ? 0 : 1, failed);
    }

    [Fact]
    public async Task ValidateAsync_WhenServerHasNoNewerPackage_CompletesWithoutPrecheck()
    {
        using var http = CreateHttp($$"""
            {"code":200,"body":[{"version":"1.0.0","url":"https://example.com/app.apk","hash":"{{Hash}}"}]}
            """);
        using var bootstrap = CreateBootstrap(http);
        bootstrap.AddListenerUpdatePrecheck(_ => throw new InvalidOperationException("Unexpected precheck."));

        var result = await bootstrap.ValidateAsync("1.0.0");

        Assert.True(result.Success);
        Assert.False(result.UpdateFound);
        Assert.Equal(UpdateState.Completed, result.State);
    }

    [Fact]
    public async Task ValidateAsync_WhenRequestIsCanceled_ReportsCancellationAndReleasesGate()
    {
        var calls = 0;
        using var cts = new CancellationTokenSource();
        using var http = new HttpClient(new TestHandler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
            }

            return JsonResponse("{\"code\":200,\"body\":[]}");
        }));
        using var bootstrap = CreateBootstrap(http);
        var failed = 0;
        bootstrap.AddListenerUpdateFailed += (_, _) => failed++;

        var result = await bootstrap.ValidateAsync("1.0.0", cts.Token);

        Assert.False(result.Success);
        Assert.Equal(UpdateState.Canceled, result.State);
        Assert.Equal(UpdateFailureReason.Canceled, result.FailureReason);
        Assert.Equal(1, failed);

        var next = await bootstrap.ValidateAsync("1.0.0").WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(next.Success);
        Assert.Equal(UpdateState.Completed, next.State);
    }

    [Fact]
    public async Task ValidateAsync_WhenRequestTimesOut_ReportsNetworkFailureInsteadOfCancellation()
    {
        using var http = new HttpClient(new TestHandler((_, _) =>
            throw new TaskCanceledException("Simulated request timeout.")));
        using var bootstrap = CreateBootstrap(http);

        var result = await bootstrap.ValidateAsync("1.0.0");

        Assert.False(result.Success);
        Assert.Equal(UpdateState.Failed, result.State);
        Assert.Equal(UpdateFailureReason.NetworkError, result.FailureReason);
    }

    [Fact]
    public async Task ValidateAsync_WithHttpOptions_AuthenticatesInternalRequestAndSendsCurrentVersion()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = (IPEndPoint)listener.LocalEndpoint;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = Guid.NewGuid().ToString("N");
        using var bootstrap = new AndroidBootstrap(new SystemVersionComparer(), new UnexpectedDownloader(),
            new NoOpHashValidator(), new NoOpInstaller(), new EmptyFileStorage(),
            updateServer: new UpdateServerOptions
            {
                RequestUrl = $"http://127.0.0.1:{address.Port}/Upgrade/Verification",
                AppType = 1,
                Platform = 42
            },
            httpOptions: new HttpDownloadOptions
            {
                AuthProvider = new BearerTokenAuthProvider(token),
                RequestTimeout = TimeSpan.FromSeconds(5)
            });

        var checkTask = bootstrap.ValidateAsync("1.2.3", timeout.Token);
        using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        Assert.Equal("POST /Upgrade/Verification HTTP/1.1", await reader.ReadLineAsync(timeout.Token));

        var headers = new List<string>();
        while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } header)
        {
            headers.Add(header);
        }

        var authorization = System.Net.Http.Headers.AuthenticationHeaderValue.Parse(
            headers.Single(h => h.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                .Split(':', 2)[1].Trim());
        Assert.Equal("Bearer", authorization.Scheme);
        Assert.Equal(token, authorization.Parameter);

        var requestBody = new StringBuilder();
        while (true)
        {
            var chunkLength = Convert.ToInt32(await reader.ReadLineAsync(timeout.Token), 16);
            if (chunkLength == 0)
            {
                break;
            }

            var chunk = new char[chunkLength];
            Assert.Equal(chunkLength, await reader.ReadBlockAsync(chunk, timeout.Token));
            requestBody.Append(chunk);
            Assert.Equal(string.Empty, await reader.ReadLineAsync(timeout.Token));
        }

        using var json = JsonDocument.Parse(requestBody.ToString());
        Assert.Equal("1.2.3", json.RootElement.GetProperty("version").GetString());
        Assert.Equal(42, json.RootElement.GetProperty("platform").GetInt32());

        const string body = "{\"code\":200,\"body\":[]}";
        var response = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
        await stream.WriteAsync(response, timeout.Token);
        await stream.FlushAsync(timeout.Token);

        var result = await checkTask;
        Assert.True(result.Success);
        Assert.False(result.UpdateFound);
    }

    [Fact]
    public async Task GetPackageInfoAsync_GetJson_MapsMetadataAndAppliesAuthentication()
    {
        var token = Guid.NewGuid().ToString("N");
        using var http = new HttpClient(new TestHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(Endpoint, request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(token, request.Headers.Authorization.Parameter);
            Assert.Contains(request.Headers.Accept, header => header.MediaType == "application/json");
            return Task.FromResult(JsonResponse($$"""
                {
                  "version":"2.0.0","versionName":"Release","description":"Fixes",
                  "downloadUrl":"https://example.com/app.apk","sha256":"{{Hash}}",
                  "fileSize":123,"fileName":"release.apk","isForced":true,
                  "publishTime":"2026-01-01T00:00:00Z","authScheme":"Bearer"
                }
                """));
        }));
        var client = new HttpUpdatePackageClient(http, new BearerTokenAuthProvider(token));

        var package = await client.GetPackageInfoAsync(Endpoint);

        Assert.NotNull(package);
        Assert.Equal("2.0.0", package.Version);
        Assert.Equal("Release", package.VersionName);
        Assert.Equal("Fixes", package.Description);
        Assert.Equal("https://example.com/app.apk", package.DownloadUrl);
        Assert.Equal(Hash, package.Sha256);
        Assert.Equal(123, package.FileSize);
        Assert.Equal("release.apk", package.FileName);
        Assert.True(package.IsForced);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), package.PublishTime);
        Assert.Equal(AuthScheme.Bearer, package.AuthScheme);
    }

    [Fact]
    public async Task GetPackageInfoAsync_Post_SendsVerificationContractAndSelectsNewestFullApk()
    {
        using var http = new HttpClient(new TestHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            Assert.Equal("1.0.0", body.RootElement.GetProperty("version").GetString());
            Assert.Equal("test-app", body.RootElement.GetProperty("appKey").GetString());
            Assert.Equal(1, body.RootElement.GetProperty("appType").GetInt32());
            Assert.Equal(42, body.RootElement.GetProperty("platform").GetInt32());
            Assert.Equal("test-product", body.RootElement.GetProperty("productId").GetString());
            Assert.Equal(5, body.RootElement.EnumerateObject().Count());
            return JsonResponse($$"""
                {"code":200,"body":[
                  {"version":"99.0","format":".zip","packageType":2},
                  {"version":"98.0","format":".apk","packageType":1},
                  {"version":"97.0","format":".apk","isFreeze":true},
                  {"version":"2.9.0","url":"https://example.com/old.apk","hash":"{{Hash}}"},
                  {"version":"2.10.0","name":"Release","updateLog":"Fixes","format":".APK",
                   "url":"https://example.com/File/Download/hash","hash":"{{Hash}}","size":123,
                   "packageType":2,"isForcibly":true,"releaseDate":"2026-01-01T00:00:00Z",
                   "authScheme":"Bearer"},
                  {"version":"2.8.0","format":"apk","url":"https://example.com/older.apk",
                   "hash":"{{Hash}}","isFreeze":null,"isForcibly":null}
                ]}
                """);
        }));

        var package = await new HttpUpdatePackageClient(http).GetPackageInfoAsync(Endpoint, VerificationRequest);

        Assert.NotNull(package);
        Assert.Equal("2.10.0", package.Version);
        Assert.Equal("https://example.com/File/Download/hash", package.DownloadUrl);
        Assert.Equal("Release", package.VersionName);
        Assert.Equal("Fixes", package.Description);
        Assert.Equal(Hash, package.Sha256);
        Assert.Equal(123, package.FileSize);
        Assert.True(package.IsForced);
        Assert.Equal(AuthScheme.Bearer, package.AuthScheme);
        Assert.Null(package.FileName);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.NoContent, "")]
    [InlineData(true, HttpStatusCode.NoContent, "")]
    [InlineData(false, HttpStatusCode.OK, "null")]
    [InlineData(true, HttpStatusCode.OK, "{\"code\":200,\"body\":[]}")]
    [InlineData(true, HttpStatusCode.OK, "{\"code\":200,\"body\":[{\"format\":\"zip\"}]}")]
    public async Task GetPackageInfoAsync_WhenServerReportsNoPackage_ReturnsNull(bool post, HttpStatusCode status, string json)
    {
        using var http = CreateHttp(json, status);
        var client = new HttpUpdatePackageClient(http);

        var package = post
            ? await client.GetPackageInfoAsync(Endpoint, VerificationRequest)
            : await client.GetPackageInfoAsync(Endpoint);

        Assert.Null(package);
    }

    [Theory]
    [InlineData("{\"code\":500,\"body\":[]}")]
    [InlineData("{\"code\":200}")]
    [InlineData("{\"code\":200,\"body\":null}")]
    [InlineData("{\"code\":200,\"body\":[null]}")]
    [InlineData("null")]
    public async Task GetPackageInfoAsync_WhenEnvelopeIsInvalid_ReportsFailure(string json)
    {
        using var http = CreateHttp(json);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HttpUpdatePackageClient(http).GetPackageInfoAsync(Endpoint, VerificationRequest));
    }

    [Theory]
    [InlineData("", "https://example.com/app.apk", 0, 64)]
    [InlineData("2.0", "file:///tmp/app.apk", 0, 64)]
    [InlineData("2.0", "/app.apk", 0, 64)]
    [InlineData("2.0", "https://example.com/app.apk", -1, 64)]
    [InlineData("2.0", "https://example.com/app.apk", 0, 32)]
    [InlineData("2.0", "https://example.com/app.apk", 0, 0)]
    public async Task GetPackageInfoAsync_WhenMetadataIsInvalid_IsRejected(string version, string url, long size, int hashLength)
    {
        using var http = CreateHttp(JsonSerializer.Serialize(new UpdatePackageInfo
        {
            Version = version,
            DownloadUrl = url,
            FileSize = size,
            Sha256 = new string('a', hashLength)
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HttpUpdatePackageClient(http).GetPackageInfoAsync(Endpoint));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetPackageInfoAsync_WhenHttpRequestFails_IsNotTreatedAsNoUpdate(bool post)
    {
        using var http = CreateHttp("{}", HttpStatusCode.Unauthorized);
        var client = new HttpUpdatePackageClient(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => post
            ? client.GetPackageInfoAsync(Endpoint, VerificationRequest)
            : client.GetPackageInfoAsync(Endpoint));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetPackageInfoAsync_WhenJsonIsMalformed_IsRejected(bool post)
    {
        using var http = CreateHttp("{");
        var client = new HttpUpdatePackageClient(http);
        await Assert.ThrowsAsync<JsonException>(() => post
            ? client.GetPackageInfoAsync(Endpoint, VerificationRequest)
            : client.GetPackageInfoAsync(Endpoint));
    }

    [Fact]
    public async Task GetPackageInfoAsync_WhenCanceled_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        using var http = new HttpClient(new TestHandler(async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return JsonResponse("null");
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HttpUpdatePackageClient(http).GetPackageInfoAsync(Endpoint, cts.Token));
    }

    [Fact]
    public async Task Dispose_DoesNotDisposeInjectedHttpClient()
    {
        using var http = CreateHttp("{\"code\":200,\"body\":[]}");
        var client = new HttpUpdatePackageClient(http);

        client.Dispose();

        using var response = await http.GetAsync(Endpoint);
        Assert.True(response.IsSuccessStatusCode);
    }

    private static AndroidBootstrap CreateBootstrap(HttpClient http) =>
        new(new SystemVersionComparer(), new UnexpectedDownloader(), new NoOpHashValidator(),
            new NoOpInstaller(), new EmptyFileStorage(),
            updateServer: new UpdateServerOptions
            {
                RequestUrl = Endpoint,
                AppKey = VerificationRequest.AppKey,
                AppType = VerificationRequest.AppType,
                Platform = VerificationRequest.Platform,
                ProductId = VerificationRequest.ProductId
            },
            httpClient: http);

    private static HttpClient CreateHttp(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new TestHandler((_, _) => Task.FromResult(JsonResponse(json, status))));

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class UnexpectedDownloader : IUpdateDownloader
    {
        public Task<DownloadResult> DownloadAsync(UpdatePackageInfo packageInfo,
            Action<DownloadProgressInfo>? progressCallback, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Validation must not download a package.");
    }

    private sealed class NoOpHashValidator : IHashValidator
    {
        public Task<HashValidationResult> ValidateSha256Async(string filePath, string expectedSha256,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Validation must not hash a package.");
    }

    private sealed class NoOpInstaller : IApkInstaller
    {
        public Task<InstallResult> LaunchInstallAsync(UpdatePackageInfo packageInfo, string apkFilePath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Validation must not launch an installer.");
    }

    private sealed class EmptyFileStorage : IFileStorage
    {
        public bool FileExists(string path) => false;
        public void EnsureDirectory(string directoryPath) { }
        public long GetFileLength(string path) => 0;
        public Stream OpenWrite(string path, bool append) => new MemoryStream();
        public Stream OpenRead(string filePath) => new MemoryStream();
        public void MoveFile(string sourcePath, string destinationPath, bool overwrite) { }
        public void DeleteFile(string path) { }
        public Task<string?> ReadAllTextAsync(string path, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
