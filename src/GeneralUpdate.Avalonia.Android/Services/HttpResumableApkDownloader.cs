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
    private readonly bool _ownsClient;

    /// <summary>
    /// Creates a downloader with an externally-provided HttpClient.
    /// No authentication or custom HTTP options are applied.
    /// </summary>
    public HttpResumableApkDownloader(HttpClient httpClient, IFileStorage fileStorage, AndroidUpdateOptions options, IUpdateLogger? logger = null)
        : this(httpClient, fileStorage, options, null, false, logger)
    {
    }

    internal HttpResumableApkDownloader(HttpClient httpClient, IFileStorage fileStorage, AndroidUpdateOptions options,
        HttpDownloadOptions? httpOptions, bool ownsClient, IUpdateLogger? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? new NoOpUpdateLogger();
        _httpOptions = httpOptions;
        _globalAuthProvider = httpOptions?.AuthProvider;
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
        _httpOptions = httpOptions ?? throw new ArgumentNullException(nameof(httpOptions));
        _logger = logger ?? new NoOpUpdateLogger();

        var handler = httpOptions.BuildHandler();
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            // Timeout is managed per-request via CancellationTokenSource linked to DownloadTimeout
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
        _globalAuthProvider = httpOptions.AuthProvider;
        _ownsClient = true;
    }

    public async Task<DownloadResult> DownloadAsync(UpdatePackageInfo packageInfo, Action<DownloadProgressInfo>? progressCallback, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageInfo.DownloadUrl) || string.IsNullOrWhiteSpace(packageInfo.Sha256))
        {
            return new DownloadResult
            {
                Success = false,
                State = UpdateState.Failed,
                FailureReason = UpdateFailureReason.InvalidMetadata,
                Message = "Package metadata is missing DownloadUrl or Sha256.",
                PackageInfo = packageInfo
            };
        }

        try
        {
            using var timeoutCts = _httpOptions != null
                ? new CancellationTokenSource(_httpOptions.DownloadTimeout)
                : null;
            using var linkedCts = timeoutCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)
                : null;
            var effectiveCt = linkedCts?.Token ?? cancellationToken;

            effectiveCt.ThrowIfCancellationRequested();
            _fileStorage.EnsureDirectory(_options.DownloadDirectoryPath);
            var finalName = ResolveFileName(packageInfo);
            var finalFilePath = Path.Combine(_options.DownloadDirectoryPath, finalName);
            var tempFilePath = finalFilePath + _options.TemporaryFileExtension;
            var sidecarPath = tempFilePath + _options.SidecarExtension;

            return await WithRetryAsync(async ct =>
            {
                var remoteInfo = await GetRemoteInfoAsync(packageInfo, ct).ConfigureAwait(false);
                var expectedMetadata = CreateMetadata(packageInfo, finalName, remoteInfo);

                var canResume = await EnsureResumeConsistencyAsync(tempFilePath, sidecarPath, expectedMetadata, ct).ConfigureAwait(false);
                var existingLength = canResume ? _fileStorage.GetFileLength(tempFilePath) : 0;
                var ifRange = CreateIfRange(expectedMetadata);
                if (existingLength > 0 && (!remoteInfo.AcceptRanges || ifRange is null))
                {
                    _logger.LogWarning("Safe range resumption is unavailable. Restarting download from zero.");
                    ct.ThrowIfCancellationRequested();
                    _fileStorage.DeleteFile(tempFilePath);
                    existingLength = 0;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, packageInfo.DownloadUrl);
                if (existingLength > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(existingLength, null);
                    request.Headers.IfRange = ifRange;
                }

                await ApplyAuthAsync(request, packageInfo, ct).ConfigureAwait(false);

                using var response = await SendAsync(request, ct).ConfigureAwait(false);
                if (existingLength > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    ct.ThrowIfCancellationRequested();
                    _fileStorage.DeleteFile(tempFilePath);
                    _fileStorage.DeleteFile(sidecarPath);
                    throw new HttpRequestException("The partial download is stale; restart from zero.");
                }
                if (existingLength > 0 && response.StatusCode == HttpStatusCode.OK)
                {
                    _logger.LogWarning("Server did not honor range request. Restarting download from zero.");
                    ct.ThrowIfCancellationRequested();
                    _fileStorage.DeleteFile(tempFilePath);
                    existingLength = 0;
                }

                response.EnsureSuccessStatusCode();
                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                {
                    throw new HttpRequestException("Unexpected status for a package download.", null, response.StatusCode);
                }
                if (response.StatusCode == HttpStatusCode.PartialContent &&
                    !IsValidRange(response, existingLength, expectedMetadata))
                {
                    ct.ThrowIfCancellationRequested();
                    _fileStorage.DeleteFile(tempFilePath);
                    _fileStorage.DeleteFile(sidecarPath);
                    throw new HttpRequestException("The server returned an inconsistent Content-Range.");
                }

                var totalBytes = ResolveTotalBytes(packageInfo.FileSize, response.Content.Headers.ContentLength, existingLength);
                var metadataWithResponse = expectedMetadata with
                {
                    ETag = response.Headers.ETag?.ToString() ?? expectedMetadata.ETag,
                    LastModified = response.Content.Headers.LastModified?.ToString() ?? expectedMetadata.LastModified
                };

                await _fileStorage.WriteAllTextAsync(sidecarPath, JsonSerializer.Serialize(metadataWithResponse), ct).ConfigureAwait(false);

                await using var contentStream = await ReadNetworkAsync(
                    () => response.Content.ReadAsStreamAsync(ct), ct).ConfigureAwait(false);
                var buffer = new byte[_options.DownloadBufferSize];
                var downloaded = existingLength;
                var speedMeter = new SmoothedSpeedMeter(Math.Max(3, _options.SpeedSmoothingWindowSeconds));

                progressCallback?.Invoke(CreateProgress(packageInfo, downloaded, totalBytes, speedMeter.GetSpeed(downloaded), existingLength > 0 ? "Resuming" : "Downloading"));

                // Close the flushed write stream before moving the file (FileShare.None on Windows).
                ct.ThrowIfCancellationRequested();
                await using (var fileStream = _fileStorage.OpenWrite(tempFilePath, append: existingLength > 0))
                {
                    while (true)
                    {
                        var read = await ReadNetworkAsync(
                            () => contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).AsTask(), ct).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            break;
                        }

                        await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        downloaded += read;
                        var speed = speedMeter.GetSpeed(downloaded);

                        progressCallback?.Invoke(CreateProgress(packageInfo, downloaded, totalBytes, speed, "Downloading"));
                    }
                    await fileStream.FlushAsync(ct).ConfigureAwait(false);
                }

                var expectedBodyLength = response.Content.Headers.ContentLength;
                if ((expectedBodyLength.HasValue && downloaded - existingLength != expectedBodyLength.Value) ||
                    (response.Content.Headers.ContentRange?.Length is long rangeLength && downloaded != rangeLength) ||
                    (expectedMetadata.ExpectedFileSize > 0 && downloaded != expectedMetadata.ExpectedFileSize))
                {
                    throw new HttpRequestException("The response body length does not match the expected package length.");
                }

                ct.ThrowIfCancellationRequested();
                if (_fileStorage.FileExists(finalFilePath))
                {
                    _fileStorage.DeleteFile(finalFilePath);
                }

                _fileStorage.MoveFile(tempFilePath, finalFilePath, overwrite: true);
                _fileStorage.DeleteFile(sidecarPath);

                progressCallback?.Invoke(CreateProgress(packageInfo, downloaded, totalBytes, speedMeter.GetSpeed(downloaded), "Download completed"));

                return new DownloadResult
                {
                    Success = true,
                    State = UpdateState.Completed,
                    FailureReason = UpdateFailureReason.None,
                    Message = "Download finished.",
                    PackageInfo = packageInfo,
                    FilePath = finalFilePath
                };
            }, effectiveCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            return new DownloadResult
            {
                Success = false,
                State = cancellationToken.IsCancellationRequested ? UpdateState.Canceled : UpdateState.Failed,
                FailureReason = cancellationToken.IsCancellationRequested ? UpdateFailureReason.Canceled : UpdateFailureReason.NetworkError,
                Message = cancellationToken.IsCancellationRequested ? "Download canceled." : "Download timed out.",
                PackageInfo = packageInfo,
                Exception = ex
            };
        }
        catch (HttpRequestException ex)
        {
            return new DownloadResult
            {
                Success = false,
                State = UpdateState.Failed,
                FailureReason = UpdateFailureReason.NetworkError,
                Message = "Network error occurred while downloading package.",
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
                Message = "File I/O error occurred while downloading package.",
                PackageInfo = packageInfo,
                Exception = ex
            };
        }
        catch (Exception ex)
        {
            return new DownloadResult
            {
                Success = false,
                State = UpdateState.Failed,
                FailureReason = UpdateFailureReason.Unknown,
                Message = "Unexpected error occurred while downloading package.",
                PackageInfo = packageInfo,
                Exception = ex
            };
        }
    }

    private async Task<(string? ETag, string? LastModified, long? ContentLength, bool AcceptRanges)> GetRemoteInfoAsync(UpdatePackageInfo packageInfo, CancellationToken cancellationToken)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_httpOptions != null)
        {
            probeCts.CancelAfter(_httpOptions.RequestTimeout);
        }
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, packageInfo.DownloadUrl);
        try
        {
            await ApplyAuthAsync(headRequest, packageInfo, probeCts.Token).ConfigureAwait(false);
            using var headResponse = await SendAsync(headRequest, probeCts.Token).ConfigureAwait(false);
            // Some GET-signed URLs reject HEAD. Probe rejection must not prevent a fresh GET;
            // redirects remain rejected and transient failures still consume the retry budget.
            if ((int)headResponse.StatusCode >= 400 && !IsTransientStatus(headResponse.StatusCode))
            {
                return (null, null, null, false);
            }
            headResponse.EnsureSuccessStatusCode();
            var acceptRanges = headResponse.Headers.AcceptRanges.Any(r => string.Equals(r, "bytes", StringComparison.OrdinalIgnoreCase));
            return (
                headResponse.Headers.ETag?.ToString(),
                headResponse.Content.Headers.LastModified?.ToString(),
                headResponse.Content.Headers.ContentLength,
                acceptRanges);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("The HEAD probe timed out.", ex);
        }
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        ReadNetworkAsync(() => _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken), cancellationToken);

    // Only transport reads are translated; local file I/O must not trigger network retries.
    private static async Task<T> ReadNetworkAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException ||
                                   ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("The network transfer was interrupted.", ex);
        }
    }

    private static RangeConditionHeaderValue? CreateIfRange(DownloadResumeMetadata metadata)
    {
        if (EntityTagHeaderValue.TryParse(metadata.ETag, out var etag) && !etag.IsWeak)
        {
            return new RangeConditionHeaderValue(etag);
        }
        return DateTimeOffset.TryParse(metadata.LastModified, out var modified)
            ? new RangeConditionHeaderValue(modified)
            : null;
    }

    private static bool IsValidRange(HttpResponseMessage response, long offset, DownloadResumeMetadata metadata)
    {
        var range = response.Content.Headers.ContentRange;
        if (range is null || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
            range.From != offset || !range.To.HasValue || !range.Length.HasValue ||
            range.To != range.Length - 1 ||
            (metadata.ExpectedFileSize > 0 && range.Length != metadata.ExpectedFileSize) ||
            (response.Content.Headers.ContentLength.HasValue &&
             response.Content.Headers.ContentLength != range.To - range.From + 1))
        {
            return false;
        }
        if (offset == 0) return true;
        if (response.Headers.ETag != null && metadata.ETag != null &&
            response.Headers.ETag.ToString() != metadata.ETag) return false;
        if (response.Content.Headers.LastModified != null && metadata.LastModified != null &&
            response.Content.Headers.LastModified.Value.ToString() != metadata.LastModified) return false;
        return true;
    }

    private async Task ApplyAuthAsync(HttpRequestMessage request, UpdatePackageInfo packageInfo, CancellationToken cancellationToken)
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
        if ((provider is null || provider is NoOpAuthProvider) && _globalAuthProvider != null &&
            _httpOptions?.IsDownloadAuthenticationAllowed(request.RequestUri, _options.UpdateServer?.RequestUrl) == true)
        {
            if (packageInfo.AuthScheme.HasValue)
            {
                _logger.LogWarning($"AuthScheme '{packageInfo.AuthScheme}' is set but credentials are missing. Falling back to global auth provider.");
            }
            provider = _globalAuthProvider;
        }

        if (provider is not null and not NoOpAuthProvider)
        {
            HttpDownloadOptions.EnsureAuthenticationTransport(
                request.RequestUri, _httpOptions?.AllowInsecureAuthentication == true);
            await provider.ApplyAuthAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<T> WithRetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, _httpOptions?.MaxRetryAttempts ?? 1);

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
                    Math.Min(int.MaxValue, Math.Max(0, _httpOptions!.RetryBaseDelay.TotalMilliseconds) * Math.Pow(2, attempt)));
                _logger.LogWarning($"Download attempt {attempt + 1} failed with transient error. Retrying in {delay.TotalMilliseconds}ms. {ex.GetType().Name}: {ex.Message}");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException hre && IsTransientStatus(hre.StatusCode);

    private static bool IsTransientStatus(HttpStatusCode? status) => status is null or
        HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout;

    private async Task<bool> EnsureResumeConsistencyAsync(
        string tempFilePath,
        string sidecarPath,
        DownloadResumeMetadata expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        if (!string.Equals(expected.DownloadUrl, actual.DownloadUrl, StringComparison.Ordinal)) return false;
        if (!string.Equals(expected.ExpectedSha256, actual.ExpectedSha256, StringComparison.OrdinalIgnoreCase)) return false;
        if (expected.ExpectedFileSize > 0 && actual.ExpectedFileSize > 0 && expected.ExpectedFileSize != actual.ExpectedFileSize) return false;
        if (!string.Equals(expected.FileName, actual.FileName, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(expected.ETag, actual.ETag, StringComparison.Ordinal)) return false;
        if (!string.Equals(expected.LastModified, actual.LastModified, StringComparison.Ordinal)) return false;
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
