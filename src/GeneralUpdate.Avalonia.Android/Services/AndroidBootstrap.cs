using System.Text.Json;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Events;
using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Services;

public sealed class AndroidBootstrap : IAndroidBootstrap
{
    private readonly IVersionComparer _versionComparer;
    private readonly IUpdateDownloader _downloader;
    private readonly IHashValidator _hashValidator;
    private readonly IApkInstaller _apkInstaller;
    private readonly IFileStorage _fileStorage;
    private readonly IUpdateEventDispatcher _eventDispatcher;
    private readonly IUpdateLogger _logger;
    private readonly IUpdatePackageSource _packageSource;
    private readonly IInstallationStore _installationStore;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private volatile bool _disposed;
    private bool _resourcesDisposed;

    private readonly object _sync = new();
    private UpdateStateSnapshot _snapshot = new(UpdateState.None, UpdateFailureReason.None, null);
    private Func<UpdateInfoEventArgs, bool>? _updatePrecheck;

    public AndroidBootstrap(
        IVersionComparer versionComparer,
        IUpdateDownloader downloader,
        IHashValidator hashValidator,
        IApkInstaller apkInstaller,
        IFileStorage fileStorage,
        IUpdateEventDispatcher? eventDispatcher = null,
        IUpdateLogger? logger = null,
        UpdateServerOptions? updateServer = null,
        HttpClient? httpClient = null,
        HttpDownloadOptions? httpOptions = null,
        string? installationStateFilePath = null)
        : this(versionComparer, downloader, hashValidator, apkInstaller, fileStorage,
            HttpUpdatePackageClient.Create(httpClient, httpOptions, versionComparer, updateServer),
            new JsonFileInstallationStore(installationStateFilePath ?? JsonFileInstallationStore.DefaultPath),
            eventDispatcher, logger)
    {
    }

    /// <summary>
    /// Dependency-injection constructor. Disposable downloader and package source lifetimes are owned by the bootstrap;
    /// HttpClient ownership is specified when constructing those services.
    /// </summary>
    public AndroidBootstrap(
        IVersionComparer versionComparer,
        IUpdateDownloader downloader,
        IHashValidator hashValidator,
        IApkInstaller apkInstaller,
        IFileStorage fileStorage,
        IUpdatePackageSource packageSource,
        IInstallationStore installationStore,
        IUpdateEventDispatcher? eventDispatcher = null,
        IUpdateLogger? logger = null)
    {
        _versionComparer = versionComparer ?? throw new ArgumentNullException(nameof(versionComparer));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _hashValidator = hashValidator ?? throw new ArgumentNullException(nameof(hashValidator));
        _apkInstaller = apkInstaller ?? throw new ArgumentNullException(nameof(apkInstaller));
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _eventDispatcher = eventDispatcher ?? new ImmediateEventDispatcher();
        _logger = logger ?? new NoOpUpdateLogger();
        _packageSource = packageSource ?? throw new ArgumentNullException(nameof(packageSource));
        _installationStore = installationStore ?? throw new ArgumentNullException(nameof(installationStore));
    }

    public event EventHandler<ValidateEventArgs>? AddListenerValidate;
    public event EventHandler<DownloadProgressChangedEventArgs>? AddListenerDownloadProgressChanged;
    public event EventHandler<UpdateCompletedEventArgs>? AddListenerUpdateCompleted;
    public event EventHandler<InstallationConfirmedEventArgs>? AddListenerInstallationConfirmed;
    public event EventHandler<UpdateFailedEventArgs>? AddListenerUpdateFailed;

