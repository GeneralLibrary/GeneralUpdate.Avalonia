using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Enums;
using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Services;

/// <summary>
/// Retrieves package metadata without validating the installed version or starting a download.
/// The caller owns the supplied HttpClient, including its timeout and transport configuration.
/// </summary>
public sealed class HttpUpdatePackageClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<AuthScheme>() }
    };

    private readonly HttpClient _httpClient;
    private readonly IHttpAuthProvider? _authProvider;
    private readonly IVersionComparer _versionComparer;

    public HttpUpdatePackageClient(
        HttpClient httpClient,
        IHttpAuthProvider? authProvider = null,
        IVersionComparer? versionComparer = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _authProvider = authProvider;
        _versionComparer = versionComparer ?? new SystemVersionComparer();
    }

    /// <summary>
    /// GETs a JSON UpdatePackageInfo. HTTP 204 or JSON null means no package.
    /// HTTP, JSON and metadata errors are propagated; cancellation is not treated as no update.
    /// </summary>
    public async Task<UpdatePackageInfo?> GetPackageInfoAsync(
        string requestUrl, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GetHttpUri(requestUrl));
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        var package = await response.Content.ReadFromJsonAsync<UpdatePackageInfo>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return package is null ? null : ValidateMetadata(package);
    }

    /// <summary>
    /// POSTs the GeneralUpdate verification request and returns the newest non-frozen full APK.
    /// An empty eligible package list or HTTP 204 means no package. Other package formats are ignored.
    /// This reference protocol must be checked against the deployed GeneralSpacestation API.
    /// </summary>
    public async Task<UpdatePackageInfo?> GetPackageInfoAsync(
        string requestUrl, UpdatePackageRequest packageRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageRequest);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRequest.Version);
        using var request = new HttpRequestMessage(HttpMethod.Post, GetHttpUri(requestUrl))
        {
            Content = JsonContent.Create(packageRequest, options: JsonOptions)
        };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<VerificationResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        if (result?.Code != 200 || result.Body is null)
        {
            throw new InvalidDataException("The update server returned an unsuccessful or invalid verification response.");
        }

        UpdatePackageInfo? latest = null;
        foreach (var entry in result.Body)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null)
            {
                throw new InvalidDataException("The update server returned a null package entry.");
            }

            if (entry.IsFreeze == true || entry.PackageType is not (null or 0 or 2) || !IsApk(entry))
            {
                continue;
            }

            var package = ValidateMetadata(new UpdatePackageInfo
            {
                Version = entry.Version!,
                VersionName = entry.Name,
                Description = entry.UpdateLog,
                DownloadUrl = entry.Url!,
                Sha256 = entry.Hash!,
                FileSize = entry.Size ?? 0,
                PublishTime = entry.ReleaseDate,
                IsForced = entry.IsForcibly == true,
                AuthScheme = entry.AuthScheme,
                AuthToken = entry.AuthToken
            });

            if (latest is null)
            {
                latest = package;
            }
            else if (!_versionComparer.TryCompare(latest.Version, package.Version, out var compare, out _))
            {
                throw new InvalidDataException("The update server returned versions that cannot be compared.");
            }
            else if (compare > 0)
            {
                latest = package;
            }
        }

        return latest;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Accept.ParseAdd("application/json");
        if (_authProvider is not null)
        {
            await _authProvider.ApplyAuthAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static Uri GetHttpUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("An absolute HTTP or HTTPS URL is required.", nameof(url));
        }

        return uri;
    }

    private static UpdatePackageInfo ValidateMetadata(UpdatePackageInfo package)
    {
        if (string.IsNullOrWhiteSpace(package.Version) ||
            !Uri.TryCreate(package.DownloadUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            package.FileSize < 0 || package.Sha256 is not { Length: 64 } ||
            !package.Sha256.All(Uri.IsHexDigit) ||
            (package.AuthScheme.HasValue && !Enum.IsDefined(package.AuthScheme.Value)))
        {
            throw new InvalidDataException("Package metadata requires a version, HTTP(S) download URL, non-negative size and SHA256 checksum.");
        }

        return package;
    }

    private static bool IsApk(VerificationEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Format))
        {
            return string.Equals(entry.Format.TrimStart('.'), "apk", StringComparison.OrdinalIgnoreCase);
        }

        return Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) &&
            uri.AbsolutePath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record VerificationResponse
    {
        public int Code { get; init; }
        public List<VerificationEntry?>? Body { get; init; }
    }

    private sealed record VerificationEntry
    {
        public string? Version { get; init; }
        public string? Name { get; init; }
        public string? UpdateLog { get; init; }
        public string? Url { get; init; }
        public string? Hash { get; init; }
        public long? Size { get; init; }
        public DateTimeOffset? ReleaseDate { get; init; }
        public bool? IsForcibly { get; init; }
        public bool? IsFreeze { get; init; }
        public int? PackageType { get; init; }
        public string? Format { get; init; }
        public AuthScheme? AuthScheme { get; init; }
        public string? AuthToken { get; init; }
    }
}
