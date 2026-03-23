using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Services;

public sealed class HttpResumableApkDownloader : IUpdateDownloader
{
    private readonly HttpClient _httpClient;
    private readonly IFileStorage _fileStorage;
    private readonly AndroidUpdateOptions _options;
    private readonly IUpdateLogger _logger;

    public HttpResumableApkDownloader(HttpClient httpClient, IFileStorage fileStorage, AndroidUpdateOptions options, IUpdateLogger? logger = null)
    {
        _httpClient = httpClient;
        _fileStorage = fileStorage;
        _options = options;
        _logger = logger ?? new NoOpUpdateLogger();
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

            var remoteInfo = await GetRemoteInfoAsync(packageInfo.DownloadUrl, cancellationToken).ConfigureAwait(false);
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
            await using var fileStream = _fileStorage.OpenWrite(tempFilePath, append: existingLength > 0);

            var buffer = new byte[_options.DownloadBufferSize];
            var downloaded = existingLength;
            var speedMeter = new SmoothedSpeedMeter(Math.Max(3, _options.SpeedSmoothingWindowSeconds));

            progressCallback?.Invoke(CreateProgress(packageInfo, downloaded, totalBytes, speedMeter.GetSpeed(downloaded), existingLength > 0 ? "Resuming" : "Downloading"));

            while (true)
            {
                var read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
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
                State = UpdateState.Downloading,
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

    private async Task<(string? ETag, string? LastModified, long? ContentLength, bool AcceptRanges)> GetRemoteInfoAsync(string downloadUrl, CancellationToken cancellationToken)
    {
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, downloadUrl);
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

        var actual = JsonSerializer.Deserialize<DownloadResumeMetadata>(existingJson);
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

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(candidate.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());

        if (!sanitized.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
        {
            sanitized += ".apk";
        }

        return sanitized;
    }

    private sealed class SmoothedSpeedMeter
    {
        private readonly TimeSpan _window;
        private readonly Queue<(DateTimeOffset Timestamp, long Bytes)> _samples = new();

        public SmoothedSpeedMeter(int windowSeconds)
        {
            _window = TimeSpan.FromSeconds(windowSeconds);
        }

        public double GetSpeed(long downloadedBytes)
        {
            var now = DateTimeOffset.UtcNow;
            _samples.Enqueue((now, downloadedBytes));

            while (_samples.Count > 1 && now - _samples.Peek().Timestamp > _window)
            {
                _samples.Dequeue();
            }

            if (_samples.Count < 2)
            {
                return 0;
            }

            var oldest = _samples.Peek();
            var elapsed = (now - oldest.Timestamp).TotalSeconds;
            if (elapsed <= 0)
            {
                return 0;
            }

            return Math.Max(0, (downloadedBytes - oldest.Bytes) / elapsed);
        }
    }
}
