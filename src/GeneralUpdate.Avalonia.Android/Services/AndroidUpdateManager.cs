using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Events;
using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Services;

public sealed class AndroidUpdateManager : IAndroidUpdateManager
{
    private readonly IVersionComparer _versionComparer;
    private readonly IUpdateDownloader _downloader;
    private readonly IHashValidator _hashValidator;
    private readonly IApkInstaller _apkInstaller;
    private readonly IFileStorage _fileStorage;
    private readonly IUpdateEventDispatcher _eventDispatcher;
    private readonly IUpdateLogger _logger;

    private readonly object _sync = new();
    private UpdateStateSnapshot _snapshot = new(UpdateState.None, UpdateFailureReason.None, null);

    public AndroidUpdateManager(
        IVersionComparer versionComparer,
        IUpdateDownloader downloader,
        IHashValidator hashValidator,
        IApkInstaller apkInstaller,
        IFileStorage fileStorage,
        IUpdateEventDispatcher? eventDispatcher = null,
        IUpdateLogger? logger = null)
    {
        _versionComparer = versionComparer;
        _downloader = downloader;
        _hashValidator = hashValidator;
        _apkInstaller = apkInstaller;
        _fileStorage = fileStorage;
        _eventDispatcher = eventDispatcher ?? new ImmediateEventDispatcher();
        _logger = logger ?? new NoOpUpdateLogger();
    }

    public event EventHandler<UpdateFoundEventArgs>? UpdateFound;
    public event EventHandler<DownloadProgressChangedEventArgs>? DownloadProgressChanged;
    public event EventHandler<UpdateCompletedEventArgs>? UpdateCompleted;
    public event EventHandler<UpdateFailedEventArgs>? UpdateFailed;

    public UpdateStateSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    public Task<UpdateCheckResult> CheckForUpdateAsync(UpdatePackageInfo packageInfo, string currentVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetState(UpdateState.Checking, UpdateFailureReason.None, "Checking for updates.");

        if (string.IsNullOrWhiteSpace(currentVersion) || string.IsNullOrWhiteSpace(packageInfo.Version))
        {
            var invalid = new UpdateCheckResult
            {
                Success = false,
                UpdateFound = false,
                State = UpdateState.Failed,
                FailureReason = UpdateFailureReason.InvalidMetadata,
                Message = "Current version or target version is empty.",
                PackageInfo = packageInfo,
                CurrentVersion = currentVersion
            };

            HandleFailure(invalid);
            return Task.FromResult(invalid);
        }

        if (!_versionComparer.TryCompare(currentVersion, packageInfo.Version, out var compare, out var error))
        {
            var failed = new UpdateCheckResult
            {
                Success = false,
                UpdateFound = false,
                State = UpdateState.Failed,
                FailureReason = UpdateFailureReason.VersionComparisonFailed,
                Message = error ?? "Failed to compare versions.",
                PackageInfo = packageInfo,
                CurrentVersion = currentVersion
            };

            HandleFailure(failed);
            return Task.FromResult(failed);
        }

        if (compare > 0)
        {
            SetState(UpdateState.UpdateAvailable, UpdateFailureReason.None, "Update available.");
            RaiseUpdateFound(packageInfo, currentVersion);

            return Task.FromResult(new UpdateCheckResult
            {
                Success = true,
                UpdateFound = true,
                State = UpdateState.UpdateAvailable,
                FailureReason = UpdateFailureReason.None,
                Message = "Update available.",
                PackageInfo = packageInfo,
                CurrentVersion = currentVersion
            });
        }

        SetState(UpdateState.Completed, UpdateFailureReason.None, "No update available.");

        return Task.FromResult(new UpdateCheckResult
        {
            Success = true,
            UpdateFound = false,
            State = UpdateState.Completed,
            FailureReason = UpdateFailureReason.None,
            Message = "No update available.",
            PackageInfo = packageInfo,
            CurrentVersion = currentVersion
        });
    }

