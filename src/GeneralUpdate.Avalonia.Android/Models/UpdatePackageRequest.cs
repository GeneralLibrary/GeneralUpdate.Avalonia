namespace GeneralUpdate.Avalonia.Android.Models;

/// <summary>
/// Request body for the GeneralUpdate /Upgrade/Verification reference protocol.
/// Platform must match the identifier configured by the server.
/// </summary>
public sealed record UpdatePackageRequest
{
    public required string Version { get; init; }
    public required string AppKey { get; init; }
    public int AppType { get; init; } = 1;
    public required int Platform { get; init; }
    public required string ProductId { get; init; }
}
