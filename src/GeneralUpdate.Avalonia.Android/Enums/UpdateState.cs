namespace GeneralUpdate.Avalonia.Android;

public enum UpdateState
{
    None = 0,
    Checking = 1,
    UpdateAvailable = 2,
    Downloading = 3,
    Verifying = 4,
    ReadyToInstall = 5,
    Installing = 6,
    Completed = 7,
    Failed = 8,
    Canceled = 9
}
