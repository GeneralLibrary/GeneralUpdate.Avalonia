namespace GeneralUpdate.Avalonia.Android.Enums;

/// <summary>
/// Defines the supported HTTP authentication schemes for update downloads.
/// </summary>
public enum AuthScheme
{
    /// <summary>
    /// HMAC-SHA256 signature-based authentication.
    /// Adds X-Update-Timestamp and X-Update-Signature headers.
    /// </summary>
    Hmac = 0,

    /// <summary>
    /// Bearer token authentication via Authorization header.
    /// </summary>
    Bearer = 1,

    /// <summary>
    /// API key authentication via a custom header (default: X-Api-Key).
    /// </summary>
    ApiKey = 2,

    /// <summary>
    /// HTTP Basic authentication via Authorization header.
    /// </summary>
    Basic = 3
}
