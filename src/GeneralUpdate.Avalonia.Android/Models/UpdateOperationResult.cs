namespace GeneralUpdate.Avalonia.Android.Models;

public record UpdateOperationResult
{
    public bool Success { get; init; }
    public UpdateState State { get; init; } = UpdateState.None;
    public UpdateFailureReason FailureReason { get; init; } = UpdateFailureReason.None;
    public string? Message { get; init; }
    public UpdatePackageInfo? PackageInfo { get; init; }
    public string? FilePath { get; init; }
    public Exception? Exception { get; init; }
}
