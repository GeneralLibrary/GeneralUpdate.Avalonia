namespace GeneralUpdate.Avalonia.Android.Models;

public sealed record DownloadProgressInfo
{
    public required double DownloadSpeedBytesPerSecond { get; init; }
    public required long DownloadedBytes { get; init; }
    public required long RemainingBytes { get; init; }
    public required long TotalBytes { get; init; }
    public required double ProgressPercentage { get; init; }
    public required UpdatePackageInfo PackageInfo { get; init; }
    public required string StatusDescription { get; init; }
}
