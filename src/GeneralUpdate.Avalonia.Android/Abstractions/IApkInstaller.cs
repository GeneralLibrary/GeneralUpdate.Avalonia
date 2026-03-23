using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Abstractions;

public interface IApkInstaller
{
    Task<InstallResult> LaunchInstallAsync(
        UpdatePackageInfo packageInfo,
        string apkFilePath,
        CancellationToken cancellationToken = default);
}
