using Android.Content;
using Android.OS;
using AndroidX.Core.Content;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Services;

public sealed class AndroidApkInstaller : IApkInstaller
{
    private readonly IAndroidContextProvider _contextProvider;
    private readonly IAndroidActivityProvider _activityProvider;
    private readonly AndroidUpdateOptions _options;
    private readonly IUpdateLogger _logger;

    public AndroidApkInstaller(
        IAndroidContextProvider contextProvider,
        IAndroidActivityProvider activityProvider,
        AndroidUpdateOptions options,
        IUpdateLogger? logger = null)
    {
        _contextProvider = contextProvider;
        _activityProvider = activityProvider;
        _options = options;
        _logger = logger ?? new NoOpUpdateLogger();
    }

    public Task<InstallResult> LaunchInstallAsync(UpdatePackageInfo packageInfo, string apkFilePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(_options.FileProviderAuthority))
        {
            return Task.FromResult(new InstallResult
            {
                Success = false,
                State = UpdateState.Failed,
                FailureReason = UpdateFailureReason.InvalidMetadata,
                Message = "FileProvider authority is not configured.",
                PackageInfo = packageInfo,
                FilePath = apkFilePath
            });
        }

        if (!File.Exists(apkFilePath))
        {
            return Task.FromResult(new InstallResult
            {
                Success = false,
                State = UpdateState.Failed,
                FailureReason = UpdateFailureReason.FileIoError,
                Message = "APK file not found.",
                PackageInfo = packageInfo,
                FilePath = apkFilePath
            });
        }

        var context = _contextProvider.GetContext();
        if (context is null)
        {
            return Task.FromResult(new InstallResult
            {
                Success = false,
                State = UpdateState.Failed,
                FailureReason = UpdateFailureReason.InstallLaunchFailed,
                Message = "Android context is unavailable.",
                PackageInfo = packageInfo,
                FilePath = apkFilePath
            });
        }

        var packageManager = context.PackageManager;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O && (packageManager is null || !packageManager.CanRequestPackageInstalls()))
        {
            return Task.FromResult(new InstallResult
            {
                Success = false,
                State = UpdateState.Failed,
                FailureReason = UpdateFailureReason.InstallPermissionDenied,
                Message = "App is not allowed to request package installs.",
                PackageInfo = packageInfo,
                FilePath = apkFilePath
            });
        }

        try
        {
            var javaFile = new Java.IO.File(apkFilePath);
            var apkUri = FileProvider.GetUriForFile(context, _options.FileProviderAuthority, javaFile);

            var installIntent = new Intent(Intent.ActionView)
                .SetDataAndType(apkUri, "application/vnd.android.package-archive")
                .AddFlags(ActivityFlags.GrantReadUriPermission);

            var activity = _activityProvider.GetCurrentActivity();
            if (activity is not null)
            {
                activity.StartActivity(installIntent);
            }
            else
            {
                installIntent.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(installIntent);
            }

            return Task.FromResult(new InstallResult
            {
                Success = true,
                State = UpdateState.Installing,
                FailureReason = UpdateFailureReason.None,
                Message = "Installer intent launched.",
                PackageInfo = packageInfo,
                FilePath = apkFilePath
            });
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to launch APK installer.", ex);
            return Task.FromResult(new InstallResult
            {
                Success = false,
                State = UpdateState.Failed,
                FailureReason = UpdateFailureReason.InstallLaunchFailed,
                Message = "Failed to launch installer intent.",
                PackageInfo = packageInfo,
                FilePath = apkFilePath,
                Exception = ex
            });
        }
    }
}
