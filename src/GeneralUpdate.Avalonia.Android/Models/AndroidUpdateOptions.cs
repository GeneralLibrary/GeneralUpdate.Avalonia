namespace GeneralUpdate.Avalonia.Android.Models;

public sealed record AndroidUpdateOptions
{
    public string DownloadDirectoryPath { get; init; } = string.Empty;
    public string TemporaryFileExtension { get; init; } = ".part";
    public string SidecarExtension { get; init; } = ".json";
    public string FileProviderAuthority { get; init; } = string.Empty;
    public int DownloadBufferSize { get; init; } = 64 * 1024;
    public int SpeedSmoothingWindowSeconds { get; init; } = 4;
}
