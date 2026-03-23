using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Events;

public sealed class DownloadProgressChangedEventArgs : EventArgs
{
    public DownloadProgressChangedEventArgs(DownloadProgressInfo progress)
    {
        DownloadSpeedBytesPerSecond = progress.DownloadSpeedBytesPerSecond;
        DownloadedBytes = progress.DownloadedBytes;
        RemainingBytes = progress.RemainingBytes;
        TotalBytes = progress.TotalBytes;
        ProgressPercentage = progress.ProgressPercentage;
        PackageInfo = progress.PackageInfo;
        StatusDescription = progress.StatusDescription;
    }

    public double DownloadSpeedBytesPerSecond { get; }
    public long DownloadedBytes { get; }
    public long RemainingBytes { get; }
    public long TotalBytes { get; }
    public double ProgressPercentage { get; }
    public UpdatePackageInfo PackageInfo { get; }
    public string StatusDescription { get; }
}
