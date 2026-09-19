namespace GeneralUpdate.Avalonia.Android.Models;

/// <summary>
/// Configures the update server that <see cref="Abstractions.IAndroidBootstrap.ValidateAsync"/> queries
/// internally, so callers only pass the version currently installed.
/// <para>
/// By default the client POSTs the GeneralUpdate verification request
/// (<c>version</c>, <c>appKey</c>, <c>appType</c>, <c>platform</c>, <c>productId</c>) to
/// <see cref="RequestUrl"/> and picks the newest non-frozen full APK from the
/// <c>{"code":200,"body":[...]}</c> response.
/// </para>
/// <para>
/// Set <see cref="UseJsonEndpoint"/> to <c>true</c> when <see cref="RequestUrl"/> instead returns a
/// single <see cref="UpdatePackageInfo"/> JSON document over GET.
/// </para>
/// </summary>
public sealed record UpdateServerOptions
{
    /// <summary>
    /// Absolute HTTP(S) URL queried by <see cref="Abstractions.IAndroidBootstrap.ValidateAsync"/>.
    /// For the GeneralUpdate reference protocol this is the verification endpoint
    /// (for example <c>https://example.com/Upgrade/Verification</c>).
    /// </summary>
    public required string RequestUrl { get; init; }

    /// <summary>
    /// Application key sent in the verification request body.
    /// This is a request field only; it does not enable HMAC signing
    /// (configure <see cref="HttpDownloadOptions.AuthProvider"/> for that).
    /// </summary>
    public string AppKey { get; init; } = string.Empty;

    /// <summary>
    /// Application type sent in the verification request body. Default is 1.
    /// </summary>
    public int AppType { get; init; } = 1;

    /// <summary>
    /// Platform identifier expected by the server for Android.
    /// Must match the identifier configured by the deployment; no fixed value is assumed.
    /// </summary>
    public int Platform { get; init; }

    /// <summary>
    /// Product identifier sent in the verification request body.
    /// </summary>
    public string ProductId { get; init; } = string.Empty;

    /// <summary>
    /// GET a JSON <see cref="UpdatePackageInfo"/> from <see cref="RequestUrl"/> instead of POSTing
    /// the GeneralUpdate verification protocol. HTTP 204 or a JSON <c>null</c> body means no update.
    /// </summary>
    public bool UseJsonEndpoint { get; init; }
}
