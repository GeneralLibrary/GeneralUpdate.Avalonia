namespace GeneralUpdate.Avalonia.Android.Models;

/// <summary>
/// Configures the server queried internally by version validation.
/// </summary>
public sealed record UpdateServerOptions
{
    public required string RequestUrl { get; init; }
    public string AppKey { get; init; } = string.Empty;
    public int AppType { get; init; } = 1;
    public int Platform { get; init; }
    public string ProductId { get; init; } = string.Empty;

    /// <summary>
    /// GET a JSON UpdatePackageInfo instead of POSTing the GeneralUpdate verification protocol.
    /// </summary>
    public bool UseJsonEndpoint { get; init; }
}
