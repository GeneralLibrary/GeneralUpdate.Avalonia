using System.Collections.Concurrent;
using System.Net;
using System.Text;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Events;
using GeneralUpdate.Avalonia.Android.Models;
using GeneralUpdate.Avalonia.Android.Services;
using Xunit;

namespace GeneralUpdate.Avalonia.Android.Tests;

public sealed class AndroidBootstrapTests
{
    private const string Endpoint = "https://example.com/Upgrade/Verification";
    private static readonly string Hash = new('a', 64);

    [Fact]
    public async Task ValidateAsync_WhenTargetHigher_RaisesValidateAndReturnsUpdateFound()
    {
        var bootstrap = CreateBootstrap(httpClient: CreateHttp(VerificationJson("1.2.0")));
        const string currentVersion = "1.0.0";
        ValidateEventArgs? validateArgs = null;

        bootstrap.AddListenerValidate += (_, args) => validateArgs = args;

        var result = await bootstrap.ValidateAsync(currentVersion);

        Assert.True(result.Success);
        Assert.True(result.UpdateFound);
        Assert.Equal(UpdateState.UpdateAvailable, result.State);
        Assert.NotNull(validateArgs);
        Assert.Equal(currentVersion, validateArgs!.CurrentVersion);
        Assert.Equal(result.PackageInfo, validateArgs.PackageInfo);
        Assert.Equal("1.2.0", validateArgs.PackageInfo.Version);
    }

    [Fact]
    public async Task ValidateAsync_WhenVersionCompareFails_RaisesFailedAndReturnsFailure()
    {
        var bootstrap = CreateBootstrap(
            versionComparer: new FailingVersionComparer("bad version"),
            httpClient: CreateHttp(VerificationJson("1.2.0")));
        UpdateFailedEventArgs? failedArgs = null;

        bootstrap.AddListenerUpdateFailed += (_, args) => failedArgs = args;

        var result = await bootstrap.ValidateAsync("1.0.0");

        Assert.False(result.Success);
        Assert.Equal(UpdateFailureReason.VersionComparisonFailed, result.FailureReason);
        Assert.NotNull(failedArgs);
        Assert.Equal(UpdateFailureReason.VersionComparisonFailed, failedArgs!.Result.FailureReason);
    }

    [Fact]
    public async Task ValidateAsync_WhenPrecheckReturnsTrue_SkipsUpdateAndDoesNotRaiseValidate()
    {
        var bootstrap = CreateBootstrap(httpClient: CreateHttp(VerificationJson("1.2.0")));
        UpdateInfoEventArgs? precheckArgs = null;
        var validateRaised = false;

        bootstrap.AddListenerValidate += (_, _) => validateRaised = true;
        bootstrap.AddListenerUpdatePrecheck(args =>
        {
            precheckArgs = args;
            return true;
        });

        var result = await bootstrap.ValidateAsync("1.0.0");

        Assert.True(result.Success);
        Assert.False(result.UpdateFound);
        Assert.Equal(UpdateState.Completed, result.State);
        Assert.False(validateRaised);
        Assert.NotNull(precheckArgs);
        Assert.Equal("1.2.0", precheckArgs!.PackageInfo.Version);
        Assert.Equal("1.0.0", precheckArgs.CurrentVersion);
        Assert.Equal("1.2.0", precheckArgs.Result.TargetVersion);
        Assert.True(precheckArgs.Result.UpdateFound);
        Assert.Equal(UpdateState.Completed, bootstrap.GetSnapshot().State);
    }

    [Fact]
    public async Task ValidateAsync_WhenPrecheckReturnsFalse_ProceedsWithUpdate()
    {
        var bootstrap = CreateBootstrap(httpClient: CreateHttp(VerificationJson("1.2.0")));
        var calls = 0;
        UpdateInfoEventArgs? precheckArgs = null;

        bootstrap.AddListenerUpdatePrecheck(args =>
        {
            calls++;
            precheckArgs = args;
            return false;
        });

        var result = await bootstrap.ValidateAsync("1.0.0");

        Assert.True(result.UpdateFound);
        Assert.Equal(UpdateState.UpdateAvailable, result.State);
        Assert.Equal(1, calls);
        Assert.NotNull(precheckArgs);
        Assert.Equal("1.2.0", precheckArgs!.Result.TargetVersion);
    }

