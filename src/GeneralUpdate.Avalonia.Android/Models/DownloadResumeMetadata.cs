namespace GeneralUpdate.Avalonia.Android.Models;

public sealed record DownloadResumeMetadata
{
    public required string DownloadUrl { get; init; }
    public required string ExpectedSha256 { get; init; }
    public long ExpectedFileSize { get; init; }
    public required string FileName { get; init; }
    public string? ETag { get; init; }
    public string? LastModified { get; init; }
}
