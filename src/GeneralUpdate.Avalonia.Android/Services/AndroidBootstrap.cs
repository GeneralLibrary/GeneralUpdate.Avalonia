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
    private readonly UpdateServerOptions? _updateServer;
    private readonly HttpUpdatePackageClient? _packageClient;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _disposed;

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
        HttpDownloadOptions? httpOptions = null)
    {
        _versionComparer = versionComparer ?? throw new ArgumentNullException(nameof(versionComparer));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _hashValidator = hashValidator ?? throw new ArgumentNullException(nameof(hashValidator));
        _apkInstaller = apkInstaller ?? throw new ArgumentNullException(nameof(apkInstaller));
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _eventDispatcher = eventDispatcher ?? new ImmediateEventDispatcher();
        _logger = logger ?? new NoOpUpdateLogger();
        _updateServer = updateServer;
        if (updateServer is not null)
        {
            _packageClient = HttpUpdatePackageClient.Create(httpClient, httpOptions, _versionComparer);
        }
    }

    public event EventHandler<ValidateEventArgs>? AddListenerValidate;
    public event EventHandler<DownloadProgressChangedEventArgs>? AddListenerDownloadProgressChanged;
    public event EventHandler<UpdateCompletedEventArgs>? AddListenerUpdateCompleted;
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

    public Task<UpdateCheckResult> ValidateAsync(string currentVersion, CancellationToken cancellationToken = default)
        => ValidateCoreAsync(null, currentVersion, queryServer: true, cancellationToken);

    public Task<UpdateCheckResult> ValidateAsync(UpdatePackageInfo packageInfo, string currentVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageInfo);
        return ValidateCoreAsync(packageInfo, currentVersion, queryServer: false, cancellationToken);
    }

    private async Task<UpdateCheckResult> ValidateCoreAsync(
        UpdatePackageInfo? packageInfo, string currentVersion, bool queryServer, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetState(UpdateState.Checking, UpdateFailureReason.None, "Checking for updates.");

            if (queryServer)
            {
                try
                {
                    if (_updateServer is null || _packageClient is null || string.IsNullOrWhiteSpace(currentVersion))
                    {
                        throw new InvalidDataException("An update server and current version must be configured.");
                    }

                    packageInfo = _updateServer.UseJsonEndpoint
                        ? await _packageClient.GetPackageInfoAsync(_updateServer.RequestUrl, cancellationToken).ConfigureAwait(false)
                        : await _packageClient.GetPackageInfoAsync(_updateServer.RequestUrl, new UpdatePackageRequest
                        {
                            Version = currentVersion,
                            AppKey = _updateServer.AppKey,
                            AppType = _updateServer.AppType,
                            Platform = _updateServer.Platform,
                            ProductId = _updateServer.ProductId
                        }, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or
                    JsonException or InvalidDataException or IOException or ArgumentException)
                {
                    var canceled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
                    var failure = new UpdateCheckResult
                    {
                        State = canceled ? UpdateState.Canceled : UpdateState.Failed,
                        FailureReason = canceled ? UpdateFailureReason.Canceled
                            : ex is HttpRequestException or OperationCanceledException or IOException ? UpdateFailureReason.NetworkError
                            : UpdateFailureReason.InvalidMetadata,
                        Message = canceled ? "Update check canceled." : "Failed to query update metadata.",
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
                        State = UpdateState.Completed,
                        CurrentVersion = currentVersion,
                        Message = "No update available."
                    };
                }
            }

            ArgumentNullException.ThrowIfNull(packageInfo);
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
                return invalid;
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
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<UpdateOperationResult> DownloadAndVerifyAsync(UpdatePackageInfo packageInfo, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<InstallResult> LaunchInstallerAsync(UpdatePackageInfo packageInfo, string apkFilePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
        finally
        {
            _operationGate.Release();
        }
    }

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
        if (_disposed)
        {
            return;
        }

        _operationGate.Dispose();
        _packageClient?.Dispose();

        if (_downloader is IDisposable disposableDownloader)
        {
            disposableDownloader.Dispose();
        }

        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
