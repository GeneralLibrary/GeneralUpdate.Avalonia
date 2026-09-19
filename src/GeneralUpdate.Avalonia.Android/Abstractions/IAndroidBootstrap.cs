using GeneralUpdate.Avalonia.Android.Events;
using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Abstractions;

public interface IAndroidBootstrap : IDisposable
{
    event EventHandler<ValidateEventArgs>? AddListenerValidate;
    event EventHandler<DownloadProgressChangedEventArgs>? AddListenerDownloadProgressChanged;
    event EventHandler<UpdateCompletedEventArgs>? AddListenerUpdateCompleted;
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
    /// Queries the configured update server, compares versions and invokes the pre-check callback.
    /// Returning true from pre-check skips the update; false continues. Forced updates bypass pre-check.
    /// The returned PackageInfo can be passed to download and installation.
    /// </summary>
    Task<UpdateCheckResult> ValidateAsync(
        string currentVersion,
        CancellationToken cancellationToken = default);

    Task<UpdateCheckResult> ValidateAsync(
        UpdatePackageInfo packageInfo,
        string currentVersion,
        CancellationToken cancellationToken = default);

    Task<UpdateOperationResult> DownloadAndVerifyAsync(
        UpdatePackageInfo packageInfo,
        CancellationToken cancellationToken = default);

    Task<InstallResult> LaunchInstallerAsync(
        UpdatePackageInfo packageInfo,
        string apkFilePath,
        CancellationToken cancellationToken = default);
}
