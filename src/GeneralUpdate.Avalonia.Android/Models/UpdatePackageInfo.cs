using GeneralUpdate.Avalonia.Android.Enums;

namespace GeneralUpdate.Avalonia.Android.Models;

public sealed record UpdatePackageInfo
{
    public required string Version { get; init; }
    public string? VersionName { get; init; }
    public string? Description { get; init; }
    public required string DownloadUrl { get; init; }
    public long FileSize { get; init; }
    public required string Sha256 { get; init; }
    public DateTimeOffset? PublishTime { get; init; }
    public bool IsForced { get; init; }
    public string? FileName { get; init; }

    /// <summary>
    /// Per-package authentication scheme.
    /// When set, takes precedence over the global <see cref="HttpDownloadOptions.AuthProvider"/>.
    /// </summary>
    public Enums.AuthScheme? AuthScheme { get; init; }

    /// <summary>
    /// Token value used by Bearer or ApiKey authentication.
    /// For Bearer: the Bearer token string.
    /// For ApiKey: the API key value.
    /// </summary>
    public string? AuthToken { get; init; }

    /// <summary>
    /// Secret key used by HMAC-SHA256 signature authentication.
    /// </summary>
    public string? AuthSecretKey { get; init; }

    /// <summary>
    /// Username used by Basic authentication.
    /// </summary>
    public string? BasicUsername { get; init; }

    /// <summary>
    /// Password used by Basic authentication.
    /// </summary>
    public string? BasicPassword { get; init; }
}
