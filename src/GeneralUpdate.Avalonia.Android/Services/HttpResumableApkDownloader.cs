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
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? new NoOpUpdateLogger();
        _httpOptions = null;
        _globalAuthProvider = null;
        _ownsClient = false;
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
            _fileStorage.EnsureDirectory(_options.DownloadDirectoryPath);
            var finalName = ResolveFileName(packageInfo);
            var finalFilePath = Path.Combine(_options.DownloadDirectoryPath, finalName);
            var tempFilePath = finalFilePath + _options.TemporaryFileExtension;
            var sidecarPath = tempFilePath + _options.SidecarExtension;

            // Resolve download timeout: use configured value or infinite
            using var timeoutCts = _httpOptions != null
                ? new CancellationTokenSource(_httpOptions.DownloadTimeout)
                : null;
            using var linkedCts = timeoutCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)
                : null;
            var effectiveCt = linkedCts?.Token ?? cancellationToken;

            // Use RequestTimeout for the HEAD probe (quick server info check)
            using var probeCts = _httpOptions != null
                ? new CancellationTokenSource(_httpOptions.RequestTimeout)
                : null;
            using var probeLinkedCts = probeCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, probeCts.Token)
                : null;
            var probeCt = probeLinkedCts?.Token ?? cancellationToken;

            var remoteInfo = await WithRetryAsync(
                ct => GetRemoteInfoAsync(packageInfo, ct),
                probeCt).ConfigureAwait(false);
            var expectedMetadata = CreateMetadata(packageInfo, finalName, remoteInfo);

            var canResume = await EnsureResumeConsistencyAsync(tempFilePath, sidecarPath, expectedMetadata, cancellationToken).ConfigureAwait(false);
            var existingLength = canResume ? _fileStorage.GetFileLength(tempFilePath) : 0;
            if (existingLength > 0 && !remoteInfo.AcceptRanges)
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

            await ApplyAuthAsync(request, packageInfo, effectiveCt).ConfigureAwait(false);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, effectiveCt).ConfigureAwait(false);
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

            await using var contentStream = await response.Content.ReadAsStreamAsync(effectiveCt).ConfigureAwait(false);
            await using var fileStream = _fileStorage.OpenWrite(tempFilePath, append: existingLength > 0);

            var buffer = new byte[_options.DownloadBufferSize];
            var downloaded = existingLength;
            var speedMeter = new SmoothedSpeedMeter(Math.Max(3, _options.SpeedSmoothingWindowSeconds));

            progressCallback?.Invoke(CreateProgress(packageInfo, downloaded, totalBytes, speedMeter.GetSpeed(downloaded), existingLength > 0 ? "Resuming" : "Downloading"));

            while (true)
            {
                var read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), effectiveCt).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                downloaded += read;
                var speed = speedMeter.GetSpeed(downloaded);

                progressCallback?.Invoke(CreateProgress(packageInfo, downloaded, totalBytes, speed, "Downloading"));
            }

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
        }
        catch (OperationCanceledException)
        {
            return new DownloadResult
            {
                Success = false,
                State = UpdateState.Canceled,
                FailureReason = UpdateFailureReason.Canceled,
                Message = "Download canceled.",
                PackageInfo = packageInfo
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
        catch (IOException ex)
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
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, packageInfo.DownloadUrl);
        await ApplyAuthAsync(headRequest, packageInfo, cancellationToken).ConfigureAwait(false);
        using var headResponse = await _httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!headResponse.IsSuccessStatusCode)
        {
            return (null, null, null, false);
        }

        var acceptRanges = headResponse.Headers.AcceptRanges.Any(r => string.Equals(r, "bytes", StringComparison.OrdinalIgnoreCase));
        return (
            headResponse.Headers.ETag?.Tag,
            headResponse.Content.Headers.LastModified?.ToString(),
            headResponse.Content.Headers.ContentLength,
            acceptRanges);
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
        if ((provider is null || provider is NoOpAuthProvider) && _globalAuthProvider != null)
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
            catch (Exception ex) when (attempt < maxAttempts - 1 && IsTransient(ex))
            {
                var delay = TimeSpan.FromMilliseconds(
                    _httpOptions.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt));
                _logger.LogWarning($"Download attempt {attempt + 1} failed with transient error. Retrying in {delay.TotalMilliseconds}ms. {ex.GetType().Name}: {ex.Message}");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        TimeoutException => true,
        OperationCanceledException => false,
        IOException ioe when ioe.InnerException is TimeoutException => true,
        HttpRequestException hre => hre.StatusCode is
            HttpStatusCode.RequestTimeout or
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
