using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Enums;
using GeneralUpdate.Avalonia.Android.Models;
using GeneralUpdate.Avalonia.Android.Services;
using Xunit;

namespace GeneralUpdate.Avalonia.Android.Tests;

/// <summary>
/// Exercises the whole update flow with the real downloader, real file storage and real
/// SHA-256 validation: server query → version comparison → download → verify → installer handoff.
/// </summary>
public sealed class UpdateFlowEndToEndTests
{
    private const string MetadataUrl = "https://example.com/metadata";
    private const string ApkUrl = "https://example.com/app.apk";

    [Fact]
    public async Task FullFlow_WhenUpdateIsAvailable_DownloadsVerifiesAndHandsOffToInstaller()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var apkBytes = CreateApkBytes();
            var sha256 = Convert.ToHexString(SHA256.HashData(apkBytes));
            using var http = new HttpClient(new FakeUpdateServer(apkBytes, CreateMetadataJson("2.0.0", sha256, apkBytes.Length)));
            var storage = new PhysicalFileStorage();
            var installer = new RecordingInstaller();
            using var downloader = new HttpResumableApkDownloader(http, storage, new AndroidUpdateOptions { DownloadDirectoryPath = directory });
            using var bootstrap = CreateBootstrap(downloader, storage, installer, http);

            var progressEvents = 0;
            var validateRaised = false;
            bootstrap.AddListenerValidate += (_, _) => validateRaised = true;
            bootstrap.AddListenerDownloadProgressChanged += (_, _) => Interlocked.Increment(ref progressEvents);

            // 1. Validate: the server is queried internally with the installed version.
            var check = await bootstrap.ValidateAsync("1.0.0");

            Assert.True(check.Success, check.Message);
            Assert.True(check.UpdateFound);
            Assert.Equal(UpdateState.UpdateAvailable, check.State);
            Assert.True(validateRaised);
            var package = check.PackageInfo;
            Assert.NotNull(package);
            Assert.Equal("2.0.0", package.Version);

            // 2. Download and verify: resumable downloader writes to the real file system.
            var prepared = await bootstrap.DownloadAndVerifyAsync(package);

            Assert.True(prepared.Success, prepared.Message + " " + prepared.Exception);
            Assert.Equal(UpdateState.ReadyToInstall, prepared.State);
            Assert.True(progressEvents > 0);
            Assert.NotNull(prepared.FilePath);
            Assert.Equal(apkBytes, await File.ReadAllBytesAsync(prepared.FilePath!));
            Assert.False(File.Exists(prepared.FilePath + ".part"));
            Assert.False(File.Exists(prepared.FilePath + ".part.json"));

            // 3. Hand the verified package to the installer.
            var installed = await bootstrap.LaunchInstallerAsync(package, prepared.FilePath!);

            Assert.True(installed.Success);
            Assert.Equal(UpdateState.Installing, bootstrap.GetSnapshot().State);
            Assert.Same(package, installer.Package);
            Assert.Equal(prepared.FilePath, installer.Path);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task FullFlow_WhenHashDoesNotMatch_DiscardsTheDownloadedPackage()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var apkBytes = CreateApkBytes();
            var mismatchedSha256 = new string('a', 64);
            using var http = new HttpClient(new FakeUpdateServer(apkBytes, CreateMetadataJson("2.0.0", mismatchedSha256, apkBytes.Length)));
            var storage = new PhysicalFileStorage();
            using var downloader = new HttpResumableApkDownloader(http, storage, new AndroidUpdateOptions { DownloadDirectoryPath = directory });
            using var bootstrap = CreateBootstrap(downloader, storage, new RecordingInstaller(), http);

            var check = await bootstrap.ValidateAsync("1.0.0");
            Assert.NotNull(check.PackageInfo);

            var prepared = await bootstrap.DownloadAndVerifyAsync(check.PackageInfo!);

            Assert.False(prepared.Success);
            Assert.Equal(UpdateFailureReason.HashMismatch, prepared.FailureReason);
            Assert.False(File.Exists(Path.Combine(directory, "app.apk")));
            Assert.False(File.Exists(Path.Combine(directory, "app.apk.part")));
            Assert.False(File.Exists(Path.Combine(directory, "app.apk.part.json")));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task FullFlow_WhenUpdateIsForced_SkipsPrecheckAndReportsPackage()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var apkBytes = CreateApkBytes();
            var sha256 = Convert.ToHexString(SHA256.HashData(apkBytes));
            using var http = new HttpClient(new FakeUpdateServer(apkBytes, CreateMetadataJson("2.0.0", sha256, apkBytes.Length, isForced: true)));
            var storage = new PhysicalFileStorage();
            using var downloader = new HttpResumableApkDownloader(http, storage, new AndroidUpdateOptions { DownloadDirectoryPath = directory });
            using var bootstrap = CreateBootstrap(downloader, storage, new RecordingInstaller(), http);

            var precheckCalls = 0;
            bootstrap.AddListenerUpdatePrecheck(_ =>
            {
                precheckCalls++;
                return true;
            });

            var check = await bootstrap.ValidateAsync("1.0.0");

            Assert.True(check.Success);
            Assert.True(check.UpdateFound);
            Assert.NotNull(check.PackageInfo);
            Assert.True(check.PackageInfo!.IsForced);
            Assert.Equal(0, precheckCalls);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static AndroidBootstrap CreateBootstrap(
        IUpdateDownloader downloader,
        IFileStorage storage,
        IApkInstaller installer,
        HttpClient httpClient) =>
        new(new SystemVersionComparer(), downloader, new Sha256HashValidator(), installer, storage,
            eventDispatcher: new ImmediateEventDispatcher(),
            logger: new NoOpUpdateLogger(),
            updateServer: new UpdateServerOptions { RequestUrl = MetadataUrl, UseJsonEndpoint = true },
            httpClient: httpClient);

    private static byte[] CreateApkBytes()
    {
        // Larger than the default download buffer and the FileStream buffer, so a missing
        // flush or a rename-while-open would be observable.
        var bytes = new byte[300_000];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private static string CreateMetadataJson(string version, string sha256, long fileSize, bool isForced = false) =>
        JsonSerializer.Serialize(new UpdatePackageInfo
        {
            Version = version,
            VersionName = version,
            Description = "Release notes",
            DownloadUrl = ApkUrl,
            Sha256 = sha256,
            FileSize = fileSize,
            FileName = "app.apk",
            IsForced = isForced
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gu-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingInstaller : IApkInstaller
    {
        public UpdatePackageInfo? Package { get; private set; }
        public string? Path { get; private set; }

        public Task<InstallResult> LaunchInstallAsync(UpdatePackageInfo packageInfo, string apkFilePath, CancellationToken cancellationToken = default)
        {
            Package = packageInfo;
            Path = apkFilePath;
            return Task.FromResult(new InstallResult
            {
                Success = true,
                State = UpdateState.Installing,
                FailureReason = UpdateFailureReason.None,
                Message = "installer launched",
                PackageInfo = packageInfo,
                FilePath = apkFilePath
            });
        }
    }

    private sealed class FakeUpdateServer(byte[] apk, string metadataJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/metadata")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(metadataJson, Encoding.UTF8, "application/json")
                });
            }

            if (path != "/app.apk")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                head.Content.Headers.ContentLength = apk.Length;
                head.Headers.AcceptRanges.Add("bytes");
                head.Headers.ETag = new EntityTagHeaderValue("\"e2e\"");
                return Task.FromResult(head);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(apk) };
            response.Headers.AcceptRanges.Add("bytes");
            return Task.FromResult(response);
        }
    }
}
