namespace GeneralUpdate.Avalonia.Android.Enums;

public enum UpdateCoordinatorOutcome
{
    None,
    NoUpdate,
    Skipped,
    InstallerLaunched,
    Updated,
    NoPendingUpdate,
    PendingUpdateExists,
    AwaitingInstallation,
    RecoveryRequired,
    Abandoned,
    Canceled,
    Failed
}