    public async Task<UpdateOperationResult> DownloadAndVerifyAsync(UpdatePackageInfo packageInfo, CancellationToken cancellationToken = default)
    {
        SetState(UpdateState.Downloading, UpdateFailureReason.None, "Downloading package.");

        var downloadResult = await _downloader.DownloadAsync(
            packageInfo,
            progress => RaiseDownloadProgress(progress),
            cancellationToken).ConfigureAwait(false);

        if (!downloadResult.Success || string.IsNullOrWhiteSpace(downloadResult.FilePath))
        {
            HandleFailure(downloadResult);
            return downloadResult;
        }

        if (packageInfo.FileSize > 0)
        {
            var actualLength = _fileStorage.GetFileLength(downloadResult.FilePath);
            if (actualLength != packageInfo.FileSize)
            {
                _fileStorage.DeleteFile(downloadResult.FilePath);
                var sizeFailed = new UpdateOperationResult
                {
                    Success = false,
                    State = UpdateState.Failed,
                    FailureReason = UpdateFailureReason.FileIoError,
                    Message = $"Downloaded file size mismatch. Expected {packageInfo.FileSize}, actual {actualLength}.",
                    PackageInfo = packageInfo,
                    FilePath = downloadResult.FilePath
                };
                HandleFailure(sizeFailed);
                return sizeFailed;
            }
        }

        SetState(UpdateState.Verifying, UpdateFailureReason.None, "Validating package hash.");
        var hashResult = await _hashValidator.ValidateSha256Async(downloadResult.FilePath, packageInfo.Sha256, cancellationToken).ConfigureAwait(false);

        if (!hashResult.Success)
        {
            _fileStorage.DeleteFile(downloadResult.FilePath);
            var failed = hashResult with
            {
                PackageInfo = packageInfo,
                State = UpdateState.Failed,
                FailureReason = hashResult.FailureReason == UpdateFailureReason.None ? UpdateFailureReason.HashMismatch : hashResult.FailureReason,
                Message = hashResult.Message ?? "SHA256 validation failed."
            };
            HandleFailure(failed);
            return failed;
        }

        var completed = new UpdateOperationResult
        {
            Success = true,
            State = UpdateState.ReadyToInstall,
            FailureReason = UpdateFailureReason.None,
            Message = "Package downloaded and verified.",
            PackageInfo = packageInfo,
            FilePath = downloadResult.FilePath
        };

        SetState(completed.State, completed.FailureReason, completed.Message);
        RaiseCompleted(completed);
        return completed;
    }

    public async Task<InstallResult> LaunchInstallerAsync(UpdatePackageInfo packageInfo, string apkFilePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetState(UpdateState.Installing, UpdateFailureReason.None, "Launching installer.");

        var result = await _apkInstaller.LaunchInstallAsync(packageInfo, apkFilePath, cancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            SetState(UpdateState.Installing, UpdateFailureReason.None, result.Message ?? "Installer launched.");
            RaiseCompleted(result);
        }
        else
        {
            HandleFailure(result);
        }

        return result;
    }

    private void SetState(UpdateState state, UpdateFailureReason failureReason, string? message)
    {
        lock (_sync)
        {
            _snapshot = new UpdateStateSnapshot(state, failureReason, message);
        }
    }

    private void HandleFailure(UpdateOperationResult result)
    {
        SetState(result.State == UpdateState.Canceled ? UpdateState.Canceled : UpdateState.Failed, result.FailureReason, result.Message);
        _logger.LogError(result.Message ?? "Update failed.", result.Exception);
        RaiseFailed(result);
    }

    private void RaiseUpdateFound(UpdatePackageInfo packageInfo, string currentVersion)
    {
        var args = new UpdateFoundEventArgs(packageInfo, currentVersion);
        _eventDispatcher.Dispatch(() => UpdateFound?.Invoke(this, args));
    }

    private void RaiseDownloadProgress(DownloadProgressInfo progress)
    {
        var args = new DownloadProgressChangedEventArgs(progress);
        _eventDispatcher.Dispatch(() => DownloadProgressChanged?.Invoke(this, args));
    }

    private void RaiseCompleted(UpdateOperationResult result)
    {
        var args = new UpdateCompletedEventArgs(result);
        _eventDispatcher.Dispatch(() => UpdateCompleted?.Invoke(this, args));
    }

    private void RaiseFailed(UpdateOperationResult result)
    {
        var args = new UpdateFailedEventArgs(result);
        _eventDispatcher.Dispatch(() => UpdateFailed?.Invoke(this, args));
    }
}
