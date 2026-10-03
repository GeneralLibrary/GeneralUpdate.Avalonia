using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text.Json;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Enums;
using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Services;

public sealed class HttpResumableApkDownloader : IUpdateDownloader, IDisposable
{
    private static readonly HashSet<char> InvalidFileNameChars = Path.GetInvalidFileNameChars().ToHashSet();

    private readonly HttpClient _httpClient;
    private readonly IFileStorage _fileStorage;
    private readonly AndroidUpdateOptions _options;
    private readonly IUpdateLogger _logger;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpDownloadOptions? _httpOptions;
    private readonly IHttpAuthProvider? _globalAuthProvider;
    private readonly Uri? _verificationUri;
    private readonly bool _ownsClient;

    /// <summary>
    /// Creates a downloader with an externally-provided HttpClient.
    /// Request policies can be supplied without replacing the caller's handler or changing its timeout.
    /// </summary>
    public HttpResumableApkDownloader(HttpClient httpClient, IFileStorage fileStorage, AndroidUpdateOptions options,
        IUpdateLogger? logger = null, HttpDownloadOptions? httpOptions = null, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = UpdateHttpClientFactory.Create(httpClient, httpOptions, out _);
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
        _logger = logger ?? new NoOpUpdateLogger();
        _httpOptions = httpOptions;
        _globalAuthProvider = httpOptions?.AuthProvider;
        _verificationUri = TryGetVerificationUri(options.UpdateServer?.RequestUrl);
        _ownsClient = ownsClient;
    }

    /// <summary>
    /// Creates a downloader with HTTP options that configure SSL, proxy, auth, and timeouts.
    /// The HttpClient is constructed internally from the provided options.
    /// </summary>
    internal HttpResumableApkDownloader(IFileStorage fileStorage, AndroidUpdateOptions options, HttpDownloadOptions httpOptions, IUpdateLogger? logger = null)
    {
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
        _httpOptions = httpOptions ?? throw new ArgumentNullException(nameof(httpOptions));
        _logger = logger ?? new NoOpUpdateLogger();

        _httpClient = UpdateHttpClientFactory.Create(null, httpOptions, out _);
        _globalAuthProvider = httpOptions.AuthProvider;
        _verificationUri = TryGetVerificationUri(options.UpdateServer?.RequestUrl);
        _ownsClient = true;
    }

