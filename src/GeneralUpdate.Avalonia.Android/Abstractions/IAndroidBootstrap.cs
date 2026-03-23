using GeneralUpdate.Avalonia.Android.Events;
using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Abstractions;

public interface IAndroidBootstrap
{
    event EventHandler<ValidateEventArgs>? AddListenerValidate;
    event EventHandler<DownloadProgressChangedEventArgs>? AddListenerDownloadProgressChanged;
    event EventHandler<UpdateCompletedEventArgs>? AddListenerUpdateCompleted;
    event EventHandler<UpdateFailedEventArgs>? AddListenerUpdateFailed;

    UpdateStateSnapshot GetSnapshot();

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
