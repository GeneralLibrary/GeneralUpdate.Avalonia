using System.Net;
using GeneralUpdate.Avalonia.Android.Abstractions;

namespace GeneralUpdate.Avalonia.Android.Models;

/// <summary>
/// Configures HTTP transport behavior for update verification and downloads:
/// SSL/TLS certificate validation, timeouts, proxy, retry, and authentication.
/// <para>
/// When provided to <see cref="GeneralUpdateBootstrap.CreateDefault"/>,
/// the library constructs an internal <see cref="HttpClient"/> from these settings.
/// Internally created clients do not follow redirects; configure direct verification and package URLs.
/// Host-supplied clients remain host-owned, including responsibility for redirect and default-header safety.
/// </para>
/// </summary>
public sealed record HttpDownloadOptions
{
    /// <summary>
    /// Custom SSL/TLS certificate validation policy.
    /// Defaults to null, which uses the system's default certificate validation.
    /// Set to <see cref="Services.AllowAllSslValidationPolicy"/> for self-signed certificates
    /// in development environments only.
    /// </summary>
    public ISslValidationPolicy? SslValidationPolicy { get; init; }

    /// <summary>
    /// Timeout for update server verification requests and download HEAD probes.
    /// Default is 30 seconds.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Overall timeout for the entire download operation.
    /// Default is 10 minutes.
    /// </summary>
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Optional web proxy for HTTP requests.
    /// When set, <see cref="UseProxy"/> must also be true for the proxy to take effect.
    /// </summary>
    public IWebProxy? Proxy { get; init; }

    /// <summary>
    /// Whether to use the configured <see cref="Proxy"/>.
    /// Default is false.
    /// </summary>
    public bool UseProxy { get; init; }

    /// <summary>
    /// Maximum total attempts for transient download failures, shared across the HEAD probe,
    /// GET request and body transfer; retries restart the download attempt.
    /// Default is 3 (meaning 1 initial attempt + 2 retries).
    /// Set to 1 to disable retry.
    /// </summary>
    public int MaxRetryAttempts { get; init; } = 3;

    /// <summary>
    /// Base delay for exponential backoff retry.
    /// Actual delays are: baseDelay * 2^attempt.
    /// Default is 1 second.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Global authentication provider applied to update server verification requests.
    /// Downloads receive it only at the verification origin or an explicitly allowed download origin.
    /// Per-package authentication on <see cref="UpdatePackageInfo"/> takes precedence for downloads.
    /// </summary>
    public IHttpAuthProvider? AuthProvider { get; init; }

    /// <summary>
    /// Allows global and per-package credentials over plaintext HTTP. Unsafe: use only for
    /// explicitly trusted development endpoints. Defaults to false; authenticated requests
    /// require HTTPS and are rejected before invoking the authentication provider otherwise.
    /// This does not relax origin restrictions or enable redirects.
    /// </summary>
    public bool AllowInsecureAuthentication { get; init; }

    /// <summary>
    /// Optional trusted origin for global authentication, overriding the configured verification URL's origin.
    /// Origin matching uses scheme, host and effective port, not URL paths.
    /// Without either this value or a valid verification URL, downloads do not receive global credentials
    /// unless their origin is explicitly included in <see cref="AllowedDownloadAuthenticationOrigins"/>.
    /// </summary>
    public Uri? TrustedAuthenticationOrigin { get; init; }

    /// <summary>
    /// Additional HTTP(S) origins explicitly trusted to receive global download credentials, such as a CDN.
    /// Empty by default. Entries grant trust to the entire origin; paths are ignored.
    /// Redirects are not followed even between trusted origins.
    /// </summary>
    public IReadOnlyCollection<Uri> AllowedDownloadAuthenticationOrigins { get; init; } = Array.Empty<Uri>();

    internal bool IsDownloadAuthenticationAllowed(Uri? requestUri, string? verificationUrl)
    {
        var trustedOrigin = TrustedAuthenticationOrigin;
        if (trustedOrigin is null)
        {
            Uri.TryCreate(verificationUrl, UriKind.Absolute, out trustedOrigin);
        }

        return IsSameOrigin(requestUri, trustedOrigin) ||
            (AllowedDownloadAuthenticationOrigins?.Any(origin => IsSameOrigin(requestUri, origin)) ?? false);
    }

    internal static bool IsSameOrigin(Uri? destination, Uri? origin)
        => IsHttpOrigin(destination) && IsHttpOrigin(origin) &&
            string.Equals(destination!.Scheme, origin!.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(destination.IdnHost, origin.IdnHost, StringComparison.OrdinalIgnoreCase) &&
            destination.Port == origin.Port;

    internal static void EnsureAuthenticationTransport(Uri? requestUri, bool allowInsecureAuthentication = false)
    {
        if (!IsHttpOrigin(requestUri) ||
            (requestUri!.Scheme != Uri.UriSchemeHttps && !allowInsecureAuthentication))
        {
            throw new InvalidDataException(
                "Authentication requires an HTTP(S) URI without userinfo, and HTTPS unless AllowInsecureAuthentication is explicitly enabled.");
        }
    }

    private static bool IsHttpOrigin(Uri? uri)
        => uri is { IsAbsoluteUri: true } &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
            string.IsNullOrEmpty(uri.UserInfo);

    /// <summary>
    /// Builds an <see cref="HttpClientHandler"/> from the configured options.
    /// Applies SSL validation policy and proxy settings. Automatic redirects are disabled
    /// so custom authentication headers cannot be forwarded to a different endpoint.
    /// </summary>
    internal HttpClientHandler BuildHandler()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };

        if (SslValidationPolicy != null)
        {
            handler.ServerCertificateCustomValidationCallback =
                (_, cert, chain, errors) => SslValidationPolicy.ValidateCertificate(cert, chain, errors);
        }

        if (UseProxy && Proxy != null)
        {
            handler.Proxy = Proxy;
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = false;
        }

        return handler;
    }
}
