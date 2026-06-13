using System.Net;
using GeneralUpdate.Avalonia.Android.Abstractions;

namespace GeneralUpdate.Avalonia.Android.Models;

/// <summary>
/// Configures HTTP transport behavior for update downloads:
/// SSL/TLS certificate validation, timeouts, proxy, retry, and authentication.
/// <para>
/// When provided to <see cref="GeneralUpdateBootstrap.CreateDefault"/>,
/// the library constructs an internal <see cref="HttpClient"/> from these settings.
/// When null, the existing behavior is preserved (bare HttpClient, no auth, system SSL).
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
    /// Timeout for individual HTTP requests (HEAD probes, etc.).
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
    /// Maximum number of retry attempts for transient failures.
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
    /// Global authentication provider applied to all download requests.
    /// Per-package authentication on <see cref="UpdatePackageInfo"/> takes precedence.
    /// </summary>
    public IHttpAuthProvider? AuthProvider { get; init; }

    /// <summary>
    /// Builds an <see cref="HttpClientHandler"/> from the configured options.
    /// Applies SSL validation policy and proxy settings.
    /// </summary>
    internal HttpClientHandler BuildHandler()
    {
        var handler = new HttpClientHandler();

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
