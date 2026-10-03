using GeneralUpdate.Avalonia.Android.Events;
using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Abstractions;

/// <summary>
/// Serializes update operations. Cancel and await active work before disposing for deterministic shutdown.
/// Dispose rejects new/queued operations and defers resource cleanup until active work exits.
/// Cancellation while waiting for the operation lock throws; cancellation within an operation returns Canceled.
/// </summary>
public interface IAndroidBootstrap : IDisposable
{
    event EventHandler<ValidateEventArgs>? AddListenerValidate;
    event EventHandler<DownloadProgressChangedEventArgs>? AddListenerDownloadProgressChanged;
    /// <summary>Legacy phase completion: ReadyToInstall or Installing, not proof of installation.</summary>
    event EventHandler<UpdateCompletedEventArgs>? AddListenerUpdateCompleted;
    /// <summary>Raised when CheckInstallationAsync first durably confirms the target version is installed.</summary>
    event EventHandler<InstallationConfirmedEventArgs>? AddListenerInstallationConfirmed;
    event EventHandler<UpdateFailedEventArgs>? AddListenerUpdateFailed;

    UpdateStateSnapshot GetSnapshot();

    /// <summary>
    /// Registers a pre-check callback that is invoked after version validation reports an update
    /// and before the update package is downloaded.
    /// <para>
    /// Mirrors <c>GeneralUpdate.Core</c>'s <c>AddListenerUpdatePrecheck</c>: the callback receives
    /// the update information (<see cref="UpdateInfoEventArgs"/>) and returns <c>true</c> to skip the
    /// update or <c>false</c> to continue. When skipped, <see cref="ValidateAsync"/> returns
    /// <see cref="UpdateCheckResult.UpdateFound"/> set to <c>false</c> with state <see cref="UpdateState.Completed"/>,
    /// so callers naturally stop before downloading.
    /// </para>
    /// <para>
    /// Like <c>GeneralUpdate.Core</c>, the callback is ignored for forced updates
    /// (<see cref="UpdatePackageInfo.IsForced"/>).
    /// </para>
    /// </summary>
    /// <param name="func">The pre-check callback. Must not be null.</param>
    /// <returns>This instance, so registrations can be chained.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="func"/> is null.</exception>
    IAndroidBootstrap AddListenerUpdatePrecheck(Func<UpdateInfoEventArgs, bool> func);

    /// <summary>
    /// Queries the configured update server for the newest package, compares it with
    /// <paramref name="currentVersion"/> and runs the pre-check callback.
    /// <para>
    /// Package metadata is discovered internally from <see cref="AndroidUpdateOptions.UpdateServer"/>, so callers
    /// only supply the version currently installed on the device. When an update is found, the returned
    /// <see cref="UpdateOperationResult.PackageInfo"/> can be passed to <see cref="DownloadAndVerifyAsync"/> and
    /// <see cref="LaunchInstallerAsync"/>.
    /// </para>
    /// <para>
    /// Like <c>GeneralUpdate.Core</c>, the pre-check callback registered with
    /// <see cref="AddListenerUpdatePrecheck"/> decides whether to continue: returning <c>true</c> skips the update
    /// (the result carries <see cref="UpdateCheckResult.UpdateFound"/> set to <c>false</c> and state
    /// <see cref="UpdateState.Completed"/>), returning <c>false</c> keeps it. Forced updates bypass the callback.
    /// </para>
    /// <para>
    /// Server, protocol, metadata and HTTP failures are reported through
    /// <see cref="UpdateOperationResult.Success"/>, <see cref="UpdateOperationResult.FailureReason"/> and
    /// <see cref="AddListenerUpdateFailed"/>; when the server reports no package the call succeeds with
    /// <see cref="UpdateCheckResult.UpdateFound"/> set to <c>false</c>.
    /// </para>
    /// </summary>
    /// <param name="currentVersion">The application version currently installed on the device.</param>
    /// <param name="cancellationToken">Cancels the server request and the validation.</param>
    /// <returns>The validation outcome, including the discovered package metadata when an update is available.</returns>
    Task<UpdateCheckResult> ValidateAsync(
        string currentVersion,
        CancellationToken cancellationToken = default);

    Task<UpdateOperationResult> DownloadAndVerifyAsync(
        UpdatePackageInfo packageInfo,
        CancellationToken cancellationToken = default);

    Task<InstallResult> LaunchInstallerAsync(
        UpdatePackageInfo packageInfo,
        string apkFilePath,
        CancellationToken cancellationToken = default);

    /// <summary>Checks and prepares a verified APK under one operation lock. Never opens an installer or permission UI.</summary>
    Task<UpdatePreparationResult> PrepareUpdateAsync(string currentVersion, CancellationToken cancellationToken = default);

    /// <summary>Explicitly forgets the installation record, including corrupt data. Does not modify the installed application.</summary>
    Task<UpdateOperationResult> ResetInstallationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles the durable installation target with the actual installed version, without a server request.
    /// Call at startup and after returning from the installer. The target or a newer version confirms installation;
    /// an older version remains pending (not proof of cancellation). No record returns a successful None state.
    /// Storage failures are reported through AddListenerUpdateFailed; a pending record is never silently discarded.
    /// Use one bootstrap instance per journal. The host must supply the version read from Android PackageManager.
    /// </summary>
    Task<InstallationCheckResult> CheckInstallationAsync(
        string currentVersion,
        CancellationToken cancellationToken = default);
}
