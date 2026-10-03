using GeneralUpdate.Avalonia.Android.Models;
using GeneralUpdate.Avalonia.Android.Services;
using Xunit;

namespace GeneralUpdate.Avalonia.Android.Tests;

public sealed class UpdatePreparationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepare_RespectsPrecheckButForcedUpdatesBypassIt(bool forced)
    {
        var package = TestBootstrap.Package with { IsForced = forced };
        var source = new DelegatePackageSource((version, _) =>
        {
            Assert.Equal("1.0.0", version);
            return Task.FromResult<UpdatePackageInfo?>(package);
        });
        var installerCalls = 0;
        using var bootstrap = TestBootstrap.Create(source: source, installer: new DelegateInstaller(_ =>
        {
            installerCalls++;
            throw new InvalidOperationException("Preparation must not launch an installer.");
        }));
        var prechecks = 0;
        bootstrap.AddListenerUpdatePrecheck(_ => { prechecks++; return true; });

        var result = await bootstrap.PrepareUpdateAsync("1.0.0");

        Assert.True(result.Success);
        Assert.Equal(forced, result.IsReadyToInstall);
        Assert.Equal(forced, result.UpdateFound);
        Assert.Equal(forced ? 0 : 1, prechecks);
        Assert.Equal(0, installerCalls);
        Assert.Equal(result.State, bootstrap.GetSnapshot().State);
    }

    [Fact]
    public async Task Prepare_NoPackage_DoesNotDownload()
    {
        using var bootstrap = TestBootstrap.Create(
            source: new DelegatePackageSource((_, _) => Task.FromResult<UpdatePackageInfo?>(null)),
            downloader: new DelegateDownloader(_ => throw new InvalidOperationException("Unexpected download.")));
        var result = await bootstrap.PrepareUpdateAsync("1.0.0");
        Assert.True(result.Success);
        Assert.False(result.UpdateFound);
        Assert.False(result.IsReadyToInstall);
        Assert.Null(result.FilePath);
    }

    [Fact]
    public async Task Prepare_MissingDownloadPath_IsAnExplicitFailure()
    {
        using var bootstrap = TestBootstrap.Create(downloader: new DelegateDownloader(_ =>
            Task.FromResult(new DownloadResult { Success = true })));
        var failures = 0;
        bootstrap.AddListenerUpdateFailed += (_, _) => failures++;
        var result = await bootstrap.PrepareUpdateAsync("1.0.0");
        Assert.True(result.UpdateFound);
        Assert.False(result.Success);
        Assert.False(result.IsReadyToInstall);
        Assert.Equal(UpdateFailureReason.FileIoError, result.FailureReason);
        Assert.Equal(UpdateState.Failed, bootstrap.GetSnapshot().State);
        Assert.Equal(1, failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepare_HashCancellation_ReturnsCanceledAndPublishesFailure(bool returnedResult)
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "hash cancellation fixture");
            using var cancellation = new CancellationTokenSource();
            var hash = new DelegateHashValidator(async ct =>
            {
                if (returnedResult)
                    return new HashValidationResult { FailureReason = UpdateFailureReason.Canceled };
                cancellation.Cancel();
                return await new Sha256HashValidator().ValidateSha256Async(file, TestBootstrap.Package.Sha256, ct);
            });
            using var bootstrap = TestBootstrap.Create(hash: hash, downloader: new DelegateDownloader(_ =>
                Task.FromResult(new DownloadResult { Success = true, FilePath = file })));
            var failures = 0;
            bootstrap.AddListenerUpdateFailed += (_, _) => failures++;
            var result = await bootstrap.PrepareUpdateAsync("1.0.0", cancellation.Token);
            Assert.False(result.Success);
            Assert.False(result.IsReadyToInstall);
            Assert.Equal(UpdateState.Canceled, result.State);
            Assert.Equal(UpdateFailureReason.Canceled, result.FailureReason);
            Assert.Equal(UpdateState.Canceled, bootstrap.GetSnapshot().State);
            Assert.Equal(1, failures);
            Assert.True(File.Exists(file));
            Assert.True((await bootstrap.ValidateAsync("1.0.0")).Success);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Prepare_HoldsOneLockAcrossQueryAndDownload()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queries = 0;
        using var bootstrap = TestBootstrap.Create(
            source: new DelegatePackageSource((_, _) =>
            {
                queries++;
                return Task.FromResult<UpdatePackageInfo?>(TestBootstrap.Package);
            }),
            downloader: new DelegateDownloader(async ct =>
            {
                started.SetResult();
                await finish.Task.WaitAsync(ct);
                return new DownloadResult { Success = true, FilePath = "test.apk" };
            }));
        var preparing = bootstrap.PrepareUpdateAsync("1.0.0");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var checking = bootstrap.ValidateAsync("1.0.0");
        Assert.False(checking.IsCompleted);
        Assert.Equal(1, queries);
        finish.SetResult();
        Assert.True((await preparing).IsReadyToInstall);
        Assert.True((await checking).Success);
        Assert.Equal(2, queries);
    }

    [Fact]
    public async Task Dispose_DefersCleanupAndRejectsQueuedOperations()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new DelegatePackageSource(async (_, ct) =>
        {
            started.SetResult();
            await finish.Task.WaitAsync(ct);
            return null;
        });
        var downloader = new DelegateDownloader(_ => throw new InvalidOperationException("Unexpected download."));
        using var bootstrap = TestBootstrap.Create(source: source, downloader: downloader);
        var active = bootstrap.ValidateAsync("1.0.0");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = bootstrap.ValidateAsync("1.0.0");
        bootstrap.Dispose();
        Assert.False(source.Disposed);
        Assert.False(downloader.Disposed);
        finish.SetResult();
        Assert.True((await active).Success);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        Assert.True(source.Disposed);
        Assert.True(downloader.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallerCancellation_IsNotConfusedWithAnUnrequestedTimeout(bool userCanceled)
    {
        using var cancellation = new CancellationTokenSource();
        using var bootstrap = TestBootstrap.Create(installer: new DelegateInstaller(_ =>
        {
            if (userCanceled) cancellation.Cancel();
            throw new OperationCanceledException();
        }));
        var failures = 0;
        bootstrap.AddListenerUpdateFailed += (_, _) => failures++;
        var result = await bootstrap.LaunchInstallerAsync(TestBootstrap.Package, "test.apk", cancellation.Token);
        Assert.False(result.Success);
        Assert.Equal(userCanceled ? UpdateState.Canceled : UpdateState.Failed, result.State);
        Assert.Equal(userCanceled ? UpdateFailureReason.Canceled : UpdateFailureReason.InstallLaunchFailed, result.FailureReason);
        Assert.Equal(result.State, bootstrap.GetSnapshot().State);
        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task Reset_RecoversCorruptJournalAndIsIdempotent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gu-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "installation.json");
            await File.WriteAllTextAsync(path, "{broken");
            using var bootstrap = TestBootstrap.Create(store: new JsonFileInstallationStore(path));
            Assert.False((await bootstrap.CheckInstallationAsync("1.0.0")).Success);
            Assert.True((await bootstrap.ResetInstallationAsync()).Success);
            Assert.True((await bootstrap.ResetInstallationAsync()).Success);
            Assert.False(File.Exists(path));
            Assert.Equal(UpdateState.None, (await bootstrap.CheckInstallationAsync("1.0.0")).State);
            Assert.True((await bootstrap.LaunchInstallerAsync(TestBootstrap.Package, "test.apk")).Success);
            Assert.True((await bootstrap.CheckInstallationAsync("2.0.0")).IsInstalled);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Reset_FailurePreservesRecordAndPublishesFailure()
    {
        var store = new MemoryInstallationStore
        {
            Record = new InstallationRecord { TargetVersion = "2.0.0", RequestedAt = DateTimeOffset.UtcNow },
            ClearError = new IOException("read-only")
        };
        using var bootstrap = TestBootstrap.Create(store: store);
        var failures = 0;
        bootstrap.AddListenerUpdateFailed += (_, _) => failures++;
        var result = await bootstrap.ResetInstallationAsync();
        Assert.False(result.Success);
        Assert.Equal(UpdateFailureReason.FileIoError, result.FailureReason);
        Assert.Equal(UpdateState.Failed, bootstrap.GetSnapshot().State);
        Assert.Equal(1, failures);
        Assert.NotNull(store.Record);
    }
}
