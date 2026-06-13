using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Enums;

namespace GeneralUpdate.Avalonia.Android.Services;

/// <summary>
/// No-op authentication provider. Does not modify the request.
/// Used as the default when no authentication is configured.
/// </summary>
public sealed class NoOpAuthProvider : IHttpAuthProvider
{
    public Task ApplyAuthAsync(HttpRequestMessage request, CancellationToken token = default)
        => Task.CompletedTask;
}

/// <summary>
/// Bearer token authentication provider.
/// Adds <c>Authorization: Bearer &lt;token&gt;</c> header to the request.
/// </summary>
public sealed class BearerTokenAuthProvider : IHttpAuthProvider
{
    private readonly string _token;

    public BearerTokenAuthProvider(string token)
    {
#if NET6_0_OR_GREATER
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
#else
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Token cannot be null or whitespace.", nameof(token));
#endif
        _token = token;
    }

    public Task ApplyAuthAsync(HttpRequestMessage request, CancellationToken token = default)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return Task.CompletedTask;
    }
}

/// <summary>
/// API key authentication provider.
/// Adds a custom header (default: <c>X-Api-Key</c>) with the API key value.
/// </summary>
public sealed class ApiKeyAuthProvider : IHttpAuthProvider
{
    private readonly string _apiKey;
    private readonly string _headerName;

    public ApiKeyAuthProvider(string apiKey, string headerName = "X-Api-Key")
    {
#if NET6_0_OR_GREATER
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
#else
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key cannot be null or whitespace.", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(headerName))
            throw new ArgumentException("Header name cannot be null or whitespace.", nameof(headerName));
#endif
        _apiKey = apiKey;
        _headerName = headerName;
    }

    public Task ApplyAuthAsync(HttpRequestMessage request, CancellationToken token = default)
    {
        request.Headers.Remove(_headerName);
        request.Headers.Add(_headerName, _apiKey);
        return Task.CompletedTask;
    }
}

/// <summary>
/// HTTP Basic authentication provider.
/// Adds <c>Authorization: Basic &lt;base64&gt;</c> header to the request.
/// </summary>
public sealed class BasicAuthProvider : IHttpAuthProvider
{
    private readonly string _credential;

    public BasicAuthProvider(string credential)
    {
#if NET6_0_OR_GREATER
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
#else
        if (string.IsNullOrWhiteSpace(credential))
            throw new ArgumentException("Credential cannot be null or whitespace.", nameof(credential));
#endif
        _credential = credential;
    }

    /// <summary>
    /// Creates a Basic authentication header value from username and password.
    /// </summary>
    public static string EncodeCredential(string username, string password)
    {
#if NET6_0_OR_GREATER
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
#else
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username cannot be null or whitespace.", nameof(username));
#endif
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password ?? string.Empty}"));
    }

    /// <summary>
    /// Creates a BasicAuthProvider from username and password credentials.
    /// </summary>
    public static BasicAuthProvider FromCredentials(string username, string password)
        => new BasicAuthProvider(EncodeCredential(username, password));

    public Task ApplyAuthAsync(HttpRequestMessage request, CancellationToken token = default)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _credential);
        return Task.CompletedTask;
    }
}

/// <summary>
/// HMAC-SHA256 signature authentication provider.
/// Adds <c>X-Update-Timestamp</c> and <c>X-Update-Signature</c> headers.
/// The signature is computed as HMACSHA256 over <c>body|timestamp</c> using the secret key.
/// </summary>
public sealed class HmacAuthProvider : IHttpAuthProvider
{
    private readonly string _secretKey;

    public HmacAuthProvider(string secretKey)
    {
#if NET6_0_OR_GREATER
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
#else
        if (string.IsNullOrWhiteSpace(secretKey))
            throw new ArgumentException("Secret key cannot be null or whitespace.", nameof(secretKey));
#endif
        _secretKey = secretKey;
    }

    public async Task ApplyAuthAsync(HttpRequestMessage request, CancellationToken token = default)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var body = string.Empty;

        if (request.Content != null)
        {
            body = await request.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }

        var message = $"{body}|{timestamp}";
        var keyBytes = Encoding.UTF8.GetBytes(_secretKey);

#if NET6_0_OR_GREATER
        var hash = HMACSHA256.HashData(keyBytes, Encoding.UTF8.GetBytes(message));
#else
        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
#endif
        var signature = Convert.ToHexString(hash).ToLowerInvariant();

        request.Headers.Remove("X-Update-Timestamp");
        request.Headers.Remove("X-Update-Signature");
        request.Headers.Add("X-Update-Timestamp", timestamp);
        request.Headers.Add("X-Update-Signature", signature);
    }
}

/// <summary>
/// Factory for creating <see cref="IHttpAuthProvider"/> instances
/// based on the specified authentication scheme and credentials.
/// </summary>
public static class HttpAuthProviderFactory
{
    /// <summary>
    /// Creates an <see cref="IHttpAuthProvider"/> from an <see cref="AuthScheme"/> with the given credentials.
    /// Returns <see cref="NoOpAuthProvider"/> when scheme is null or unrecognized.
    /// </summary>
    public static IHttpAuthProvider Create(
        AuthScheme? scheme,
        string? token = null,
        string? secretKey = null,
        string? basicUsername = null,
        string? basicPassword = null)
    {
        return scheme switch
        {
            AuthScheme.Bearer when !string.IsNullOrWhiteSpace(token)
                => new BearerTokenAuthProvider(token),
            AuthScheme.ApiKey when !string.IsNullOrWhiteSpace(token)
                => new ApiKeyAuthProvider(token),
            AuthScheme.Basic when !string.IsNullOrWhiteSpace(basicUsername)
                => BasicAuthProvider.FromCredentials(basicUsername, basicPassword ?? string.Empty),
            AuthScheme.Hmac when !string.IsNullOrWhiteSpace(secretKey)
                => new HmacAuthProvider(secretKey),
            _ => new NoOpAuthProvider()
        };
    }

    /// <summary>
    /// Creates an <see cref="IHttpAuthProvider"/> from a string-based scheme identifier
    /// with the given credentials. Supports "bearer", "apikey", "basic", "hmac" (case-insensitive).
    /// Returns <see cref="NoOpAuthProvider"/> when scheme is null or unrecognized.
    /// </summary>
    public static IHttpAuthProvider Create(
        string? scheme,
        string? token = null,
        string? secretKey = null,
        string? basicUsername = null,
        string? basicPassword = null)
    {
        if (string.IsNullOrWhiteSpace(scheme))
            return new NoOpAuthProvider();

        return scheme.ToLowerInvariant() switch
        {
            "bearer" when !string.IsNullOrWhiteSpace(token)
                => new BearerTokenAuthProvider(token),
            "apikey" when !string.IsNullOrWhiteSpace(token)
                => new ApiKeyAuthProvider(token),
            "basic" when !string.IsNullOrWhiteSpace(basicUsername)
                => BasicAuthProvider.FromCredentials(basicUsername, basicPassword ?? string.Empty),
            "hmac" when !string.IsNullOrWhiteSpace(secretKey)
                => new HmacAuthProvider(secretKey),
            _ => new NoOpAuthProvider()
        };
    }
}
