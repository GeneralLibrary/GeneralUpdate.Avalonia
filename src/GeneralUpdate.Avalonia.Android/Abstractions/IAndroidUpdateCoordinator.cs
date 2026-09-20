using GeneralUpdate.Avalonia.Android.Events;
using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Abstractions;

/// <summary>
/// Serializes complete update attempts. Stores without IPendingUpdateStoreLeaseProvider require
/// one coordinator per store. Do not interleave operations with direct bootstrap calls or store
/// writes. Construction never launches an installer.
/// Callbacks must not synchronously wait for another operation or asynchronous disposal.
/// </summary>
public interface IAndroidUpdateCoordinator : IDisposable, IAsyncDisposable
{
    event EventHandler<UpdateCoordinatorEventArgs>? StateChanged;
    event EventHandler<DownloadProgressChangedEventArgs>? AddListenerDownloadProgressChanged;

    /// <summary>
    /// Configures the bootstrap pre-check before starting an operation. Returning true skips an
    /// optional update; forced updates bypass this callback. Policy exceptions fail the check.
    /// </summary>
    IAndroidUpdateCoordinator AddListenerUpdatePrecheck(Func<UpdateInfoEventArgs, bool> func);

    Task<UpdateCoordinatorResult> RunAsync(string currentVersion, CancellationToken cancellationToken = default);
    Task<UpdateCoordinatorResult> ReconcileAsync(string currentVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles first, then discovers and verifies the same pending target again. If fresh
    /// discovery selects a different version, returns RecoveryRequired without replacing the
    /// pending intent: an earlier installer may still finish. Explicitly reconcile or abandon
    /// tracking before starting that different target with RunAsync.
    /// </summary>
    Task<UpdateCoordinatorResult> RetryAsync(string currentVersion, CancellationToken cancellationToken = default);

    /// <summary>Forgets tracking only; does not cancel Android installation, roll back, or delete APKs.</summary>
    Task<UpdateCoordinatorResult> AbandonAsync(CancellationToken cancellationToken = default);
}
