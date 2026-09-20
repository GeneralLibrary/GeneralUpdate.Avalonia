using System.Text.Json;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Events;
using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Services;

public sealed class AndroidBootstrap : IAndroidBootstrap, IAsyncDisposable
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
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    private bool _shutdownCanceled;
    private bool _resourcesReleased;
    private int _operations;

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

    public async Task<UpdateCheckResult> ValidateAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken = operation.Token;
        try
        {
            SetState(UpdateState.Checking, UpdateFailureReason.None, "Checking for updates.");

            UpdatePackageInfo? packageInfo;
            try
            {
                ThrowIfCancellationRequested(cancellationToken);
                if (string.IsNullOrWhiteSpace(currentVersion))
                {
                    throw new InvalidDataException("The current application version is required to query the update server.");
                }

                if (_updateServer is null || _packageClient is null)
                {
                    throw new InvalidDataException("Querying the update server requires AndroidUpdateOptions.UpdateServer.");
                }

                packageInfo = _updateServer.UseJsonEndpoint
                    ? await _packageClient.GetPackageInfoAsync(_updateServer.RequestUrl, cancellationToken).ConfigureAwait(false)
                    : await _packageClient.GetPackageInfoAsync(
                        _updateServer.RequestUrl,
                        new UpdatePackageRequest
                        {
                            Version = currentVersion,
                            AppKey = _updateServer.AppKey,
                            AppType = _updateServer.AppType,
                            Platform = _updateServer.Platform,
                            ProductId = _updateServer.ProductId
                        },
                        cancellationToken).ConfigureAwait(false);
                ThrowIfCancellationRequested(cancellationToken);
            }
            catch (Exception ex) when (IsQueryFailure(ex))
            {
                var canceled = ex is OperationCanceledException && IsCancellationRequested(cancellationToken);
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

                bool skip;
                try
                {
                    skip = ShouldSkipUpdate(available, packageInfo, currentVersion);
                }
                catch (Exception ex)
                {
                    var canceled = ex is OperationCanceledException && IsCancellationRequested(cancellationToken);
                    var failed = available with
                    {
                        Success = false,
                        UpdateFound = false,
                        State = canceled ? UpdateState.Canceled : UpdateState.Failed,
                        FailureReason = canceled ? UpdateFailureReason.Canceled : UpdateFailureReason.Unknown,
                        Message = canceled ? "Update check canceled." : "Update pre-check callback failed.",
                        Exception = ex
                    };
                    HandleFailure(failed);
                    return failed;
                }
                ThrowIfCancellationRequested(cancellationToken);
                if (skip)
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
        catch (OperationCanceledException ex)
        {
            var failure = new UpdateCheckResult
            {
                Success = false,
                State = UpdateState.Canceled,
                FailureReason = UpdateFailureReason.Canceled,
                Message = "Update check canceled.",
                CurrentVersion = currentVersion,
                Exception = ex
            };
            HandleFailure(failure);
            return failure;
        }
    }

    public async Task<UpdateOperationResult> DownloadAndVerifyAsync(UpdatePackageInfo packageInfo, CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken = operation.Token;
        string? filePath = null;
        try
        {
            ThrowIfCancellationRequested(cancellationToken);
            SetState(UpdateState.Downloading, UpdateFailureReason.None, "Downloading package.");

            var downloadResult = await _downloader.DownloadAsync(
                packageInfo,
                progress => RaiseDownloadProgress(progress),
                cancellationToken).ConfigureAwait(false);

            filePath = downloadResult.FilePath;
            ThrowIfCancellationRequested(cancellationToken);
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
                    TryDeleteFile(downloadResult.FilePath);
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
            ThrowIfCancellationRequested(cancellationToken);

            if (!hashResult.Success)
            {
                TryDeleteFile(downloadResult.FilePath);
                var canceled = hashResult.State == UpdateState.Canceled || hashResult.FailureReason == UpdateFailureReason.Canceled;
                var failed = hashResult with
                {
                    PackageInfo = packageInfo,
                    FilePath = downloadResult.FilePath,
                    State = canceled ? UpdateState.Canceled : UpdateState.Failed,
                    FailureReason = canceled ? UpdateFailureReason.Canceled
                        : hashResult.FailureReason == UpdateFailureReason.None ? UpdateFailureReason.HashMismatch : hashResult.FailureReason,
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
        catch (Exception ex)
        {
            TryDeleteFile(filePath);
            var canceled = ex is OperationCanceledException && IsCancellationRequested(cancellationToken);
            var failure = new UpdateOperationResult
            {
                Success = false,
                State = canceled ? UpdateState.Canceled : UpdateState.Failed,
                FailureReason = canceled ? UpdateFailureReason.Canceled
                    : ex is IOException or UnauthorizedAccessException ? UpdateFailureReason.FileIoError
                    : ex is HttpRequestException or OperationCanceledException ? UpdateFailureReason.NetworkError
                    : UpdateFailureReason.Unknown,
                Message = canceled ? "Download or verification canceled." : "Download or verification failed.",
                PackageInfo = packageInfo,
                FilePath = filePath,
                Exception = ex
            };
            HandleFailure(failure);
            return failure;
        }
    }

    public async Task<InstallResult> LaunchInstallerAsync(UpdatePackageInfo packageInfo, string apkFilePath, CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken = operation.Token;
        try
        {
            ThrowIfCancellationRequested(cancellationToken);
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
        catch (Exception ex)
        {
            var canceled = ex is OperationCanceledException && IsCancellationRequested(cancellationToken);
            var failure = new InstallResult
            {
                Success = false,
                State = canceled ? UpdateState.Canceled : UpdateState.Failed,
                FailureReason = canceled ? UpdateFailureReason.Canceled
                    : ex is UnauthorizedAccessException ? UpdateFailureReason.InstallPermissionDenied
                    : ex is IOException ? UpdateFailureReason.FileIoError : UpdateFailureReason.InstallLaunchFailed,
                Message = canceled ? "Installer launch canceled." : "Failed to launch installer.",
                PackageInfo = packageInfo,
                FilePath = apkFilePath,
                Exception = ex
            };
            HandleFailure(failure);
            return failure;
        }
    }

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
        LogError(result.Message ?? "Update failed.", result.Exception);
        RaiseFailed(result);
    }

    private void RaiseValidate(UpdatePackageInfo packageInfo, string currentVersion)
    {
        var args = new ValidateEventArgs(packageInfo, currentVersion);
        Dispatch(AddListenerValidate, args);
    }

    private void RaiseDownloadProgress(DownloadProgressInfo progress)
    {
        var args = new DownloadProgressChangedEventArgs(progress);
        Dispatch(AddListenerDownloadProgressChanged, args);
    }

    private void RaiseCompleted(UpdateOperationResult result)
    {
        var args = new UpdateCompletedEventArgs(result);
        Dispatch(AddListenerUpdateCompleted, args);
    }

    private void RaiseFailed(UpdateOperationResult result)
    {
        var args = new UpdateFailedEventArgs(result);
        Dispatch(AddListenerUpdateFailed, args);
    }

    private void Dispatch<T>(EventHandler<T>? handlers, T args) where T : EventArgs
    {
        if (handlers is null)
        {
            return;
        }

        try
        {
            _eventDispatcher.Dispatch(() =>
            {
                foreach (EventHandler<T> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(this, args);
                    }
                    catch (Exception ex)
                    {
                        LogError("Update event subscriber failed.", ex);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            LogError("Update event dispatch failed.", ex);
        }
    }

    private void LogError(string message, Exception? exception)
    {
        try
        {
            _logger.LogError(message, exception);
        }
        catch
        {
            // Diagnostics must not replace the operation's outcome.
        }
    }

    private void TryDeleteFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            _fileStorage.DeleteFile(filePath);
        }
        catch (Exception ex)
        {
            LogError("Failed to remove an unverified package.", ex);
        }
    }

    private bool IsCancellationRequested(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested || _shutdown.IsCancellationRequested;

    private void ThrowIfCancellationRequested(CancellationToken cancellationToken)
    {
        // CancelAsync marks shutdown immediately, but linked-token callbacks may still be queued.
        _shutdown.Token.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<OperationLease> EnterOperationAsync(CancellationToken cancellationToken)
    {
        OperationLease operation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            operation = new OperationLease(this, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token));
            _operations++;
        }

        try
        {
            await _operationGate.WaitAsync(operation.Token).ConfigureAwait(false);
            operation.Acquired = true;
            ThrowIfCancellationRequested(operation.Token);
            return operation;
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Requests cancellation without blocking callbacks. Resources are released after all operations
    /// and gate waiters drain; use <see cref="DisposeAsync"/> to await that release outside callbacks.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _ = CancelAndDrainAsync();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return new ValueTask(_drained.Task);
    }

    private async Task CancelAndDrainAsync()
    {
        try
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogError("An update shutdown cancellation callback failed.", ex);
        }

        lock (_sync)
        {
            _shutdownCanceled = true;
        }
        TryReleaseResources();
    }

    private void TryReleaseResources()
    {
        lock (_sync)
        {
            if (!_shutdownCanceled || _operations != 0 || _resourcesReleased)
            {
                return;
            }

            _resourcesReleased = true;
        }

        try
        {
            try
            {
                _packageClient?.Dispose();
            }
            finally
            {
                (_downloader as IDisposable)?.Dispose();
            }
        }
        catch (Exception ex)
        {
            LogError("Failed to dispose update resources.", ex);
        }
        finally
        {
            _operationGate.Dispose();
            _shutdown.Dispose();
            _drained.TrySetResult();
        }
    }

    private sealed class OperationLease(AndroidBootstrap owner, CancellationTokenSource cancellation) : IDisposable
    {
        public CancellationToken Token => cancellation.Token;
        public bool Acquired { get; set; }

        public void Dispose()
        {
            if (Acquired)
            {
                owner._operationGate.Release();
            }
            cancellation.Dispose();
            lock (owner._sync)
            {
                owner._operations--;
            }
            owner.TryReleaseResources();
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