    [Fact]
    public async Task ValidateAsync_WhenUpdateIsForced_DoesNotInvokePrecheck()
    {
        var bootstrap = CreateBootstrap(httpClient: CreateHttp(VerificationJson("1.2.0", isForced: true)));
        var calls = 0;

        bootstrap.AddListenerUpdatePrecheck(_ =>
        {
            calls++;
            return true;
        });

        var result = await bootstrap.ValidateAsync("1.0.0");

        Assert.True(result.UpdateFound);
        Assert.Equal(UpdateState.UpdateAvailable, result.State);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ValidateAsync_WhenNoUpdate_DoesNotInvokePrecheck()
    {
        var bootstrap = CreateBootstrap(httpClient: CreateHttp(VerificationJson("1.0.0")));
        var calls = 0;

        bootstrap.AddListenerUpdatePrecheck(_ =>
        {
            calls++;
            return true;
        });

        var result = await bootstrap.ValidateAsync("1.0.0");

        Assert.False(result.UpdateFound);
        Assert.Equal(UpdateState.Completed, result.State);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ValidateAsync_WithoutConfiguredServer_ReportsInvalidMetadata()
    {
        using var bootstrap = new AndroidBootstrap(
            new SystemVersionComparer(),
            new SuccessDownloader(),
            new SuccessHashValidator(),
            new SuccessInstaller(),
            new TestFileStorage(),
            eventDispatcher: new ImmediateEventDispatcher(),
            logger: new NoOpUpdateLogger());

        var result = await bootstrap.ValidateAsync("1.0.0");

        Assert.False(result.Success);
        Assert.False(result.UpdateFound);
        Assert.Equal(UpdateFailureReason.InvalidMetadata, result.FailureReason);
        Assert.Equal(UpdateState.Failed, bootstrap.GetSnapshot().State);
    }

    [Fact]
    public void AddListenerUpdatePrecheck_WhenNull_Throws()
    {
        var bootstrap = CreateBootstrap();

        Assert.Throws<ArgumentNullException>(() => bootstrap.AddListenerUpdatePrecheck(null!));
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_WhenSuccess_RaisesProgressAndCompleted()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"gu-{Guid.NewGuid():N}.apk");
        try
        {
            var packageInfo = CreatePackageInfo(version: "2.0.0", sha256: "123");
            var downloader = new SuccessDownloader(tempPath);
            var hashValidator = new SuccessHashValidator();
            var fileStorage = new TestFileStorage(new Dictionary<string, long> { [tempPath] = 10 });
            var bootstrap = CreateBootstrap(downloader: downloader, hashValidator: hashValidator, fileStorage: fileStorage);

            var progressSeen = 0;
            UpdateCompletedEventArgs? completed = null;

            bootstrap.AddListenerDownloadProgressChanged += (_, _) => Interlocked.Increment(ref progressSeen);
            bootstrap.AddListenerUpdateCompleted += (_, args) => completed = args;

            var result = await bootstrap.DownloadAndVerifyAsync(packageInfo);

            Assert.True(result.Success);
            Assert.Equal(UpdateState.ReadyToInstall, result.State);
            Assert.True(progressSeen > 0);
            Assert.NotNull(completed);
            Assert.Equal(UpdateState.ReadyToInstall, completed!.Result.State);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public async Task ConcurrentOperations_AreSerializedByOperationGate()
    {
        var gate = new GateController();
        var downloader = new GatedTestDownloader(gate);
        var bootstrap = CreateBootstrap(downloader: downloader, httpClient: CreateHttp(VerificationJson("9.0.0")));
        var packageInfo = CreatePackageInfo();

        var firstTask = bootstrap.DownloadAndVerifyAsync(packageInfo);
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var secondTask = bootstrap.ValidateAsync("1.0.0");
        await Task.Delay(150);

        Assert.False(secondTask.IsCompleted);

        gate.AllowFinish.TrySetResult(true);

        await firstTask;
        await secondTask;
    }

    private static AndroidBootstrap CreateBootstrap(
        IVersionComparer? versionComparer = null,
        IUpdateDownloader? downloader = null,
        IHashValidator? hashValidator = null,
        IApkInstaller? installer = null,
        IFileStorage? fileStorage = null,
        HttpClient? httpClient = null)
    {
        return new AndroidBootstrap(
            versionComparer ?? new SystemVersionComparer(),
            downloader ?? new SuccessDownloader(),
            hashValidator ?? new SuccessHashValidator(),
            installer ?? new SuccessInstaller(),
            fileStorage ?? new TestFileStorage(),
            eventDispatcher: new ImmediateEventDispatcher(),
            logger: new NoOpUpdateLogger(),
            updateServer: new UpdateServerOptions { RequestUrl = Endpoint },
            httpClient: httpClient ?? CreateHttp("{\"code\":200,\"body\":[]}"));
    }

    /// <summary>
    /// Builds a GeneralUpdate <c>/Upgrade/Verification</c> response carrying a single full APK.
    /// </summary>
    private static string VerificationJson(string version, bool isForced = false) =>
        $$"""
        {"code":200,"body":[
          {"version":"{{version}}","name":"Release","updateLog":"Fixes","format":".apk",
           "url":"https://example.com/app.apk","hash":"{{Hash}}","size":10,"isForcibly":{{(isForced ? "true" : "false")}}}
        ]}
        """;

    private static HttpClient CreateHttp(string json) =>
        new(new TestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        })));

