using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Abstractions;

public interface IUpdateDownloader
{
    Task<DownloadResult> DownloadAsync(
        UpdatePackageInfo packageInfo,
        Action<DownloadProgressInfo>? progressCallback,
        CancellationToken cancellationToken = default);
}
