using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Enums;
using GeneralUpdate.Avalonia.Android.Models;
using GeneralUpdate.Avalonia.Android.Services;
using Xunit;

namespace GeneralUpdate.Avalonia.Android.Tests;

public sealed class HttpResumableApkDownloaderTests
{
    private static readonly byte[] Bytes = [1, 2, 3, 4, 5, 6];
    private static readonly UpdatePackageInfo Package = new()
    {
        DownloadUrl = "https://example.com/app.apk", Sha256 = new string('a', 64),
        Version = "2.0.0", FileName = "app.apk", FileSize = 6
    };
    private static readonly AndroidUpdateOptions Options = new() { DownloadDirectoryPath = "downloads" };
    private static readonly HttpDownloadOptions HttpOptions = new() { MaxRetryAttempts = 3, RetryBaseDelay = TimeSpan.Zero };
    private static readonly string Partial = Path.Combine("downloads", "app.apk.part");
    private static readonly string Final = Path.Combine("downloads", "app.apk");

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task TransientGetStatus_RetriesWithFreshRequests(HttpStatusCode status)
    {
        var gets = 0;
        var requests = new HashSet<HttpRequestMessage>();
        using var handler = new Handler((request, _) =>
        {
            Assert.True(requests.Add(request));
            return Task.FromResult(request.Method == HttpMethod.Head ? Head() :
                ++gets == 1 ? new HttpResponseMessage(status) : Body());
        });
        var storage = new Storage();
        using var client = new HttpClient(handler);
        using var downloader = Create(client, storage);

        var result = await downloader.DownloadAsync(Package, null);

        Assert.True(result.Success, result.Exception?.ToString());
        Assert.Equal(2, gets);
        Assert.Equal(Bytes, storage.Files[Final]);
    }

