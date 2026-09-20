using System.Net;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Models;
using GeneralUpdate.Avalonia.Android.Services;
using Xunit;

namespace GeneralUpdate.Avalonia.Android.Tests;

public sealed class BootstrapLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly UpdatePackageInfo Package = new()
    {
        Version = "2.0.0",
        DownloadUrl = "https://example.com/app.apk",
        Sha256 = new string('a', 64),
        FileName = "app.apk",
        FileSize = 10
    };

    [Fact]
    public async Task HashCancellation_ReportsOneTerminalFailure_AndReleasesGate()
    {
        using var cancel = new CancellationTokenSource();
        var original = new OperationCanceledException(cancel.Token);
        var hash = new HashValidator
        {
            Run = _ =>
            {
                cancel.Cancel();
                throw original;
            }
        };
        var storage = new Storage { DeleteError = new UnauthorizedAccessException("cleanup") };
        using var bootstrap = Create(hash: hash, storage: storage);
        var failures = ObserveFailures(bootstrap);

        var result = await bootstrap.DownloadAndVerifyAsync(Package, cancel.Token);

        AssertTerminal(bootstrap, failures, result, UpdateState.Canceled, UpdateFailureReason.Canceled);
        Assert.Same(original, result.Exception);
        Assert.Equal(1, storage.Deletes);
        hash.Run = _ => Task.FromResult(new HashValidationResult { Success = true });
        Assert.True((await bootstrap.DownloadAndVerifyAsync(Package).WaitAsync(Timeout)).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SizeReadFailure_PreservesOriginalDespiteCleanupFailure(bool denied)
    {
        Exception original = denied ? new UnauthorizedAccessException("size") : new IOException("size");
        var storage = new Storage { LengthError = original, DeleteError = new IOException("cleanup") };
        using var bootstrap = Create(storage: storage);
        var failures = ObserveFailures(bootstrap);

        var result = await bootstrap.DownloadAndVerifyAsync(Package);

        AssertTerminal(bootstrap, failures, result, UpdateState.Failed, UpdateFailureReason.FileIoError);
        Assert.Same(original, result.Exception);
        Assert.Equal("app.apk", result.FilePath);
        storage.LengthError = null;
        Assert.True((await bootstrap.DownloadAndVerifyAsync(Package).WaitAsync(Timeout)).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedFileCleanupFailure_DoesNotReplaceMismatch(bool sizeMismatch)
    {
        var original = new InvalidDataException("hash");
        var hash = new HashValidator
        {
            Run = _ => Task.FromResult(new HashValidationResult
            {
                Success = false,
                FailureReason = UpdateFailureReason.HashMismatch,
                Exception = original
            })
        };
        var storage = new Storage { Length = sizeMismatch ? 3 : 10, DeleteError = new UnauthorizedAccessException("cleanup") };
        using var bootstrap = Create(hash: hash, storage: storage);
        var failures = ObserveFailures(bootstrap);

        var result = await bootstrap.DownloadAndVerifyAsync(Package);

        AssertTerminal(bootstrap, failures, result, UpdateState.Failed,
            sizeMismatch ? UpdateFailureReason.FileIoError : UpdateFailureReason.HashMismatch);
        Assert.Equal(1, storage.Deletes);
        if (sizeMismatch)
            Assert.Contains("size mismatch", result.Message);
        else
            Assert.Same(original, result.Exception);
    }

    [Fact]
    public async Task HashReturnedCancellation_RemainsCanceled()
    {
        var hash = new HashValidator
        {
            Run = _ => Task.FromResult(new HashValidationResult { State = UpdateState.Canceled, FailureReason = UpdateFailureReason.Canceled })
        };
        using var bootstrap = Create(hash: hash);
        var failures = ObserveFailures(bootstrap);
        var result = await bootstrap.DownloadAndVerifyAsync(Package);
        AssertTerminal(bootstrap, failures, result, UpdateState.Canceled, UpdateFailureReason.Canceled);
    }

    [Fact]
    public async Task InstallPermissionException_ReportsOneTerminalFailure()
    {
        var original = new UnauthorizedAccessException("permission");
        using var bootstrap = Create(installer: new Installer { Run = _ => throw original });
        var failures = ObserveFailures(bootstrap);
        var result = await bootstrap.LaunchInstallerAsync(Package, "app.apk");
        AssertTerminal(bootstrap, failures, result, UpdateState.Failed, UpdateFailureReason.InstallPermissionDenied);
        Assert.Same(original, result.Exception);
    }

    [Fact]
    public async Task Dispose_CancelsActiveOperationAndWaiters_ButDefersResourcesUntilDrain()
    {
        var started = Signal();
        var cancellationSeen = Signal();
        var finish = Signal();
        var downloader = new Downloader
        {
            Run = async token =>
            {
                using var registration = token.Register(() => cancellationSeen.TrySetResult());
                started.TrySetResult();
                await finish.Task;
                token.ThrowIfCancellationRequested();
                return Downloaded();
            }
        };
        var bootstrap = Create(downloader: downloader);
        var failures = ObserveFailures(bootstrap);
        var active = bootstrap.DownloadAndVerifyAsync(Package);
        await started.Task.WaitAsync(Timeout);
        var waitingDownload = bootstrap.DownloadAndVerifyAsync(Package);
        var waitingValidation = bootstrap.ValidateAsync("1.0.0");
        var waitingInstall = bootstrap.LaunchInstallerAsync(Package, "app.apk");

        bootstrap.Dispose();
        var drained = bootstrap.DisposeAsync().AsTask();
        await cancellationSeen.Task.WaitAsync(Timeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingDownload.WaitAsync(Timeout));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingValidation.WaitAsync(Timeout));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingInstall.WaitAsync(Timeout));
        Assert.False(drained.IsCompleted);
        Assert.Equal(0, downloader.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => bootstrap.DownloadAndVerifyAsync(Package));

        finish.TrySetResult();
        var result = await active.WaitAsync(Timeout);
        await drained.WaitAsync(Timeout);
        Assert.Equal(UpdateState.Canceled, result.State);
        Assert.Single(failures);
        Assert.Equal(1, downloader.DisposeCount);
        await bootstrap.DisposeAsync();
        Assert.Equal(1, downloader.DisposeCount);
    }

    [Fact]
    public async Task CallerCancellationWhileWaiting_DoesNotChangeActiveStateOrRaiseFailure()
    {
        var finish = Signal();
        var downloader = new Downloader { Run = async _ => { await finish.Task; return Downloaded(); } };
        using var bootstrap = Create(downloader: downloader);
        var failures = ObserveFailures(bootstrap);
        var active = bootstrap.DownloadAndVerifyAsync(Package);
        using var cancel = new CancellationTokenSource();
        var waiting = bootstrap.DownloadAndVerifyAsync(Package, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Timeout));
        Assert.Equal(UpdateState.Downloading, bootstrap.GetSnapshot().State);
        Assert.Empty(failures);
        finish.TrySetResult();
        Assert.True((await active.WaitAsync(Timeout)).Success);
    }

    [Fact]
    public async Task SynchronousDisposeFromProgress_DoesNotDeadlockOrDisposeActiveDownloader()
    {
        var downloader = new Downloader();
        var bootstrap = Create(downloader: downloader);
        var failures = ObserveFailures(bootstrap);
        var disposedDuringCallback = -1;
        bootstrap.AddListenerDownloadProgressChanged += (_, _) =>
        {
            bootstrap.Dispose();
            disposedDuringCallback = downloader.DisposeCount;
        };

        var result = await Task.Run(() => bootstrap.DownloadAndVerifyAsync(Package)).WaitAsync(Timeout);

        Assert.Equal(UpdateState.Canceled, result.State);
        Assert.Single(failures);
        Assert.Equal(0, disposedDuringCallback);
        await bootstrap.DisposeAsync().AsTask().WaitAsync(Timeout);
        Assert.Equal(1, downloader.DisposeCount);
    }

    [Fact]
    public async Task ThrowingProgressAndCompletionSubscribers_DoNotBlockOtherSubscribersOrSuccess()
    {
        using var bootstrap = Create(logger: new ThrowingLogger());
        var progress = 0;
        var completed = 0;
        bootstrap.AddListenerDownloadProgressChanged += (_, _) => throw new InvalidOperationException("progress");
        bootstrap.AddListenerDownloadProgressChanged += (_, _) => progress++;
        bootstrap.AddListenerUpdateCompleted += (_, _) => throw new InvalidOperationException("completed");
        bootstrap.AddListenerUpdateCompleted += (_, _) => completed++;

        var result = await bootstrap.DownloadAndVerifyAsync(Package);

        Assert.True(result.Success);
        Assert.Equal(1, progress);
        Assert.Equal(1, completed);
        Assert.Equal(UpdateState.ReadyToInstall, bootstrap.GetSnapshot().State);
    }

    [Fact]
    public async Task ThrowingFailureSubscriberAndLogger_DoNotMaskOriginalFailure()
    {
        var original = new IOException("size");
        using var bootstrap = Create(storage: new Storage { LengthError = original }, logger: new ThrowingLogger());
        bootstrap.AddListenerUpdateFailed += (_, _) => throw new InvalidOperationException("subscriber");
        var failures = ObserveFailures(bootstrap);
        var result = await bootstrap.DownloadAndVerifyAsync(Package);
        AssertTerminal(bootstrap, failures, result, UpdateState.Failed, UpdateFailureReason.FileIoError);
        Assert.Same(original, result.Exception);
    }

    [Fact]
    public async Task QueuedDispatcher_IsPreserved_AndCatchesSubscriberExceptionsWhenInvokedLater()
    {
        var dispatcher = new QueuedDispatcher();
        using var bootstrap = Create(dispatcher: dispatcher);
        var observed = 0;
        bootstrap.AddListenerUpdateCompleted += (_, _) => throw new InvalidOperationException("subscriber");
        bootstrap.AddListenerUpdateCompleted += (_, _) => observed++;

        Assert.True((await bootstrap.DownloadAndVerifyAsync(Package)).Success);
        Assert.Equal(0, observed);
        Assert.Single(dispatcher.Callbacks)();
        Assert.Equal(1, observed);
    }

    [Fact]
    public async Task ThrowingDispatcher_DoesNotFailOperation()
    {
        using var bootstrap = Create(dispatcher: new ThrowingDispatcher());
        bootstrap.AddListenerDownloadProgressChanged += (_, _) => { };
        bootstrap.AddListenerUpdateCompleted += (_, _) => { };
        Assert.True((await bootstrap.DownloadAndVerifyAsync(Package)).Success);
    }

    [Fact]
    public async Task ThrowingValidationSubscriber_DoesNotBlockOtherSubscribers()
    {
        using var http = new HttpClient(new MetadataHandler());
        using var bootstrap = Create(http: http);
        var observed = 0;
        bootstrap.AddListenerValidate += (_, _) => throw new InvalidOperationException("validation");
        bootstrap.AddListenerValidate += (_, _) => observed++;

        var result = await bootstrap.ValidateAsync("1.0.0");

        Assert.True(result.UpdateFound);
        Assert.Equal(UpdateState.UpdateAvailable, bootstrap.GetSnapshot().State);
        Assert.Equal(1, observed);
    }

    [Fact]
    public async Task ThrowingPrecheck_FailsClosedWithOriginalException_DespiteThrowingLoggerAndSubscriber()
    {
        using var http = new HttpClient(new MetadataHandler());
        using var bootstrap = Create(http: http, logger: new ThrowingLogger());
        var original = new InvalidOperationException("policy unavailable");
        var validateNotifications = 0;
        bootstrap.AddListenerUpdatePrecheck(_ => throw original);
        bootstrap.AddListenerValidate += (_, _) => validateNotifications++;
        bootstrap.AddListenerUpdateFailed += (_, _) => throw new InvalidOperationException("subscriber");
        var failures = ObserveFailures(bootstrap);

        var result = await bootstrap.ValidateAsync("1.0.0");

        AssertTerminal(bootstrap, failures, result, UpdateState.Failed, UpdateFailureReason.Unknown);
        Assert.False(result.UpdateFound);
        Assert.Same(original, result.Exception);
        Assert.Equal(0, validateNotifications);
        Assert.Equal("2.0.0", result.PackageInfo!.Version);
        bootstrap.AddListenerUpdatePrecheck(_ => false);
        Assert.True((await bootstrap.ValidateAsync("1.0.0").WaitAsync(Timeout)).UpdateFound);
        Assert.Equal(1, validateNotifications);
        Assert.Single(failures);
    }

    [Theory]
    [InlineData("http://updates.example/check", false)]
    [InlineData("http://updates.example/check", true)]
    [InlineData("https://user@updates.example/check", false)]
    [InlineData("https://user@updates.example/check", true)]
    public async Task RejectedAuthenticationTransport_IsTerminalInvalidMetadataBeforeProviderOrNetwork(
        string requestUrl, bool useJsonEndpoint)
    {
        var provider = new RecordingAuthProvider();
        var proxy = new BlockingRecordingProxy();
        using var bootstrap = new AndroidBootstrap(
            new SystemVersionComparer(), new Downloader(), new HashValidator(), new Installer(), new Storage(),
            updateServer: new UpdateServerOptions { RequestUrl = requestUrl, UseJsonEndpoint = useJsonEndpoint },
            httpOptions: new HttpDownloadOptions { AuthProvider = provider, UseProxy = true, Proxy = proxy });
        var failures = ObserveFailures(bootstrap);
        var validateNotifications = 0;
        var prechecks = 0;
        bootstrap.AddListenerValidate += (_, _) => validateNotifications++;
        bootstrap.AddListenerUpdatePrecheck(_ => { prechecks++; return false; });

        var result = await bootstrap.ValidateAsync("1.0.0").WaitAsync(Timeout);

        AssertTerminal(bootstrap, failures, result, UpdateState.Failed, UpdateFailureReason.InvalidMetadata);
        Assert.NotNull(result.Exception);
        Assert.Contains("Authentication requires", result.Exception.Message);
        Assert.False(result.UpdateFound);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, proxy.Calls);
        Assert.Equal(0, validateNotifications);
        Assert.Equal(0, prechecks);
    }

    [Fact]
    public async Task ShutdownCancellationCallbackException_DoesNotPreventDrain()
    {
        var started = Signal();
        var finish = Signal();
        var downloader = new Downloader
        {
            Run = async token =>
            {
                using var registration = token.Register(() => throw new InvalidOperationException("cancellation callback"));
                started.TrySetResult();
                await finish.Task;
                token.ThrowIfCancellationRequested();
                return Downloaded();
            }
        };
        var bootstrap = Create(downloader: downloader);
        var active = bootstrap.DownloadAndVerifyAsync(Package);
        await started.Task.WaitAsync(Timeout);
        bootstrap.Dispose();
        finish.TrySetResult();
        Assert.Equal(UpdateState.Canceled, (await active.WaitAsync(Timeout)).State);
        await bootstrap.DisposeAsync().AsTask().WaitAsync(Timeout);
        Assert.Equal(1, downloader.DisposeCount);
    }

    [Fact]
    public async Task DisposeDuringHashing_CancelsAndDrainsBeforeDisposingDownloader()
    {
        var started = Signal();
        var downloader = new Downloader();
        var hash = new HashValidator
        {
            Run = async token =>
            {
                started.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
                return new HashValidationResult { Success = true };
            }
        };
        var bootstrap = Create(downloader: downloader, hash: hash);
        var failures = ObserveFailures(bootstrap);
        var active = bootstrap.DownloadAndVerifyAsync(Package);
        await started.Task.WaitAsync(Timeout);

        var drained = bootstrap.DisposeAsync().AsTask();

        Assert.Equal(UpdateState.Canceled, (await active.WaitAsync(Timeout)).State);
        await drained.WaitAsync(Timeout);
        Assert.Single(failures);
        Assert.Equal(1, downloader.DisposeCount);
    }

    [Fact]
    public async Task DisposeDuringInstaller_CancelsAndDrains()
    {
        var started = Signal();
        var downloader = new Downloader();
        var installer = new Installer
        {
            Run = async token =>
            {
                started.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
                return new InstallResult { Success = true };
            }
        };
        var bootstrap = Create(downloader: downloader, installer: installer);
        var failures = ObserveFailures(bootstrap);
        var active = bootstrap.LaunchInstallerAsync(Package, "app.apk");
        await started.Task.WaitAsync(Timeout);

        bootstrap.Dispose();

        Assert.Equal(UpdateState.Canceled, (await active.WaitAsync(Timeout)).State);
        await bootstrap.DisposeAsync().AsTask().WaitAsync(Timeout);
        Assert.Single(failures);
        Assert.Equal(1, downloader.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentDisposeAndOperationAdmission_DoesNotRaceSemaphoreDisposal()
    {
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var downloader = new Downloader();
            var bootstrap = Create(downloader: downloader);
            var operations = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                try
                {
                    var result = await bootstrap.DownloadAndVerifyAsync(Package);
                    Assert.True(result.Success || result.State == UpdateState.Canceled);
                }
                catch (ObjectDisposedException ex)
                {
                    Assert.Contains(nameof(AndroidBootstrap), ex.ObjectName);
                }
                catch (OperationCanceledException)
                {
                    // An admitted gate waiter can be canceled before entering the operation.
                }
            })).ToArray();
            await Task.WhenAll(Task.Run(bootstrap.Dispose), Task.Run(bootstrap.Dispose));
            await Task.WhenAll(operations).WaitAsync(Timeout);
            await bootstrap.DisposeAsync().AsTask().WaitAsync(Timeout);
            Assert.Equal(1, downloader.DisposeCount);
        }
    }

    private static AndroidBootstrap Create(Downloader? downloader = null, HashValidator? hash = null,
        Storage? storage = null, Installer? installer = null, IUpdateEventDispatcher? dispatcher = null,
        IUpdateLogger? logger = null, HttpClient? http = null) =>
        new(new SystemVersionComparer(), downloader ?? new Downloader(), hash ?? new HashValidator(),
            installer ?? new Installer(), storage ?? new Storage(), dispatcher, logger,
            http is null ? null : new UpdateServerOptions { RequestUrl = "https://example.com/check", UseJsonEndpoint = true }, http);

    private static List<UpdateOperationResult> ObserveFailures(AndroidBootstrap bootstrap)
    {
        var results = new List<UpdateOperationResult>();
        bootstrap.AddListenerUpdateFailed += (_, args) => results.Add(args.Result);
        return results;
    }

    private static void AssertTerminal(AndroidBootstrap bootstrap, List<UpdateOperationResult> failures,
        UpdateOperationResult result, UpdateState state, UpdateFailureReason reason)
    {
        Assert.False(result.Success);
        Assert.Equal(state, result.State);
        Assert.Equal(reason, result.FailureReason);
        Assert.Equal(state, bootstrap.GetSnapshot().State);
        Assert.Equal(reason, bootstrap.GetSnapshot().FailureReason);
        Assert.Same(result, Assert.Single(failures));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static DownloadResult Downloaded() => new() { Success = true, FilePath = "app.apk", PackageInfo = Package };

    private sealed class Downloader : IUpdateDownloader, IDisposable
    {
        public Func<CancellationToken, Task<DownloadResult>> Run { get; init; } = _ => Task.FromResult(Downloaded());
        public int DisposeCount { get; private set; }
        public Task<DownloadResult> DownloadAsync(UpdatePackageInfo packageInfo, Action<DownloadProgressInfo>? progressCallback, CancellationToken cancellationToken = default)
        {
            progressCallback?.Invoke(new DownloadProgressInfo
            {
                PackageInfo = packageInfo, DownloadSpeedBytesPerSecond = 0, DownloadedBytes = 0,
                RemainingBytes = 10, TotalBytes = 10, ProgressPercentage = 0, StatusDescription = "starting"
            });
            return Run(cancellationToken);
        }
        public void Dispose() => DisposeCount++;
    }

    private sealed class HashValidator : IHashValidator
    {
        public Func<CancellationToken, Task<HashValidationResult>> Run { get; set; } =
            _ => Task.FromResult(new HashValidationResult { Success = true });
        public Task<HashValidationResult> ValidateSha256Async(string filePath, string expectedSha256, CancellationToken cancellationToken = default) => Run(cancellationToken);
    }

    private sealed class Installer : IApkInstaller
    {
        public Func<CancellationToken, Task<InstallResult>> Run { get; init; } =
            _ => Task.FromResult(new InstallResult { Success = true, State = UpdateState.Installing });
        public Task<InstallResult> LaunchInstallAsync(UpdatePackageInfo packageInfo, string apkFilePath, CancellationToken cancellationToken = default) => Run(cancellationToken);
    }

    private sealed class Storage : IFileStorage
    {
        public long Length { get; init; } = 10;
        public Exception? LengthError { get; set; }
        public Exception? DeleteError { get; init; }
        public int Deletes { get; private set; }
        public long GetFileLength(string path) => LengthError is null ? Length : throw LengthError;
        public void DeleteFile(string path)
        {
            Deletes++;
            if (DeleteError is not null) throw DeleteError;
        }
        public void EnsureDirectory(string path) { }
        public bool FileExists(string path) => true;
        public void MoveFile(string sourceFilePath, string destinationFilePath, bool overwrite) { }
        public Stream OpenRead(string filePath) => new MemoryStream();
        public Stream OpenWrite(string filePath, bool append) => new MemoryStream();
        public Task<string?> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowingLogger : IUpdateLogger
    {
        public void LogDebug(string message) { }
        public void LogInformation(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message, Exception? exception = null) => throw new InvalidOperationException("logger");
    }

    private sealed class RecordingAuthProvider : IHttpAuthProvider
    {
        public int Calls { get; private set; }
        public Task ApplyAuthAsync(HttpRequestMessage request, CancellationToken token = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingRecordingProxy : IWebProxy
    {
        public int Calls { get; private set; }
        public ICredentials? Credentials { get; set; }
        public Uri? GetProxy(Uri destination)
        {
            Calls++;
            throw new InvalidOperationException("Unexpected network dispatch.");
        }
        public bool IsBypassed(Uri host)
        {
            Calls++;
            throw new InvalidOperationException("Unexpected network dispatch.");
        }
    }

    private sealed class QueuedDispatcher : IUpdateEventDispatcher
    {
        public List<Action> Callbacks { get; } = [];
        public void Dispatch(Action callback) => Callbacks.Add(callback);
    }

    private sealed class ThrowingDispatcher : IUpdateEventDispatcher
    {
        public void Dispatch(Action callback) => throw new InvalidOperationException("dispatcher");
    }

    private sealed class MetadataHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"version":"2.0.0","downloadUrl":"https://example.com/app.apk","sha256":"{{Package.Sha256}}","fileSize":10,"fileName":"app.apk"}""")
            });
    }
}
