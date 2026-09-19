using System.Net;
using System.Security.Cryptography;
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

    [Fact]
    public async Task GetPackageInfoAsync_Get_MapsMetadataAndAppliesAuthentication()
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
                  "version": "2.0.0", "versionName": "Release", "description": "Fixes",
                  "downloadUrl": "https://example.com/app.apk", "sha256": "{{Hash}}",
                  "fileSize": 123, "fileName": "release.apk", "isForced": true,
                  "publishTime": "2026-01-01T00:00:00Z", "authScheme": "Bearer"
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
    public async Task GetPackageInfoAsync_Post_SendsReferenceContractAndSelectsNewestFullApk()
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
    public async Task GetPackageInfoAsync_NoPackage_ReturnsNull(bool post, HttpStatusCode status, string json)
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
    public async Task GetPackageInfoAsync_InvalidEnvelope_DoesNotReportNoUpdate(string json)
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
    public async Task GetPackageInfoAsync_InvalidMetadata_IsRejected(string version, string url, long size, int hashLength)
    {
        using var http = CreateHttp(JsonSerializer.Serialize(new UpdatePackageInfo
        {
            Version = version, DownloadUrl = url, FileSize = size, Sha256 = new string('a', hashLength)
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HttpUpdatePackageClient(http).GetPackageInfoAsync(Endpoint));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetPackageInfoAsync_HttpFailure_IsNotNoUpdate(bool post)
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
    public async Task GetPackageInfoAsync_MalformedJson_IsRejected(bool post)
    {
        using var http = CreateHttp("{");
        var client = new HttpUpdatePackageClient(http);
        await Assert.ThrowsAsync<JsonException>(() => post
            ? client.GetPackageInfoAsync(Endpoint, VerificationRequest)
            : client.GetPackageInfoAsync(Endpoint));
    }

    [Fact]
    public async Task GetPackageInfoAsync_Cancellation_ReachesHttpRequest()
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
    public async Task DiscoveredPackage_CompletesDownloadVerificationAndInstallerHandoff()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gu-{Guid.NewGuid():N}");
        var bytes = Encoding.UTF8.GetBytes("APK test content");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var packageInfo = new UpdatePackageInfo
        {
            Version = "2.0.0", DownloadUrl = "https://example.com/app.apk",
            Sha256 = hash, FileSize = bytes.Length, FileName = "app.apk"
        };
        using var http = new HttpClient(new TestHandler((request, _) =>
            Task.FromResult(request.RequestUri!.AbsolutePath == "/metadata"
                ? JsonResponse(JsonSerializer.Serialize(packageInfo))
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
        try
        {
            var storage = new ClosedFileStorage();
            var installer = new RecordingInstaller();
            using var downloader = new HttpResumableApkDownloader(http, storage,
                new AndroidUpdateOptions { DownloadDirectoryPath = directory });
            using var bootstrap = new AndroidBootstrap(new SystemVersionComparer(), downloader,
                new Sha256HashValidator(), installer, storage);
            var validationRaised = false;
            bootstrap.AddListenerValidate += (_, _) => validationRaised = true;

            var package = await new HttpUpdatePackageClient(http).GetPackageInfoAsync("https://example.com/metadata");
            Assert.NotNull(package);
            Assert.False(validationRaised);
            Assert.Equal(UpdateState.None, bootstrap.GetSnapshot().State);

            var check = await bootstrap.ValidateAsync(package, "1.0.0");
            Assert.True(check.UpdateFound);
            Assert.True(validationRaised);
            var prepared = await bootstrap.DownloadAndVerifyAsync(package);
            Assert.True(prepared.Success, prepared.Message);
            Assert.Equal(UpdateState.ReadyToInstall, prepared.State);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(prepared.FilePath!));
            Assert.False(File.Exists(prepared.FilePath + ".part"));
            Assert.False(File.Exists(prepared.FilePath + ".part.json"));

            var installed = await bootstrap.LaunchInstallerAsync(package, prepared.FilePath!);
            Assert.True(installed.Success);
            Assert.Equal(UpdateState.Installing, bootstrap.GetSnapshot().State);
            Assert.Same(package, installer.Package);
            Assert.Equal(prepared.FilePath, installer.Path);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static HttpClient CreateHttp(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new TestHandler((_, _) => Task.FromResult(JsonResponse(json, status))));

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class RecordingInstaller : IApkInstaller
    {
        public UpdatePackageInfo? Package { get; private set; }
        public string? Path { get; private set; }

        public Task<InstallResult> LaunchInstallAsync(UpdatePackageInfo packageInfo, string apkFilePath, CancellationToken cancellationToken = default)
        {
            Package = packageInfo;
            Path = apkFilePath;
            return Task.FromResult(new InstallResult { Success = true, State = UpdateState.Installing });
        }
    }

    private sealed class ClosedFileStorage : IFileStorage
    {
        private readonly PhysicalFileStorage _inner = new();
        private Stream? _writer;

        public Stream OpenWrite(string path, bool append) => _writer = _inner.OpenWrite(path, append);

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
        {
            Assert.NotNull(_writer);
            Assert.False(_writer.CanWrite);
            _inner.MoveFile(sourcePath, destinationPath, overwrite);
        }

        public bool FileExists(string path) => _inner.FileExists(path);
        public void EnsureDirectory(string path) => _inner.EnsureDirectory(path);
        public long GetFileLength(string path) => _inner.GetFileLength(path);
        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public void DeleteFile(string path) => _inner.DeleteFile(path);
        public Task<string?> ReadAllTextAsync(string path, CancellationToken cancellationToken) =>
            _inner.ReadAllTextAsync(path, cancellationToken);
        public Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken) =>
            _inner.WriteAllTextAsync(path, content, cancellationToken);
    }
}
