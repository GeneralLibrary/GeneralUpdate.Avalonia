namespace GeneralUpdate.Avalonia.Android.Models;

public sealed record UpdatePackageInfo
{
    public required string Version { get; init; }
    public string? VersionName { get; init; }
    public string? Description { get; init; }
    public required string DownloadUrl { get; init; }
    public long FileSize { get; init; }
    public required string Sha256 { get; init; }
    public DateTimeOffset? PublishTime { get; init; }
    public bool IsForced { get; init; }
    public string? FileName { get; init; }
}
