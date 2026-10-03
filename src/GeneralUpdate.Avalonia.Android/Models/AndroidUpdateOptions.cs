namespace GeneralUpdate.Avalonia.Android.Models;

public sealed record AndroidUpdateOptions
{
    /// <summary>
    /// Update server queried by <see cref="Abstractions.IAndroidBootstrap.ValidateAsync"/>.
    /// When null and no custom IUpdatePackageSource is provided, validation reports
    /// <see cref="UpdateFailureReason.InvalidMetadata"/> because no package can be discovered.
    /// </summary>
    public UpdateServerOptions? UpdateServer { get; init; }

    public string DownloadDirectoryPath { get; init; } = string.Empty;
    /// <summary>
    /// Durable installation journal. CreateDefault uses FilesDir/update/installation.json when empty.
    /// Do not place it in the download cache, which Android may clear during an update.
    /// </summary>
    public string InstallationStateFilePath { get; init; } = string.Empty;
    public string TemporaryFileExtension { get; init; } = ".part";
    public string SidecarExtension { get; init; } = ".json";
    public string FileProviderAuthority { get; init; } = string.Empty;
    public int DownloadBufferSize { get; init; } = 64 * 1024;
    public int SpeedSmoothingWindowSeconds { get; init; } = 4;
}