    [Fact]
    public async Task HeadAndGetFailures_ShareBoundedAttemptBudget()
    {
        var heads = 0;
        var gets = 0;
        using var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
                return Task.FromResult(++heads == 1 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Head());
            gets++;
            throw new HttpRequestException("Connection reset");
        });
        using var client = new HttpClient(handler);
        using var downloader = Create(client, new Storage());

        var result = await downloader.DownloadAsync(Package, null);

        Assert.Equal(UpdateFailureReason.NetworkError, result.FailureReason);
        Assert.Equal(3, heads);
        Assert.Equal(2, gets);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task RejectedHead_FallsBackToFreshGetWithoutResuming(HttpStatusCode status)
    {
        var storage = new Storage();
        SeedPartial(storage);
        var gets = 0;
        using var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Head) return Task.FromResult(new HttpResponseMessage(status));
            gets++;
            Assert.Null(request.Headers.Range);
            Assert.Null(request.Headers.IfRange);
            return Task.FromResult(Body());
        });
        using var client = new HttpClient(handler);
        using var downloader = Create(client, storage);

        Assert.True((await downloader.DownloadAsync(Package, null)).Success);
        Assert.Equal(1, gets);
        Assert.Equal(Bytes, storage.Files[Final]);
    }

    [Fact]
    public async Task RedirectedHead_IsRejectedWithoutGetOrRetry()
    {
        var requests = 0;
        using var handler = new Handler((request, _) =>
        {
            requests++;
            Assert.Equal(HttpMethod.Head, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect));
        });
        using var client = new HttpClient(handler);
        using var downloader = Create(client, new Storage());

        Assert.Equal(UpdateFailureReason.NetworkError, (await downloader.DownloadAsync(Package, null)).FailureReason);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task PermanentGetFailure_IsNotRetried()
    {
        var gets = 0;
        using var handler = new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Head
            ? Head() : ++gets > 0 ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Body()));
        using var client = new HttpClient(handler);
        using var downloader = Create(client, new Storage());
        Assert.Equal(UpdateFailureReason.NetworkError, (await downloader.DownloadAsync(Package, null)).FailureReason);
        Assert.Equal(1, gets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedBody_ResumesUsingValidatedRange(bool prematureEnd)
    {
        var gets = 0;
        using var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Head) return Task.FromResult(Head());
            if (++gets == 1)
            {
                var response = Body();
                response.Content = new StreamContent(prematureEnd
                    ? new MemoryStream(Bytes[..3]) : new BrokenReadStream());
                response.Content.Headers.ContentLength = Bytes.Length;
                return Task.FromResult(response);
            }
            Assert.Equal(3, request.Headers.Range!.Ranges.Single().From);
            Assert.Equal("\"version-one\"", request.Headers.IfRange!.EntityTag!.Tag);
            return Task.FromResult(Body(3));
        });
        var storage = new Storage();
        using var client = new HttpClient(handler);
        using var downloader = Create(client, storage);

        Assert.True((await downloader.DownloadAsync(Package, null)).Success);
        Assert.Equal(2, gets);
        Assert.Equal(Bytes, storage.Files[Final]);
    }

    [Theory]
    [InlineData("416")]
    [InlineData("wrong-offset")]
    [InlineData("wrong-total")]
    [InlineData("changed-etag")]
    [InlineData("ignored-range")]
    public async Task StaleOrInvalidRange_RestartsWithoutAppending(string failure)
    {
        var storage = new Storage();
        SeedPartial(storage);
        var gets = 0;
        using var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Head) return Task.FromResult(Head());
            if (++gets == 1)
            {
                Assert.NotNull(request.Headers.Range);
                var response = failure == "416" ? new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                    : failure == "ignored-range" ? Body() : Body(3);
                if (failure == "wrong-offset") response.Content.Headers.ContentRange = new ContentRangeHeaderValue(2, 5, 6);
                if (failure == "wrong-total") response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 6, 7);
                if (failure == "changed-etag") response.Headers.ETag = new EntityTagHeaderValue("\"other\"");
                return Task.FromResult(response);
            }
            Assert.Null(request.Headers.Range);
            return Task.FromResult(Body());
        });
        using var client = new HttpClient(handler);
        using var downloader = Create(client, storage);

        Assert.True((await downloader.DownloadAsync(Package, null)).Success);
        Assert.Equal(failure == "ignored-range" ? 1 : 2, gets);
        Assert.Equal(Bytes, storage.Files[Final]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{invalid-json")]
    public async Task MissingOrCorruptSidecar_RestartsWithoutAppending(string? sidecar)
    {
        var storage = new Storage { Sidecar = sidecar };
        storage.Files[Partial] = Bytes[..3];
        using var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Head) return Task.FromResult(Head());
            Assert.Null(request.Headers.Range);
            Assert.Null(request.Headers.IfRange);
            return Task.FromResult(Body());
        });
        using var client = new HttpClient(handler);
        using var downloader = Create(client, storage);

        Assert.True((await downloader.DownloadAsync(Package, null)).Success);
        Assert.Equal(Bytes, storage.Files[Final]);
        Assert.False(storage.FileExists(Partial));
    }

    [Fact]
    public async Task EtagChangesBetweenAttempts_RestartsWithoutAppending()
    {
        var storage = new Storage();
        var heads = 0;
        var gets = 0;
        using var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
            {
                var head = Head();
                if (++heads > 1) head.Headers.ETag = new EntityTagHeaderValue("\"version-two\"");
                return Task.FromResult(head);
            }
            var response = Body();
            if (++gets == 1)
            {
                response.Content = new StreamContent(new BrokenReadStream());
                response.Content.Headers.ContentLength = Bytes.Length;
            }
            else
            {
                Assert.Null(request.Headers.Range);
                Assert.Null(request.Headers.IfRange);
                response.Headers.ETag = new EntityTagHeaderValue("\"version-two\"");
            }
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        using var downloader = Create(client, storage);

        Assert.True((await downloader.DownloadAsync(Package, null)).Success);
        Assert.Equal(2, gets);
        Assert.Equal(Bytes, storage.Files[Final]);
    }

    [Fact]
    public async Task StreamFailures_ExhaustRetriesAndPreservePartial()
    {
        var gets = 0;
        using var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Head) return Task.FromResult(Head());
            gets++;
            var response = Body();
            response.Content = new StreamContent(new BrokenReadStream());
            response.Content.Headers.ContentLength = 6;
            return Task.FromResult(response);
        });
        var storage = new Storage();
        using var client = new HttpClient(handler);
        using var downloader = Create(client, storage);

        var result = await downloader.DownloadAsync(Package, null);

        Assert.Equal(UpdateFailureReason.NetworkError, result.FailureReason);
        Assert.Equal(3, gets);
        Assert.False(storage.FileExists(Final));
        Assert.Equal(Bytes[..3], storage.Files[Partial]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalStorageFailures_AreNotRetried(bool denied)
    {
        var gets = 0;
        var storage = new Storage { OpenWriteError = denied ? new UnauthorizedAccessException() : new IOException("Disk full") };
        using var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Head) return Task.FromResult(Head());
            gets++;
            return Task.FromResult(Body());
        });
        using var client = new HttpClient(handler);
        using var downloader = Create(client, storage);

        Assert.Equal(UpdateFailureReason.FileIoError, (await downloader.DownloadAsync(Package, null)).FailureReason);
        Assert.Equal(1, gets);
    }

    [Theory]
    [InlineData("sidecar")]
    [InlineData("write")]
    [InlineData("flush")]
    public async Task StorageWriteFailures_AreNotTreatedAsTransportFailures(string phase)
    {
        static Task Fail(CancellationToken _) => throw new IOException("Disk full");
        var storage = new Storage
        {
            SidecarWait = phase == "sidecar" ? Fail : null,
            StreamWait = phase is "write" or "flush" ? Fail : null,
            WaitOnFlush = phase == "flush"
        };
        var gets = 0;
        using var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Head) return Task.FromResult(Head());
            gets++;
            return Task.FromResult(Body());
        });
        using var client = new HttpClient(handler);
        using var downloader = Create(client, storage);
        Assert.Equal(UpdateFailureReason.FileIoError, (await downloader.DownloadAsync(Package, null)).FailureReason);
        Assert.Equal(1, gets);
    }

    [Theory]
    [InlineData("head", false)]
    [InlineData("get", false)]
    [InlineData("body", false)]
    [InlineData("read-sidecar", false)]
    [InlineData("sidecar", false)]
    [InlineData("write", false)]
    [InlineData("flush", false)]
    [InlineData("head", true)]
    [InlineData("get", true)]
    [InlineData("body", true)]
    [InlineData("read-sidecar", true)]
    [InlineData("sidecar", true)]
    [InlineData("write", true)]
    [InlineData("flush", true)]
    public async Task OverallTimeoutAndCallerCancellation_AreDistinctAndBoundDiskOperations(string phase, bool cancelCaller)
    {
        using var cts = new CancellationTokenSource();
        async Task Wait(CancellationToken token)
        {
            if (cancelCaller) cts.Cancel();
            await Task.Delay(Timeout.Infinite, token);
        }
        var storage = new Storage
        {
            SidecarWait = phase == "sidecar" ? Wait : null,
            SidecarReadWait = phase == "read-sidecar" ? Wait : null,
            StreamWait = phase is "write" or "flush" ? Wait : null,
            WaitOnFlush = phase == "flush"
        };
        if (phase == "read-sidecar") SeedPartial(storage);
        using var handler = new Handler(async (request, token) =>
        {
            if (phase == "head" && request.Method == HttpMethod.Head ||
                phase == "get" && request.Method == HttpMethod.Get) await Wait(token);
            if (request.Method == HttpMethod.Head) return Head();
            var response = Body();
            if (phase == "body") response.Content = new StreamContent(new WaitingReadStream(Wait));
            return response;
        });
        using var client = new HttpClient(handler);
        using var downloader = Create(client, storage, HttpOptions with
        {
            DownloadTimeout = cancelCaller ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(100)
        });

        var result = await downloader.DownloadAsync(Package, null, cts.Token);

        Assert.Equal(cancelCaller ? UpdateState.Canceled : UpdateState.Failed, result.State);
        Assert.Equal(cancelCaller ? UpdateFailureReason.Canceled : UpdateFailureReason.NetworkError, result.FailureReason);
        Assert.False(storage.FileExists(Final));
    }

    [Fact]
    public async Task ProbeTimeout_IsRetriedAndReportedAsNetworkFailure()
    {
        var heads = 0;
        using var handler = new Handler(async (_, token) =>
        {
            heads++;
            await Task.Delay(Timeout.Infinite, token);
            return Head();
        });
        using var client = new HttpClient(handler);
        using var downloader = Create(client, new Storage(), HttpOptions with { RequestTimeout = TimeSpan.FromMilliseconds(30) });

        var result = await downloader.DownloadAsync(Package, null);

        Assert.Equal(UpdateFailureReason.NetworkError, result.FailureReason);
        Assert.Equal(3, heads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_RequiresValidatorAndUsesLastModifiedWhenEtagIsAbsent(bool hasLastModified)
    {
        var modified = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var storage = new Storage();
        storage.Files[Partial] = Bytes[..3];
        storage.Sidecar = JsonSerializer.Serialize(new DownloadResumeMetadata
        {
            DownloadUrl = Package.DownloadUrl, ExpectedSha256 = Package.Sha256,
            ExpectedFileSize = Package.FileSize, FileName = Package.FileName!,
            LastModified = hasLastModified ? modified.ToString() : null
        });
        using var handler = new Handler((request, _) =>
        {
            var response = request.Method == HttpMethod.Head ? Head() : Body(hasLastModified ? 3 : 0);
            response.Headers.ETag = null;
            if (hasLastModified) response.Content.Headers.LastModified = modified;
            if (request.Method == HttpMethod.Get)
            {
                if (hasLastModified) Assert.Equal(modified, request.Headers.IfRange!.Date);
                else Assert.Null(request.Headers.Range);
            }
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        using var downloader = Create(client, storage);
        Assert.True((await downloader.DownloadAsync(Package, null)).Success);
        Assert.Equal(Bytes, storage.Files[Final]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disposal_RespectsClientOwnership(bool ownsClient)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Head()));
        using var client = new HttpClient(handler);
        using (var downloader = ownsClient
            ? new HttpResumableApkDownloader(client, new Storage(), Options, null, true)
            : new HttpResumableApkDownloader(client, new Storage(), Options))
        {
        }
        Assert.Equal(ownsClient, handler.Disposed);
    }

    private static HttpResumableApkDownloader Create(HttpClient client, Storage storage, HttpDownloadOptions? options = null) =>
        new(client, storage, Options, options ?? HttpOptions, false);

    private static HttpResponseMessage Head()
    {
        var response = Body();
        response.Headers.AcceptRanges.Add("bytes");
        return response;
    }

    private static HttpResponseMessage Body(int offset = 0)
    {
        var response = new HttpResponseMessage(offset == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(Bytes[offset..])
        };
        response.Headers.ETag = new EntityTagHeaderValue("\"version-one\"");
        if (offset > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, 5, 6);
        return response;
    }

    private static void SeedPartial(Storage storage)
    {
        storage.Files[Partial] = Bytes[..3];
        storage.Sidecar = JsonSerializer.Serialize(new DownloadResumeMetadata
        {
            DownloadUrl = Package.DownloadUrl, ExpectedSha256 = Package.Sha256,
            ExpectedFileSize = Package.FileSize, FileName = Package.FileName!, ETag = "\"version-one\""
        });
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class BrokenReadStream : MemoryStream
    {
        private bool _read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) throw new IOException("Connection dropped");
            _read = true;
            Bytes.AsMemory(0, 3).CopyTo(buffer);
            return ValueTask.FromResult(3);
        }
    }

    private sealed class WaitingReadStream(Func<CancellationToken, Task> wait) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await wait(cancellationToken);
            return 0;
        }
    }

    private sealed class Storage : IFileStorage
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        public string? Sidecar { get; set; }
        public Exception? OpenWriteError { get; init; }
        public Func<CancellationToken, Task>? SidecarWait { get; init; }
        public Func<CancellationToken, Task>? SidecarReadWait { get; init; }
        public Func<CancellationToken, Task>? StreamWait { get; init; }
        public bool WaitOnFlush { get; init; }
        public void EnsureDirectory(string path) { }
        public bool FileExists(string path) => Files.ContainsKey(path);
        public long GetFileLength(string path) => Files[path].Length;
        public void DeleteFile(string path) { Files.Remove(path); if (path.EndsWith(".json")) Sidecar = null; }
        public void MoveFile(string sourceFilePath, string destinationFilePath, bool overwrite)
        {
            Files[destinationFilePath] = Files[sourceFilePath];
            Files.Remove(sourceFilePath);
        }
        public Stream OpenRead(string filePath) => new MemoryStream(Files[filePath]);
        public Stream OpenWrite(string filePath, bool append)
        {
            if (OpenWriteError != null) throw OpenWriteError;
            var stream = new StoredStream(bytes => Files[filePath] = bytes, StreamWait, WaitOnFlush);
            if (append) stream.Write(Files[filePath]);
            return stream;
        }
        public async Task<string?> ReadAllTextAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (SidecarReadWait != null) await SidecarReadWait(cancellationToken);
            return Sidecar;
        }
        public async Task WriteAllTextAsync(string filePath, string content, CancellationToken cancellationToken = default)
        {
            if (SidecarWait != null) await SidecarWait(cancellationToken);
            Sidecar = content;
        }
    }

    private sealed class StoredStream(Action<byte[]> save, Func<CancellationToken, Task>? wait, bool waitOnFlush) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (wait != null && !waitOnFlush) await wait(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (wait != null && waitOnFlush) await wait(cancellationToken);
            await base.FlushAsync(cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) save(ToArray());
            base.Dispose(disposing);
        }
    }
}
