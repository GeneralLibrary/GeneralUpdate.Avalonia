namespace GeneralUpdate.Avalonia.Android.Abstractions;

/// <summary>
/// Provides authentication for HTTP requests.
/// Implementations can add headers, modify the request, or perform
/// any other authentication flow before the request is sent.
/// </summary>
public interface IHttpAuthProvider
{
    Task ApplyAuthAsync(HttpRequestMessage request, CancellationToken token = default);
}