    public UpdateStateSnapshot GetSnapshot()
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            return _snapshot;
        }
    }

    public IAndroidBootstrap AddListenerUpdatePrecheck(Func<UpdateInfoEventArgs, bool> func)
    {
        ThrowIfDisposed();
        _updatePrecheck = func ?? throw new ArgumentNullException(nameof(func));
        return this;
    }

    public async Task<UpdateCheckResult> ValidateAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await ValidateCoreAsync(currentVersion, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseOperation();
        }
    }

    private async Task<UpdateCheckResult> ValidateCoreAsync(string currentVersion, CancellationToken cancellationToken)
    {
        SetState(UpdateState.Checking, UpdateFailureReason.None, "Checking for updates.");

        UpdatePackageInfo? packageInfo;
        try
        {
            if (string.IsNullOrWhiteSpace(currentVersion))
            {
                throw new InvalidDataException("The current application version is required to query the update server.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            packageInfo = await _packageSource.GetLatestAsync(currentVersion, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (IsQueryFailure(ex))
        {
            var canceled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
            var failure = new UpdateCheckResult
            {
                Success = false,
                UpdateFound = false,
                State = canceled ? UpdateState.Canceled : UpdateState.Failed,
                FailureReason = canceled
                    ? UpdateFailureReason.Canceled
                    : ex is HttpRequestException or OperationCanceledException or IOException
                        ? UpdateFailureReason.NetworkError
                        : UpdateFailureReason.InvalidMetadata,
                Message = canceled
                    ? "Update check canceled."
                    : "Failed to query the update server.",
                CurrentVersion = currentVersion,
                Exception = ex
            };

            HandleFailure(failure);
            return failure;
        }

        if (packageInfo is null)
        {
            SetState(UpdateState.Completed, UpdateFailureReason.None, "No update available.");

            return new UpdateCheckResult
            {
                Success = true,
                UpdateFound = false,
                State = UpdateState.Completed,
                FailureReason = UpdateFailureReason.None,
                Message = "No update available.",
                CurrentVersion = currentVersion
            };
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
            return failed;
        }

        if (compare > 0)
        {
            var available = new UpdateCheckResult
            {
                Success = true,
                UpdateFound = true,
                State = UpdateState.UpdateAvailable,
                FailureReason = UpdateFailureReason.None,
                Message = "Update available.",
                PackageInfo = packageInfo,
                CurrentVersion = currentVersion
            };

            if (ShouldSkipUpdate(available, packageInfo, currentVersion))
            {
                var skipped = available with
                {
                    UpdateFound = false,
                    State = UpdateState.Completed,
                    Message = "Update skipped by pre-check callback."
                };

                SetState(skipped.State, skipped.FailureReason, skipped.Message);
                return skipped;
            }

            SetState(UpdateState.UpdateAvailable, UpdateFailureReason.None, "Update available.");
            RaiseValidate(packageInfo, currentVersion);

            return available;
        }

        SetState(UpdateState.Completed, UpdateFailureReason.None, "No update available.");

        return new UpdateCheckResult
        {
            Success = true,
            UpdateFound = false,
            State = UpdateState.Completed,
            FailureReason = UpdateFailureReason.None,
            Message = "No update available.",
            PackageInfo = packageInfo,
            CurrentVersion = currentVersion
        };
    }

    public async Task<UpdateOperationResult> DownloadAndVerifyAsync(UpdatePackageInfo packageInfo, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await DownloadAndVerifyCoreAsync(packageInfo, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseOperation();
        }
    }

    public async Task<UpdatePreparationResult> PrepareUpdateAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var check = await ValidateCoreAsync(currentVersion, cancellationToken).ConfigureAwait(false);
            if (!check.Success || !check.UpdateFound || check.PackageInfo is null)
                return UpdatePreparationResult.From(check, check.UpdateFound);

            var prepared = await DownloadAndVerifyCoreAsync(check.PackageInfo, cancellationToken).ConfigureAwait(false);
            return UpdatePreparationResult.From(prepared, updateFound: true);
        }
        finally
        {
            ReleaseOperation();
        }
    }

    private async Task<UpdateOperationResult> DownloadAndVerifyCoreAsync(UpdatePackageInfo packageInfo, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetState(UpdateState.Downloading, UpdateFailureReason.None, "Downloading package.");

            var downloadResult = await _downloader.DownloadAsync(
                packageInfo,
                progress => RaiseDownloadProgress(progress),
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (!downloadResult.Success)
            {
                HandleFailure(downloadResult);
                return downloadResult;
            }

            if (string.IsNullOrWhiteSpace(downloadResult.FilePath))
            {
                var invalid = downloadResult with
                {
                    Success = false,
                    State = UpdateState.Failed,
                    FailureReason = UpdateFailureReason.FileIoError,
                    Message = "The downloader returned success without a file path.",
                    PackageInfo = packageInfo
                };
                HandleFailure(invalid);
                return invalid;
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
            cancellationToken.ThrowIfCancellationRequested();

            if (!hashResult.Success)
            {
                if (hashResult.FailureReason != UpdateFailureReason.Canceled)
                    _fileStorage.DeleteFile(downloadResult.FilePath);
                var failed = hashResult with
                {
                    PackageInfo = packageInfo,
                    State = hashResult.FailureReason == UpdateFailureReason.Canceled ? UpdateState.Canceled : UpdateState.Failed,
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
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            var canceled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
            var failure = new UpdateOperationResult
            {
                State = canceled ? UpdateState.Canceled : UpdateState.Failed,
                FailureReason = canceled ? UpdateFailureReason.Canceled
                    : ex is HttpRequestException or OperationCanceledException ? UpdateFailureReason.NetworkError : UpdateFailureReason.FileIoError,
                Message = canceled ? "Package preparation canceled." : "Package preparation failed.",
                PackageInfo = packageInfo,
                Exception = ex
            };
            HandleFailure(failure);
            return failure;
        }
    }

    public async Task<InstallResult> LaunchInstallerAsync(UpdatePackageInfo packageInfo, string apkFilePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_versionComparer.TryCompare(packageInfo.Version, packageInfo.Version, out _, out var error))
            {
                var invalidVersion = new InstallResult
                {
                    Success = false,
                    State = UpdateState.Failed,
                    FailureReason = UpdateFailureReason.VersionComparisonFailed,
                    Message = error,
                    PackageInfo = packageInfo,
                    FilePath = apkFilePath
                };
                HandleFailure(invalidVersion);
                return invalidVersion;
            }

            try
            {
                // Persist before handing off: Android may kill this process as soon as installation starts.
                await _installationStore.SaveAsync(new InstallationRecord
                {
                    TargetVersion = packageInfo.Version,
                    RequestedAt = DateTimeOffset.UtcNow
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsInstallationStorageFailure(ex))
            {
                var canceled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
                var failure = new InstallResult
                {
                    Success = false,
                    State = canceled ? UpdateState.Canceled : UpdateState.Failed,
                    FailureReason = canceled ? UpdateFailureReason.Canceled : UpdateFailureReason.FileIoError,
                    Message = "Could not persist the installation target. The installer was not launched.",
                    PackageInfo = packageInfo,
                    FilePath = apkFilePath,
                    Exception = ex
                };
                HandleFailure(failure);
                return failure;
            }
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
        catch (OperationCanceledException ex)
        {
            var canceled = cancellationToken.IsCancellationRequested;
            var failure = new InstallResult
            {
                State = canceled ? UpdateState.Canceled : UpdateState.Failed,
                FailureReason = canceled ? UpdateFailureReason.Canceled : UpdateFailureReason.InstallLaunchFailed,
                Message = canceled ? "Installer launch canceled." : "Installer launch did not complete.",
                PackageInfo = packageInfo,
                FilePath = apkFilePath,
                Exception = ex
            };
            HandleFailure(failure);
            return failure;
        }
        finally
        {
            ReleaseOperation();
        }
    }

    public async Task<InstallationCheckResult> CheckInstallationAsync(
        string currentVersion, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            InstallationCheckResult Fail(UpdateFailureReason reason, string message, Exception? exception = null)
            {
                var failure = new InstallationCheckResult
                {
                    CurrentVersion = currentVersion,
                    Success = false,
                    State = reason == UpdateFailureReason.Canceled ? UpdateState.Canceled : UpdateState.Failed,
                    FailureReason = reason,
                    Message = message,
                    Exception = exception
                };
                HandleFailure(failure);
                return failure;
            }

            if (!_versionComparer.TryCompare(currentVersion, currentVersion, out _, out var versionError))
            {
                return Fail(UpdateFailureReason.VersionComparisonFailed, versionError ?? "Invalid installed version.");
            }

            InstallationCheckResult result;
            bool newlyConfirmed;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = await _installationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (record is null)
                {
                    SetState(UpdateState.None, UpdateFailureReason.None, "No installation attempt recorded.");
                    return new InstallationCheckResult
                    {
                        CurrentVersion = currentVersion,
                        Success = true,
                        State = UpdateState.None,
                        Message = "No installation attempt recorded."
                    };
                }

                if (record.ConfirmedAt.HasValue &&
                    (!_versionComparer.TryCompare(record.InstalledVersion!, record.TargetVersion, out var confirmedComparison, out _) ||
                     confirmedComparison > 0))
                    throw new InvalidDataException("The confirmed installation record contains inconsistent versions.");

                if (!_versionComparer.TryCompare(currentVersion, record.TargetVersion, out var comparison, out versionError))
                {
                    return Fail(UpdateFailureReason.VersionComparisonFailed, versionError ?? "Cannot compare installation versions.");
                }

                var installed = comparison <= 0;
                newlyConfirmed = installed && record.ConfirmedAt is null;
                if (newlyConfirmed)
                {
                    record = record with { InstalledVersion = currentVersion, ConfirmedAt = DateTimeOffset.UtcNow };
                    await _installationStore.SaveAsync(record, cancellationToken).ConfigureAwait(false);
                }

                result = new InstallationCheckResult
                {
                    CurrentVersion = currentVersion,
                    Record = record,
                    Success = true,
                    State = installed ? UpdateState.Installed : UpdateState.InstallationPending,
                    Message = installed
                        ? "The installed version has reached the recorded update target."
                        : "The recorded update target is not installed yet. Installation may be pending or canceled."
                };
            }
            catch (Exception ex) when (IsInstallationStorageFailure(ex))
            {
                return Fail(
                    ex is OperationCanceledException && cancellationToken.IsCancellationRequested
                        ? UpdateFailureReason.Canceled : UpdateFailureReason.FileIoError,
                    "Could not reconcile the installation record.", ex);
            }

            SetState(result.State, result.FailureReason, result.Message);
            if (newlyConfirmed)
            {
                _eventDispatcher.Dispatch(() =>
                    AddListenerInstallationConfirmed?.Invoke(this, new InstallationConfirmedEventArgs(result)));
            }
            return result;
        }
        finally
        {
            ReleaseOperation();
        }
    }

    public async Task<UpdateOperationResult> ResetInstallationAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            await _installationStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            var result = new UpdateOperationResult
            {
                Success = true,
                State = UpdateState.None,
                Message = "Installation record cleared. The installed application was not modified."
            };
            SetState(result.State, result.FailureReason, result.Message);
            return result;
        }
        catch (Exception ex) when (IsInstallationStorageFailure(ex))
        {
            var canceled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
            var failure = new UpdateOperationResult
            {
                State = canceled ? UpdateState.Canceled : UpdateState.Failed,
                FailureReason = canceled ? UpdateFailureReason.Canceled : UpdateFailureReason.FileIoError,
                Message = "Could not clear the installation record.",
                Exception = ex
            };
            HandleFailure(failure);
            return failure;
        }
        finally
        {
            ReleaseOperation();
        }
    }

    private static bool IsInstallationStorageFailure(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or OperationCanceledException;

    /// <summary>
    /// Transport, protocol and metadata problems are reported as validation failures.
    /// User cancellation is reported as a canceled state instead.
    /// </summary>
    private static bool IsQueryFailure(Exception ex) =>
        ex is HttpRequestException or OperationCanceledException or JsonException or
            InvalidDataException or IOException or ArgumentException;

    private bool ShouldSkipUpdate(UpdateCheckResult result, UpdatePackageInfo packageInfo, string currentVersion)
    {
        if (packageInfo.IsForced || _updatePrecheck is null)
        {
            return false;
        }

        // Same contract as GeneralUpdate.Core's ClientStrategy.CanSkip:
        // the callback receives the discovered update information and returns
        // true to skip the update, false to continue.
        return _updatePrecheck(new UpdateInfoEventArgs(packageInfo, currentVersion, result));
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

    private void RaiseValidate(UpdatePackageInfo packageInfo, string currentVersion)
    {
        var args = new ValidateEventArgs(packageInfo, currentVersion);
        _eventDispatcher.Dispatch(() => AddListenerValidate?.Invoke(this, args));
    }

    private void RaiseDownloadProgress(DownloadProgressInfo progress)
    {
        var args = new DownloadProgressChangedEventArgs(progress);
        _eventDispatcher.Dispatch(() => AddListenerDownloadProgressChanged?.Invoke(this, args));
    }

    private void RaiseCompleted(UpdateOperationResult result)
    {
        var args = new UpdateCompletedEventArgs(result);
        _eventDispatcher.Dispatch(() => AddListenerUpdateCompleted?.Invoke(this, args));
    }

    private void RaiseFailed(UpdateOperationResult result)
    {
        var args = new UpdateFailedEventArgs(result);
        _eventDispatcher.Dispatch(() => AddListenerUpdateFailed?.Invoke(this, args));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_operationGate.CurrentCount != 0)
                DisposeResources();
        }
    }

    private void ReleaseOperation()
    {
        lock (_sync)
        {
            try
            {
                if (_disposed) DisposeResources();
            }
            finally
            {
                // Keep the managed gate alive so queued callers can observe ObjectDisposedException.
                _operationGate.Release();
            }
        }
    }

    private void DisposeResources()
    {
        if (_resourcesDisposed) return;
        _resourcesDisposed = true;
        try
        {
            if (_packageSource is IDisposable disposableSource)
                disposableSource.Dispose();
        }
        finally
        {
            if (_downloader is IDisposable disposableDownloader)
                disposableDownloader.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