    public async Task<DownloadResult> DownloadAsync(UpdatePackageInfo packageInfo, Action<DownloadProgressInfo>? progressCallback, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(packageInfo.DownloadUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(packageInfo.Sha256))
        {
            return new DownloadResult
            {
                Success = false,
                State = UpdateState.Failed,
                FailureReason = UpdateFailureReason.InvalidMetadata,
                Message = Text("Package metadata is missing DownloadUrl or Sha256."),
                PackageInfo = packageInfo
            };
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !_options.AllowInsecureHttpDownloads)
        {
            return new DownloadResult
            {
                Success = false,
                State = UpdateState.Failed,
                FailureReason = UpdateFailureReason.InvalidMetadata,
                Message = Text("Package download URL must use HTTPS."),
                PackageInfo = packageInfo
            };
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_httpOptions?.DownloadTimeout ?? Timeout.InfiniteTimeSpan);
            return await WithRetryAsync(
                ct => DownloadAttemptAsync(packageInfo, progressCallback, ct), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            var canceled = cancellationToken.IsCancellationRequested;
            return new DownloadResult
            {
                Success = false,
                State = canceled ? UpdateState.Canceled : UpdateState.Failed,
                FailureReason = canceled ? UpdateFailureReason.Canceled : UpdateFailureReason.NetworkError,
                Message = Text(canceled ? "Download canceled." : "Download timed out."),
                PackageInfo = packageInfo,
                Exception = ex
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            return new DownloadResult
            {
                Success = false,
                State = UpdateState.Failed,
                FailureReason = UpdateFailureReason.NetworkError,
                Message = Text("Network error occurred while downloading package."),
                PackageInfo = packageInfo,
                Exception = ex
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DownloadResult
            {
                Success = false,
                State = UpdateState.Failed,
                FailureReason = UpdateFailureReason.FileIoError,
                Message = Text("File I/O error occurred while downloading package."),
                PackageInfo = packageInfo,
                Exception = ex
            };
        }
    }

    private async Task<DownloadResult> DownloadAttemptAsync(UpdatePackageInfo packageInfo,
        Action<DownloadProgressInfo>? progressCallback, CancellationToken cancellationToken)
    {
        _fileStorage.EnsureDirectory(_options.DownloadDirectoryPath);
        var downloadUri = new Uri(packageInfo.DownloadUrl, UriKind.Absolute);
        var finalName = ResolveFileName(packageInfo);
        var finalFilePath = Path.Combine(_options.DownloadDirectoryPath, finalName);
        var tempFilePath = finalFilePath + _options.TemporaryFileExtension;
        var sidecarPath = tempFilePath + _options.SidecarExtension;
        var remoteInfo = await GetRemoteInfoAsync(packageInfo, cancellationToken).ConfigureAwait(false);
        var expectedMetadata = CreateMetadata(packageInfo, finalName, remoteInfo);

        var canResume = await EnsureResumeConsistencyAsync(tempFilePath, sidecarPath, expectedMetadata, cancellationToken).ConfigureAwait(false);
        var existingLength = canResume ? _fileStorage.GetFileLength(tempFilePath) : 0;
        if (existingLength > 0 && remoteInfo.ContentLength == existingLength)
        {
            var complete = CompleteDownload(packageInfo, tempFilePath, finalFilePath, sidecarPath);
            progressCallback?.Invoke(CreateProgress(packageInfo, existingLength, existingLength, 0, Text("Download completed")!));
            return complete;
        }
        if (existingLength > 0 && (!remoteInfo.AcceptRanges || existingLength > remoteInfo.ContentLength))
        {
            _logger.LogWarning("Server does not support range requests. Restarting download from zero.");
            _fileStorage.DeleteFile(tempFilePath);
            existingLength = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, packageInfo.DownloadUrl);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        await ApplyAuthAsync(request, packageInfo, downloadUri, cancellationToken).ConfigureAwait(false);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (existingLength > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            _logger.LogWarning("Server did not honor range request. Restarting download from zero.");
            _fileStorage.DeleteFile(tempFilePath);
            existingLength = 0;
        }

        response.EnsureSuccessStatusCode();

        var totalBytes = ResolveTotalBytes(packageInfo.FileSize, response.Content.Headers.ContentLength, existingLength);
        var metadataWithResponse = expectedMetadata with
        {
            ETag = response.Headers.ETag?.Tag ?? expectedMetadata.ETag,
            LastModified = response.Content.Headers.LastModified?.ToString() ?? expectedMetadata.LastModified
        };

        await _fileStorage.WriteAllTextAsync(sidecarPath, JsonSerializer.Serialize(metadataWithResponse), cancellationToken).ConfigureAwait(false);

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[_options.DownloadBufferSize];
        var downloaded = existingLength;
        var speedMeter = new SmoothedSpeedMeter(Math.Max(3, _options.SpeedSmoothingWindowSeconds));

        progressCallback?.Invoke(CreateProgress(packageInfo, downloaded, totalBytes, speedMeter.GetSpeed(downloaded), Text(existingLength > 0 ? "Resuming" : "Downloading")!));

        // The write stream is flushed and closed before the temporary file is renamed:
        // PhysicalFileStorage opens files with FileShare.None, so renaming an open file fails on
        // Windows, and skipping the flush could leave trailing bytes behind on other platforms.
        await using (var fileStream = _fileStorage.OpenWrite(tempFilePath, append: existingLength > 0))
        {
            while (true)
            {
                int read;
                try
                {
                    read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    throw new HttpRequestException("The download stream was interrupted.", ex);
                }
                if (read <= 0)
                {
                    break;
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                downloaded += read;
                var speed = speedMeter.GetSpeed(downloaded);

                progressCallback?.Invoke(CreateProgress(packageInfo, downloaded, totalBytes, speed, Text("Downloading")!));
            }
        }

        var result = CompleteDownload(packageInfo, tempFilePath, finalFilePath, sidecarPath);
        progressCallback?.Invoke(CreateProgress(packageInfo, downloaded, totalBytes, speedMeter.GetSpeed(downloaded), Text("Download completed")!));
        return result;
    }

    private DownloadResult CompleteDownload(UpdatePackageInfo packageInfo, string temporaryPath, string finalPath, string sidecarPath)
    {
        _fileStorage.MoveFile(temporaryPath, finalPath, overwrite: true);
        _fileStorage.DeleteFile(sidecarPath);
        return new DownloadResult
        {
            Success = true,
            State = UpdateState.Completed,
            Message = Text("Download finished."),
            PackageInfo = packageInfo,
            FilePath = finalPath
        };
    }

    private async Task<(string? ETag, string? LastModified, long? ContentLength, bool AcceptRanges)> GetRemoteInfoAsync(UpdatePackageInfo packageInfo, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_httpOptions?.RequestTimeout ?? Timeout.InfiniteTimeSpan);
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, packageInfo.DownloadUrl);
        await ApplyAuthAsync(headRequest, packageInfo, headRequest.RequestUri!, timeout.Token).ConfigureAwait(false);
        using var headResponse = await _httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (headResponse.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            return (null, null, null, false);
        }
        headResponse.EnsureSuccessStatusCode();

        var acceptRanges = headResponse.Headers.AcceptRanges.Any(r => string.Equals(r, "bytes", StringComparison.OrdinalIgnoreCase));
        return (
            headResponse.Headers.ETag?.Tag,
            headResponse.Content.Headers.LastModified?.ToString(),
            headResponse.Content.Headers.ContentLength,
            acceptRanges);
    }

    private async Task ApplyAuthAsync(
        HttpRequestMessage request, UpdatePackageInfo packageInfo, Uri downloadUri, CancellationToken cancellationToken)
    {
        IHttpAuthProvider? provider = null;

        // Per-package auth takes precedence
        if (packageInfo.AuthScheme.HasValue)
        {
            provider = HttpAuthProviderFactory.Create(
                packageInfo.AuthScheme.Value,
                packageInfo.AuthToken,
                packageInfo.AuthSecretKey,
                packageInfo.BasicUsername,
                packageInfo.BasicPassword);
        }

        // Fall back to global auth when per-package is not set or not configured
        if ((provider is null || provider is NoOpAuthProvider) &&
            _globalAuthProvider != null &&
            downloadUri.Scheme == Uri.UriSchemeHttps &&
            IsSameOrigin(downloadUri, _verificationUri))
        {
            if (packageInfo.AuthScheme.HasValue)
            {
                _logger.LogWarning($"AuthScheme '{packageInfo.AuthScheme}' is set but credentials are missing. Falling back to global auth provider.");
            }
            provider = _globalAuthProvider;
        }

        if (provider != null)
        {
            await provider.ApplyAuthAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<T> WithRetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        if (_httpOptions == null || _httpOptions.MaxRetryAttempts <= 1)
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }

        var maxAttempts = _httpOptions.MaxRetryAttempts;

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && attempt < maxAttempts - 1 && IsTransient(ex))
            {
                var delay = TimeSpan.FromMilliseconds(
                    Math.Min(30_000, _httpOptions.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt)));
                _logger.LogWarning($"Download attempt {attempt + 1} failed with transient error. Retrying in {delay.TotalMilliseconds}ms. {ex.GetType().Name}: {ex.Message}");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        TimeoutException => true,
        OperationCanceledException => true,
        HttpRequestException hre => hre.StatusCode is null or
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout,
        _ => false
    };

