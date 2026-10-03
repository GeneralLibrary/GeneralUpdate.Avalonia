namespace GeneralUpdate.Avalonia.Android.Models;

public sealed record UpdatePreparationResult : UpdateOperationResult
{
    public bool UpdateFound { get; init; }
    public bool IsReadyToInstall => Success && State == UpdateState.ReadyToInstall &&
        PackageInfo is not null && !string.IsNullOrWhiteSpace(FilePath);

    internal static UpdatePreparationResult From(UpdateOperationResult result, bool updateFound) => new()
    {
        Success = result.Success,
        State = result.State,
        FailureReason = result.FailureReason,
        Message = result.Message,
        PackageInfo = result.PackageInfo,
        FilePath = result.FilePath,
        Exception = result.Exception,
        UpdateFound = updateFound
    };
}
