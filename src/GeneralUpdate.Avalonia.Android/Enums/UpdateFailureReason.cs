namespace GeneralUpdate.Avalonia.Android;

public enum UpdateFailureReason
{
    None = 0,
    NetworkError = 1,
    Canceled = 2,
    InvalidMetadata = 3,
    FileIoError = 4,
    HashMismatch = 5,
    ServerDoesNotSupportRange = 6,
    InstallPermissionDenied = 7,
    InstallLaunchFailed = 8,
    VersionComparisonFailed = 9,
    Unknown = 10
}
