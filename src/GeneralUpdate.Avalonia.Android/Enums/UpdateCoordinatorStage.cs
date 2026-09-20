namespace GeneralUpdate.Avalonia.Android.Enums;

public enum UpdateCoordinatorStage
{
    ReadingPending,
    Checking,
    DownloadingAndVerifying,
    PersistingIntent,
    LaunchingInstaller,
    Reconciling,
    Abandoning,
    Finished
}
