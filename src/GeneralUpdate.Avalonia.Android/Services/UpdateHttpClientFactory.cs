using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Services;

internal static class UpdateHttpClientFactory
{
    public static HttpClient Create(HttpClient? client, HttpDownloadOptions? options, out bool ownsClient)
    {
        options?.Validate();
        if (client is not null)
        {
            if (options is { SslValidationPolicy: not null } or { Proxy: not null } or { UseProxy: true })
            {
                throw new ArgumentException(
                    "Configure TLS and proxy on the supplied HttpClient handler, or omit httpClient.");
            }
            ownsClient = false;
            return client;
        }

        ownsClient = true;
        return new HttpClient(options?.BuildHandler() ?? new HttpClientHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}
