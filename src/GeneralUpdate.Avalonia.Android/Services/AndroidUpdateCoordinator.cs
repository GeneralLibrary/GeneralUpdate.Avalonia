using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Enums;
using GeneralUpdate.Avalonia.Android.Events;
using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Services;

/// <summary>
/// Durable, explicitly initiated update orchestration. Does not own a supplied bootstrap unless
/// requested. Dispose requests cancellation without blocking; await DisposeAsync outside callbacks
/// to wait for operations to drain and for owned resources to be released.
/// </summary>
public sealed class AndroidUpdateCoordinator : IAndroidUpdateCoordinator
{
    private readonly IAndroidBootstrap _bootstrap;
    private readonly IPendingUpdateStore _store;
    private readonly IVersionComparer _versions;
    private readonly IUpdateEventDispatcher _dispatcher;
    private readonly bool _ownsBootstrap;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lifetime = new();
    private bool _disposed;
    private bool _shutdownCanceled;
    private bool _releasing;
    private int _operations;

    public AndroidUpdateCoordinator(
        IAndroidBootstrap bootstrap,
        IPendingUpdateStore store,
        IVersionComparer? versionComparer = null,
        IUpdateEventDispatcher? eventDispatcher = null,
        bool ownsBootstrap = false)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _versions = versionComparer ?? new SystemVersionComparer();
        _dispatcher = eventDispatcher ?? new ImmediateEventDispatcher();
        _ownsBootstrap = ownsBootstrap;
        _bootstrap.AddListenerDownloadProgressChanged += ForwardDownloadProgress;
    }

    public event EventHandler<UpdateCoordinatorEventArgs>? StateChanged;
    public event EventHandler<DownloadProgressChangedEventArgs>? AddListenerDownloadProgressChanged;

    public IAndroidUpdateCoordinator AddListenerUpdatePrecheck(Func<UpdateInfoEventArgs, bool> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _bootstrap.AddListenerUpdatePrecheck(func);
        }
        return this;
    }

    private void ForwardDownloadProgress(object? sender, DownloadProgressChangedEventArgs args) =>
        Dispatch(AddListenerDownloadProgressChanged, args);

    public Task<UpdateCoordinatorResult> RunAsync(string currentVersion, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (operation, token) =>
        {
            await ReadPendingAsync(operation, token).ConfigureAwait(false);
            if (operation.Pending is not null)
                return Finish(operation, UpdateCoordinatorOutcome.PendingUpdateExists,
                    "Reconcile, explicitly retry, or abandon the pending update before starting another.");
            return await RunCoreAsync(operation, currentVersion, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<UpdateCoordinatorResult> ReconcileAsync(string currentVersion, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (operation, token) =>
        {
            await ReadPendingAsync(operation, token).ConfigureAwait(false);
            return await ReconcileCoreAsync(operation, currentVersion, token).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// Reconciles first, then rediscovers and verifies the same target; never trusts a persisted
    /// APK path or replaces an earlier intent with a different target during retry.
    /// </summary>
    public Task<UpdateCoordinatorResult> RetryAsync(string currentVersion, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (operation, token) =>
        {
            await ReadPendingAsync(operation, token).ConfigureAwait(false);
            var result = await ReconcileCoreAsync(operation, currentVersion, token, notifyTerminal: false).ConfigureAwait(false);
            if (result.Outcome is not (UpdateCoordinatorOutcome.AwaitingInstallation or UpdateCoordinatorOutcome.RecoveryRequired))
                return Finish(operation, result.Outcome, result.Message!, result.FailureReason);
            return await RunCoreAsync(operation, currentVersion, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<UpdateCoordinatorResult> AbandonAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (operation, token) =>
        {
            // Explicit abandonment can also recover corrupt state, so it does not deserialize first.
            Notify(operation, UpdateCoordinatorStage.Abandoning);
            ThrowIfCancellationRequested(token);
            await _store.ClearAsync(token).ConfigureAwait(false);
            operation.Pending = null;
            return Finish(operation, UpdateCoordinatorOutcome.Abandoned,
                "Update tracking forgotten. Android installation was not canceled; no APK was deleted.");
        }, cancellationToken);

    private async Task ReadPendingAsync(Operation operation, CancellationToken token)
    {
        Notify(operation, UpdateCoordinatorStage.ReadingPending);
        ThrowIfCancellationRequested(token);
        operation.Pending = await _store.ReadAsync(token).ConfigureAwait(false);
        operation.Pending?.Validate();
    }

    private async Task<UpdateCoordinatorResult> RunCoreAsync(Operation operation, string currentVersion, CancellationToken token)
    {
        if (!TryCompare(currentVersion, currentVersion, out _))
            return InvalidVersion(operation);
        Notify(operation, UpdateCoordinatorStage.Checking);
        ThrowIfCancellationRequested(token);
        var check = await _bootstrap.ValidateAsync(currentVersion, token).ConfigureAwait(false);
        ThrowIfCancellationRequested(token);
        if (!check.Success)
            return BootstrapFailure(operation, check);
        if (!check.UpdateFound)
        {
            var skipped = check.PackageInfo is not null &&
                TryCompare(currentVersion, check.PackageInfo.Version, out var available) && available > 0;
            return Finish(operation, skipped ? UpdateCoordinatorOutcome.Skipped : UpdateCoordinatorOutcome.NoUpdate,
                skipped ? "Update skipped by pre-check." : "No update available. Any earlier pending attempt remains tracked.");
        }
        var package = check.PackageInfo;
        if (package is null || !TryCompare(currentVersion, package.Version, out var comparison))
            return InvalidVersion(operation);
        if (comparison <= 0)
            return Finish(operation, UpdateCoordinatorOutcome.NoUpdate, "The discovered package is not newer than the installed version.");
        if (operation.Pending is not null)
        {
            if (!TryCompare(operation.Pending.TargetVersion, package.Version, out var targetComparison))
                return InvalidVersion(operation);
            if (targetComparison != 0)
                return Finish(operation, UpdateCoordinatorOutcome.RecoveryRequired,
                    "Fresh discovery selected a different target. Reconcile the earlier attempt or explicitly abandon tracking before starting another update; its installer may still finish.");
        }

        var intent = new PendingUpdateAttempt
        {
            AttemptId = Guid.NewGuid(),
            OriginalVersion = currentVersion,
            TargetVersion = package.Version,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Phase = PendingUpdatePhase.IntentPersisted
        };
        intent.Validate();
        Notify(operation, UpdateCoordinatorStage.DownloadingAndVerifying);
        ThrowIfCancellationRequested(token);
        var prepared = await _bootstrap.DownloadAndVerifyAsync(package, token).ConfigureAwait(false);
        ThrowIfCancellationRequested(token);
        if (!prepared.Success)
            return BootstrapFailure(operation, prepared);
        if (string.IsNullOrWhiteSpace(prepared.FilePath))
            return Finish(operation, UpdateCoordinatorOutcome.Failed, "Verification returned no APK path.", UpdateFailureReason.InvalidMetadata);

        Notify(operation, UpdateCoordinatorStage.PersistingIntent);
        ThrowIfCancellationRequested(token);
        await _store.WriteAsync(intent, token).ConfigureAwait(false);
        operation.Pending = intent;
        var handoffStarted = false;
        try
        {
            Notify(operation, UpdateCoordinatorStage.LaunchingInstaller);
            ThrowIfCancellationRequested(token);
            handoffStarted = true;
            var launched = await _bootstrap.LaunchInstallerAsync(package, prepared.FilePath, token).ConfigureAwait(false);
            // Once handed off, cancellation cannot cancel Android. Preserve the actual handoff outcome.
            if (launched.Success)
            {
                var handedOff = intent with { Phase = PendingUpdatePhase.InstallerLaunched };
                if (!await TrySaveStatusAsync(operation, handedOff).ConfigureAwait(false))
                    return Finish(operation, UpdateCoordinatorOutcome.RecoveryRequired,
                        "Installer launched, but its status could not be saved. Reconcile the retained intent.",
                        UpdateFailureReason.FileIoError);
                return Finish(operation, UpdateCoordinatorOutcome.InstallerLaunched,
                    "Installer launched. Installation is not confirmed; reconcile the actual installed version later.");
            }
            if (launched.State != UpdateState.Canceled && launched.FailureReason != UpdateFailureReason.Canceled)
                await TrySaveStatusAsync(operation, intent with { Phase = PendingUpdatePhase.Retryable }).ConfigureAwait(false);
            return BootstrapFailure(operation, launched);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || _shutdown.IsCancellationRequested)
        {
            // Cancellation after entering an external installer cannot prove that no handoff occurred.
            if (!handoffStarted)
                await TrySaveStatusAsync(operation, intent with { Phase = PendingUpdatePhase.Retryable }).ConfigureAwait(false);
            throw;
        }
        catch
        {
            // An exception does not prove that an external installer was not launched.
            return Finish(operation, UpdateCoordinatorOutcome.RecoveryRequired,
                "Installer handoff is uncertain. Reconcile before explicitly retrying.", UpdateFailureReason.InstallLaunchFailed);
        }
    }

    private async Task<UpdateCoordinatorResult> ReconcileCoreAsync(
        Operation operation, string currentVersion, CancellationToken token, bool notifyTerminal = true)
    {
        Notify(operation, UpdateCoordinatorStage.Reconciling);
        ThrowIfCancellationRequested(token);
        UpdateCoordinatorOutcome outcome;
        string message;
        if (operation.Pending is null)
        {
            outcome = UpdateCoordinatorOutcome.NoPendingUpdate;
            message = "No pending update.";
        }
        else
        {
            if (!TryCompare(currentVersion, operation.Pending.TargetVersion, out var comparison) ||
                !TryCompare(operation.Pending.OriginalVersion, operation.Pending.TargetVersion, out var originalComparison) ||
                originalComparison <= 0)
                return InvalidVersion(operation, notifyTerminal);
            if (comparison <= 0)
            {
                await _store.ClearAsync(token).ConfigureAwait(false);
                operation.Pending = null;
                outcome = UpdateCoordinatorOutcome.Updated;
                message = "The host-reported installed version matches or exceeds the pending target.";
            }
            else
            {
                outcome = operation.Pending.Phase == PendingUpdatePhase.Retryable
                    ? UpdateCoordinatorOutcome.RecoveryRequired : UpdateCoordinatorOutcome.AwaitingInstallation;
                message = "The target is not installed. Wait, explicitly retry after checking Android, or abandon tracking.";
            }
        }
        return notifyTerminal ? Finish(operation, outcome, message) : Result(operation, outcome, message);
    }

    private bool TryCompare(string current, string target, out int comparison)
    {
        comparison = 0;
        return !string.IsNullOrWhiteSpace(current) && !string.IsNullOrWhiteSpace(target) &&
            _versions.TryCompare(current, target, out comparison, out _);
    }

    private void ThrowIfCancellationRequested(CancellationToken token)
    {
        // Async shutdown marks this token immediately, before linked-token callbacks finish.
        _shutdown.Token.ThrowIfCancellationRequested();
        token.ThrowIfCancellationRequested();
    }

    private async Task<bool> TrySaveStatusAsync(Operation operation, PendingUpdateAttempt attempt)
    {
        try
        {
            await _store.WriteAsync(attempt, CancellationToken.None).ConfigureAwait(false);
            operation.Pending = attempt;
            return true;
        }
        catch { return false; }
    }

    private UpdateCoordinatorResult InvalidVersion(Operation operation, bool notifyTerminal = true) =>
        notifyTerminal
            ? Finish(operation, UpdateCoordinatorOutcome.Failed, "Invalid installed or pending target version.", UpdateFailureReason.VersionComparisonFailed)
            : Result(operation, UpdateCoordinatorOutcome.Failed, "Invalid installed or pending target version.", UpdateFailureReason.VersionComparisonFailed);

    private UpdateCoordinatorResult BootstrapFailure(Operation operation, UpdateOperationResult result)
    {
        var canceled = result.State == UpdateState.Canceled || result.FailureReason == UpdateFailureReason.Canceled;
        return Finish(operation,
            canceled ? UpdateCoordinatorOutcome.Canceled : UpdateCoordinatorOutcome.Failed,
            "Update operation did not complete. Consult the failure reason; reconcile any retained pending attempt before retrying.",
            canceled ? UpdateFailureReason.Canceled
                : result.FailureReason == UpdateFailureReason.None ? UpdateFailureReason.Unknown : result.FailureReason);
    }

    private async Task<UpdateCoordinatorResult> ExecuteAsync(
        Func<Operation, CancellationToken, Task<UpdateCoordinatorResult>> action, CancellationToken cancellationToken)
    {
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _operations++;
        }
        var operation = new Operation();
        var entered = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            try
            {
                await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
                entered = true;
                ThrowIfCancellationRequested(linked.Token);
                await using var storeLease = _store is IPendingUpdateStoreLeaseProvider leaseProvider
                    ? await leaseProvider.AcquireLeaseAsync(linked.Token).ConfigureAwait(false)
                    : null;
                ThrowIfCancellationRequested(linked.Token);
                return await action(operation, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested || _shutdown.IsCancellationRequested)
            {
                return Finish(operation, UpdateCoordinatorOutcome.Canceled,
                    "Operation canceled. Any pending intent remains tracked; reconcile before explicitly retrying.",
                    UpdateFailureReason.Canceled);
            }
            catch (Exception ex)
            {
                return Finish(operation, UpdateCoordinatorOutcome.Failed,
                    "Update operation failed. No pending tracking was intentionally discarded.",
                    ex is InvalidDataException ? UpdateFailureReason.InvalidMetadata
                        : ex is IOException or UnauthorizedAccessException ? UpdateFailureReason.FileIoError
                        : UpdateFailureReason.Unknown);
            }
        }
        finally
        {
            if (entered) _gate.Release();
            lock (_lifetime) _operations--;
            TryRelease();
        }
    }

    private static UpdateCoordinatorResult Result(Operation operation, UpdateCoordinatorOutcome outcome,
        string? message = null, UpdateFailureReason failure = UpdateFailureReason.None) => new()
        {
            OperationId = operation.Id,
            Stage = operation.Stage,
            Outcome = outcome,
            FailureReason = failure,
            Message = message,
            PendingUpdate = operation.Pending
        };

    private UpdateCoordinatorResult Finish(Operation operation, UpdateCoordinatorOutcome outcome, string message,
        UpdateFailureReason failure = UpdateFailureReason.None)
    {
        operation.Stage = UpdateCoordinatorStage.Finished;
        var result = Result(operation, outcome, message, failure);
        Dispatch(result);
        return result;
    }

    private void Notify(Operation operation, UpdateCoordinatorStage stage)
    {
        operation.Stage = stage;
        Dispatch(Result(operation, UpdateCoordinatorOutcome.None));
    }

    private void Dispatch(UpdateCoordinatorResult result)
    {
        Dispatch(StateChanged, new UpdateCoordinatorEventArgs(result));
    }

    private void Dispatch<TEventArgs>(EventHandler<TEventArgs>? listeners, TEventArgs args) where TEventArgs : EventArgs
    {
        if (listeners is null) return;
        try
        {
            _dispatcher.Dispatch(() =>
            {
                foreach (EventHandler<TEventArgs> listener in listeners.GetInvocationList())
                {
                    try { listener(this, args); }
                    catch { }
                }
            });
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _ = CancelAndDrainAsync();
    }

    private async Task CancelAndDrainAsync()
    {
        try { await _shutdown.CancelAsync().ConfigureAwait(false); }
        catch (Exception) { }
        finally
        {
            lock (_lifetime) _shutdownCanceled = true;
            TryRelease();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _drained.Task.ConfigureAwait(false);
    }

    private void TryRelease()
    {
        lock (_lifetime)
        {
            if (!_disposed || !_shutdownCanceled || _operations != 0 || _releasing) return;
            _releasing = true;
        }
        _ = ReleaseAsync();
    }

    private async Task ReleaseAsync()
    {
        Exception? failure = null;
        try
        {
            _bootstrap.AddListenerDownloadProgressChanged -= ForwardDownloadProgress;
            if (_ownsBootstrap)
            {
                if (_bootstrap is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else
                    _bootstrap.Dispose();
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            _gate.Dispose();
            _shutdown.Dispose();
        }
        if (failure is null) _drained.TrySetResult();
        else _drained.TrySetException(failure);
    }

    private sealed class Operation
    {
        public Guid Id { get; } = Guid.NewGuid();
        public UpdateCoordinatorStage Stage { get; set; }
        public PendingUpdateAttempt? Pending { get; set; }
    }
}
