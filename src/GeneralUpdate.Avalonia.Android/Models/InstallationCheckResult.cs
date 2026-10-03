namespace GeneralUpdate.Avalonia.Android.Models;

public sealed record InstallationCheckResult : UpdateOperationResult
{
    public required string CurrentVersion { get; init; }
    public InstallationRecord? Record { get; init; }
    public bool IsInstalled => Success && State == UpdateState.Installed;
    public bool HasPendingInstallation => Success && State == UpdateState.InstallationPending;
}
