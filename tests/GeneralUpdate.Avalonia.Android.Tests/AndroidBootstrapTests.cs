using System.Collections.Concurrent;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Events;
using GeneralUpdate.Avalonia.Android.Models;
using GeneralUpdate.Avalonia.Android.Services;
using Xunit;

namespace GeneralUpdate.Avalonia.Android.Tests;

public sealed class AndroidBootstrapTests
{
    [Fact]
    public async Task ValidateAsync_WhenTargetHigher_RaisesValidateAndReturnsUpdateFound()
    {
        var bootstrap = CreateBootstrap();
        var packageInfo = CreatePackageInfo(version: "1.2.0");
        const string currentVersion = "1.0.0";
        ValidateEventArgs? validateArgs = null;

        bootstrap.AddListenerValidate += (_, args) => validateArgs = args;

        var result = await bootstrap.ValidateAsync(packageInfo, currentVersion);

        Assert.True(result.Success);
        Assert.True(result.UpdateFound);
        Assert.Equal(UpdateState.UpdateAvailable, result.State);
        Assert.NotNull(validateArgs);
        Assert.Equal(currentVersion, validateArgs!.CurrentVersion);
        Assert.Equal(packageInfo, validateArgs.PackageInfo);
    }

    [Fact]
    public async Task ValidateAsync_WhenVersionCompareFails_RaisesFailedAndReturnsFailure()
    {
        var bootstrap = CreateBootstrap(versionComparer: new FailingVersionComparer("bad version"));
        var packageInfo = CreatePackageInfo(version: "invalid");
        UpdateFailedEventArgs? failedArgs = null;

        bootstrap.AddListenerUpdateFailed += (_, args) => failedArgs = args;

        var result = await bootstrap.ValidateAsync(packageInfo, "1.0.0");

        Assert.False(result.Success);
        Assert.Equal(UpdateFailureReason.VersionComparisonFailed, result.FailureReason);
        Assert.NotNull(failedArgs);
        Assert.Equal(UpdateFailureReason.VersionComparisonFailed, failedArgs!.Result.FailureReason);
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
        var bootstrap = CreateBootstrap(downloader: downloader);
        var packageInfo = CreatePackageInfo();

        var firstTask = bootstrap.DownloadAndVerifyAsync(packageInfo);
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var secondTask = bootstrap.ValidateAsync(packageInfo with { Version = "9.0.0" }, "1.0.0");
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
        IFileStorage? fileStorage = null)
    {
        return new AndroidBootstrap(
            versionComparer ?? new SystemVersionComparer(),
            downloader ?? new SuccessDownloader(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.apk")),
            hashValidator ?? new SuccessHashValidator(),
            installer ?? new SuccessInstaller(),
            fileStorage ?? new TestFileStorage(),
            eventDispatcher: new ImmediateEventDispatcher(),
            logger: new NoOpUpdateLogger());
    }

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

    private sealed class FailingVersionComparer(string error) : IVersionComparer
    {
        public bool TryCompare(string currentVersion, string targetVersion, out int compareResult, out string? errorMessage)
        {
            compareResult = 0;
            errorMessage = error;
            return false;
        }
    }

    private sealed class SuccessDownloader(string filePath) : IUpdateDownloader
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
                FilePath = filePath
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