    private async Task<bool> EnsureResumeConsistencyAsync(
        string tempFilePath,
        string sidecarPath,
        DownloadResumeMetadata expected,
        CancellationToken cancellationToken)
    {
        if (!_fileStorage.FileExists(tempFilePath))
        {
            return false;
        }

        var existingJson = await _fileStorage.ReadAllTextAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(existingJson))
        {
            _fileStorage.DeleteFile(tempFilePath);
            _fileStorage.DeleteFile(sidecarPath);
            return false;
        }

        DownloadResumeMetadata? actual;
        try
        {
            actual = JsonSerializer.Deserialize<DownloadResumeMetadata>(existingJson, _jsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning($"Resume sidecar is invalid JSON. Restarting download. {ex.Message}");
            _fileStorage.DeleteFile(tempFilePath);
            _fileStorage.DeleteFile(sidecarPath);
            return false;
        }

        if (actual is null || !CanResume(expected, actual))
        {
            _fileStorage.DeleteFile(tempFilePath);
            _fileStorage.DeleteFile(sidecarPath);
            return false;
        }

        return true;
    }

    private static bool CanResume(DownloadResumeMetadata expected, DownloadResumeMetadata actual)
    {
        if (!string.Equals(expected.DownloadUrl, actual.DownloadUrl, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(expected.ExpectedSha256, actual.ExpectedSha256, StringComparison.OrdinalIgnoreCase)) return false;
        if (expected.ExpectedFileSize > 0 && actual.ExpectedFileSize > 0 && expected.ExpectedFileSize != actual.ExpectedFileSize) return false;
        if (!string.Equals(expected.FileName, actual.FileName, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(expected.ETag) && !string.IsNullOrWhiteSpace(actual.ETag) && !string.Equals(expected.ETag, actual.ETag, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrWhiteSpace(expected.LastModified) && !string.IsNullOrWhiteSpace(actual.LastModified) && !string.Equals(expected.LastModified, actual.LastModified, StringComparison.Ordinal)) return false;
        return true;
    }

    private static DownloadResumeMetadata CreateMetadata(UpdatePackageInfo packageInfo, string fileName, (string? ETag, string? LastModified, long? ContentLength, bool AcceptRanges) remote)
    {
        return new DownloadResumeMetadata
        {
            DownloadUrl = packageInfo.DownloadUrl,
            ExpectedSha256 = packageInfo.Sha256,
            ExpectedFileSize = packageInfo.FileSize > 0 ? packageInfo.FileSize : remote.ContentLength ?? 0,
            FileName = fileName,
            ETag = remote.ETag,
            LastModified = remote.LastModified
        };
    }

    private static long ResolveTotalBytes(long metadataSize, long? contentLength, long existingLength)
    {
        if (metadataSize > 0)
        {
            return metadataSize;
        }

        if (contentLength.HasValue)
        {
            return contentLength.Value + existingLength;
        }

        return existingLength;
    }

    private static DownloadProgressInfo CreateProgress(UpdatePackageInfo packageInfo, long downloaded, long total, double speed, string status)
    {
        var remaining = total > 0 ? Math.Max(0, total - downloaded) : 0;
        var progress = total > 0 ? (double)downloaded / total * 100 : 0;

        return new DownloadProgressInfo
        {
            DownloadedBytes = downloaded,
            TotalBytes = total,
            RemainingBytes = remaining,
            ProgressPercentage = Math.Clamp(progress, 0, 100),
            DownloadSpeedBytesPerSecond = speed,
            PackageInfo = packageInfo,
            StatusDescription = status
        };
    }

    private static string ResolveFileName(UpdatePackageInfo packageInfo)
    {
        var candidate = packageInfo.FileName;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = Path.GetFileName(new Uri(packageInfo.DownloadUrl).LocalPath);
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = $"update-{packageInfo.Version}.apk";
        }

        var sanitized = new string(candidate.Select(c => InvalidFileNameChars.Contains(c) ? '_' : c).ToArray());

        if (!sanitized.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
        {
            sanitized += ".apk";
        }

        return sanitized;
    }

    private static void ValidateOptions(AndroidUpdateOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DownloadDirectoryPath);
        if (options.DownloadBufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.DownloadBufferSize));
    }

    private string? Text(string? message) => UpdateMessages.Get(_options.Language, message);

    private static Uri? TryGetVerificationUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;

    private static bool IsSameOrigin(Uri downloadUri, Uri? verificationUri) =>
        verificationUri is not null &&
        verificationUri.Scheme == Uri.UriSchemeHttps &&
        downloadUri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(downloadUri.Host, verificationUri.Host, StringComparison.OrdinalIgnoreCase) &&
        downloadUri.Port == verificationUri.Port;

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private sealed class SmoothedSpeedMeter
    {
        private readonly TimeSpan _window;
        private readonly Queue<(long TimestampTicks, long Bytes)> _samples = new();

        public SmoothedSpeedMeter(int windowSeconds)
        {
            _window = TimeSpan.FromSeconds(windowSeconds);
        }

        public double GetSpeed(long downloadedBytes)
        {
            var nowTicks = Stopwatch.GetTimestamp();
            _samples.Enqueue((nowTicks, downloadedBytes));
            var windowTicks = (long)(_window.TotalSeconds * Stopwatch.Frequency);

            while (_samples.Count > 2 && nowTicks - _samples.Peek().TimestampTicks > windowTicks)
            {
                _samples.Dequeue();
            }

            if (_samples.Count < 2)
            {
                return 0;
            }

            var oldest = _samples.Peek();
            var elapsedTicks = nowTicks - oldest.TimestampTicks;
            var elapsed = elapsedTicks / (double)Stopwatch.Frequency;
            if (elapsed <= 0)
            {
                return 0;
            }

            return Math.Max(0, (downloadedBytes - oldest.Bytes) / elapsed);
        }
    }
}
