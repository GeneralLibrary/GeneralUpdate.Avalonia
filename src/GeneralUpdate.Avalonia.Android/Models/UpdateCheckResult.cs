namespace GeneralUpdate.Avalonia.Android.Models;

public sealed record UpdateCheckResult : UpdateOperationResult
{
    public bool UpdateFound { get; init; }
    public string? CurrentVersion { get; init; }
    public string? TargetVersion => PackageInfo?.Version;
}
