namespace GeneralUpdate.Avalonia.Android.Models;

/// <summary>
/// The last installation attempt. Contains no download credentials and survives app replacement.
/// A pending record does not distinguish a canceled installer from an installation still in progress.
/// </summary>
public sealed record InstallationRecord
{
    public required string TargetVersion { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public string? InstalledVersion { get; init; }
    public DateTimeOffset? ConfirmedAt { get; init; }
}
