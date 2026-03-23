using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Events;

public sealed class UpdateFoundEventArgs : EventArgs
{
    public UpdateFoundEventArgs(UpdatePackageInfo packageInfo, string currentVersion)
    {
        PackageInfo = packageInfo;
        CurrentVersion = currentVersion;
    }

    public UpdatePackageInfo PackageInfo { get; }
    public string CurrentVersion { get; }
}
