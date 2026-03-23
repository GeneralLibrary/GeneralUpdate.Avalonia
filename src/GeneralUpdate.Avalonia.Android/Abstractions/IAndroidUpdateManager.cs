using GeneralUpdate.Avalonia.Android.Events;
using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Abstractions;

public interface IAndroidUpdateManager
{
    event EventHandler<UpdateFoundEventArgs>? UpdateFound;
    event EventHandler<DownloadProgressChangedEventArgs>? DownloadProgressChanged;
    event EventHandler<UpdateCompletedEventArgs>? UpdateCompleted;
    event EventHandler<UpdateFailedEventArgs>? UpdateFailed;

    UpdateStateSnapshot GetSnapshot();

    Task<UpdateCheckResult> CheckForUpdateAsync(
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