    private static UpdatePackageInfo CreatePackageInfo(string version = "1.1.0", string sha256 = "abc")
    {
        return new UpdatePackageInfo
        {
            Version = version,
            DownloadUrl = "https://example.com/app.apk",
            Sha256 = sha256,
            FileSize = 10,
            FileName = "app.apk"
        };
    }

    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class FailingVersionComparer(string error) : IVersionComparer
    {
        public bool TryCompare(string currentVersion, string targetVersion, out int compareResult, out string? errorMessage)
        {
            compareResult = 0;
            errorMessage = error;
            return false;
        }
    }

    private sealed class SuccessDownloader(string? filePath = null) : IUpdateDownloader
    {
        public Task<DownloadResult> DownloadAsync(UpdatePackageInfo packageInfo, Action<DownloadProgressInfo>? progressCallback, CancellationToken cancellationToken = default)
        {
            progressCallback?.Invoke(new DownloadProgressInfo
            {
                DownloadSpeedBytesPerSecond = 128,
                DownloadedBytes = packageInfo.FileSize,
                RemainingBytes = 0,
                TotalBytes = packageInfo.FileSize,
                ProgressPercentage = 100,
                PackageInfo = packageInfo,
                StatusDescription = "Download completed"
            });

            return Task.FromResult(new DownloadResult
            {
                Success = true,
                State = UpdateState.Completed,
                FailureReason = UpdateFailureReason.None,
                Message = "ok",
                PackageInfo = packageInfo,
                FilePath = filePath ?? Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.apk")
            });
        }
    }

    private sealed class GatedTestDownloader(GateController gate) : IUpdateDownloader
    {
        public async Task<DownloadResult> DownloadAsync(UpdatePackageInfo packageInfo, Action<DownloadProgressInfo>? progressCallback, CancellationToken cancellationToken = default)
        {
            gate.Started.TrySetResult(true);
            await gate.AllowFinish.Task.WaitAsync(cancellationToken);

            return new DownloadResult
            {
                Success = true,
                State = UpdateState.Completed,
                FailureReason = UpdateFailureReason.None,
                PackageInfo = packageInfo,
                FilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.apk")
            };
        }
    }

    private sealed class SuccessHashValidator : IHashValidator
    {
        public Task<HashValidationResult> ValidateSha256Async(string filePath, string expectedSha256, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new HashValidationResult
            {
                Success = true,
                State = UpdateState.Verifying,
                FailureReason = UpdateFailureReason.None,
                Message = "hash ok"
            });
        }
    }

    private sealed class SuccessInstaller : IApkInstaller
    {
        public Task<InstallResult> LaunchInstallAsync(UpdatePackageInfo packageInfo, string apkFilePath, CancellationToken cancellationToken = default)
        {
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

    private sealed class GateController
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> AllowFinish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class TestFileStorage : IFileStorage
    {
        private readonly ConcurrentDictionary<string, long> _lengths;

        public TestFileStorage(IDictionary<string, long>? lengths = null)
        {
            _lengths = new ConcurrentDictionary<string, long>(lengths ?? new Dictionary<string, long>());
        }

        public bool FileExists(string path) => _lengths.ContainsKey(path) || File.Exists(path);

        public void EnsureDirectory(string directoryPath)
        {
        }

        public long GetFileLength(string path)
        {
            if (_lengths.TryGetValue(path, out var len))
            {
                return len;
            }

            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }

        public Stream OpenWrite(string path, bool append)
        {
            return new MemoryStream();
        }

        public Stream OpenRead(string filePath)
        {
            return new MemoryStream();
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
        {
            _lengths[destinationPath] = GetFileLength(sourcePath);
            _lengths.TryRemove(sourcePath, out _);
        }

        public void DeleteFile(string path)
        {
            _lengths.TryRemove(path, out _);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        public async Task<string?> ReadAllTextAsync(string path, CancellationToken cancellationToken)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return await File.ReadAllTextAsync(path, cancellationToken);
        }

        public Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken)
        {
            return File.WriteAllTextAsync(path, content, cancellationToken);
        }
    }
}
