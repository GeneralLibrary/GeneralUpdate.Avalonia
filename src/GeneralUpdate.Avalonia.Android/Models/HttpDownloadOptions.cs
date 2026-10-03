using System.Net;
using GeneralUpdate.Avalonia.Android.Abstractions;

namespace GeneralUpdate.Avalonia.Android.Models;

/// <summary>
/// Configures HTTP transport behavior for update verification and downloads:
/// SSL/TLS certificate validation, timeouts, proxy, retry, and authentication.
/// <para>
/// CreateDefault applies the default request policies even when these options are omitted.
/// A supplied HttpClient is borrowed and never replaced or reconfigured.
/// Configure TLS/proxy on its handler; combining those handler options with a supplied client is rejected.
/// Without a supplied client the library creates and owns one shared by queries and downloads.
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
    /// Overall timeout for the entire download operation, including retries and backoff.
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
    /// Maximum total download attempts for transient HEAD, GET and response-stream failures.
    /// Metadata queries are not retried. Caller cancellation and permanent HTTP errors are not retried.
    /// Default is 3 (meaning 1 initial attempt + 2 retries).
    /// Set to 1 to disable retry.
    /// </summary>
    public int MaxRetryAttempts { get; init; } = 3;

    /// <summary>
    /// Base delay for exponential backoff retry.
    /// Actual delays are: min(30 seconds, baseDelay * 2^attempt).
    /// Default is 1 second.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Global authentication provider applied to update server verification and downloads on the same HTTPS origin.
    /// It is never sent to a different download origin or to an HTTP download. Per-package authentication
    /// on <see cref="UpdatePackageInfo"/> takes precedence for downloads.
    /// </summary>
    public IHttpAuthProvider? AuthProvider { get; init; }

    internal void Validate()
    {
        if (RequestTimeout != Timeout.InfiniteTimeSpan && RequestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        if (DownloadTimeout != Timeout.InfiniteTimeSpan && DownloadTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(DownloadTimeout));
        if (MaxRetryAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxRetryAttempts));
        if (RetryBaseDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RetryBaseDelay));
    }

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
