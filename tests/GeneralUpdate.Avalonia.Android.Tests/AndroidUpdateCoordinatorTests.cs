using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Enums;
using GeneralUpdate.Avalonia.Android.Events;
using GeneralUpdate.Avalonia.Android.Models;
using GeneralUpdate.Avalonia.Android.Services;
using Xunit;
using DownloadProgressChangedEventArgs = GeneralUpdate.Avalonia.Android.Events.DownloadProgressChangedEventArgs;

namespace GeneralUpdate.Avalonia.Android.Tests;

public sealed class AndroidUpdateCoordinatorTests
{
    private static UpdatePackageInfo Package(string version = "2.0") => new()
    {
        Version = version,
        DownloadUrl = "https://updates.example/app.apk?signature=private-query",
        Sha256 = new string('a', 64),
        AuthToken = "private-token",
        AuthSecretKey = "private-secret",
        BasicUsername = "private-user",
        BasicPassword = "private-password"
    };

    private static PendingUpdateAttempt Intent(PendingUpdatePhase phase = PendingUpdatePhase.IntentPersisted) => new()
    {
        AttemptId = Guid.NewGuid(),
        OriginalVersion = "1.0",
        TargetVersion = "2.0",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Phase = phase
    };

    [Fact]
    public async Task RunUsesRealBootstrapStagesAndPersistsBeforeInstaller()
    {
        var calls = new List<string>();
        var store = new MemoryStore { OnWrite = _ => calls.Add("persist") };
        using var http = new HttpClient(new MetadataHandler(() =>
        {
            calls.Add("check");
            return Package();
        }));
        using var bootstrap = new AndroidBootstrap(new SystemVersionComparer(),
            new RecordingDownloader(calls), new RecordingValidator(calls), new RecordingInstaller(calls, store),
            new PhysicalFileStorage(), updateServer: Server(), httpClient: http);
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);
        var stages = new List<UpdateCoordinatorStage>();
        coordinator.StateChanged += (_, args) => stages.Add(args.Result.Stage);

        var result = await coordinator.RunAsync("1.0");

        Assert.Equal(UpdateCoordinatorOutcome.InstallerLaunched, result.Outcome);
        Assert.Equal(new[] { "check", "download", "verify", "persist", "install", "persist" }, calls);
        Assert.Equal(new[]
        {
            UpdateCoordinatorStage.ReadingPending, UpdateCoordinatorStage.Checking,
            UpdateCoordinatorStage.DownloadingAndVerifying, UpdateCoordinatorStage.PersistingIntent,
            UpdateCoordinatorStage.LaunchingInstaller, UpdateCoordinatorStage.Finished
        }, stages);
        Assert.Equal(PendingUpdatePhase.InstallerLaunched, store.Pending!.Phase);
        Assert.NotEqual(UpdateCoordinatorOutcome.Updated, result.Outcome);
    }

    [Fact]
    public async Task RealTransportHashAndDurableStoreCompleteHandoffThenRecreatedCoordinatorConfirmsInstalledVersion()
    {
        using var directory = new TestDirectory();
        var calls = new List<string>();
        byte[] apk = [1, 2, 3, 4, 5];
        var package = Package() with { Sha256 = Convert.ToHexString(SHA256.HashData(apk)), FileSize = apk.Length };
        using var http = new HttpClient(new PackageHandler(package, apk, calls));
        var pendingPath = Path.Combine(directory.Path, "pending.json");
        var store = new JsonPendingUpdateStore(pendingPath);
        var storage = new PhysicalFileStorage();
        using var downloader = new HttpResumableApkDownloader(http, storage,
            new AndroidUpdateOptions { DownloadDirectoryPath = directory.Path });
        using var bootstrap = new AndroidBootstrap(new SystemVersionComparer(),
            new TrackingDownloader(downloader, calls), new TrackingValidator(new Sha256HashValidator(), calls),
            new RecordingInstaller(calls, store), storage, updateServer: Server(), httpClient: http);
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);
        var receivedProgress = 0;
        coordinator.AddListenerDownloadProgressChanged += (_, _) => throw new InvalidOperationException("progress listener");
        coordinator.AddListenerDownloadProgressChanged += (_, args) =>
        {
            if (args.DownloadedBytes == apk.Length) receivedProgress++;
        };
        var result = await coordinator.RunAsync("1.0");
        Assert.Equal(UpdateCoordinatorOutcome.InstallerLaunched, result.Outcome);
        Assert.True(receivedProgress > 0);
        Assert.Equal(new[] { "check", "download", "verify", "install" }, calls);
        Assert.Equal(PendingUpdatePhase.InstallerLaunched, (await store.ReadAsync())!.Phase);
        Assert.Equal(UpdateCoordinatorOutcome.AwaitingInstallation, (await coordinator.ReconcileAsync("1.0")).Outcome);
        var json = await File.ReadAllTextAsync(pendingPath);
        foreach (var forbidden in new[] { "private-", "https:", "DownloadUrl", "Auth", "Password", "FilePath", "apk", "Exception" })
            Assert.DoesNotContain(forbidden, json);
        await coordinator.DisposeAsync();
        await bootstrap.DisposeAsync();

        var recreatedStore = new JsonPendingUpdateStore(pendingPath);
        using var recreatedDownloader = new HttpResumableApkDownloader(http, storage,
            new AndroidUpdateOptions { DownloadDirectoryPath = directory.Path });
        using var recreatedBootstrap = new AndroidBootstrap(new SystemVersionComparer(),
            new TrackingDownloader(recreatedDownloader, calls), new TrackingValidator(new Sha256HashValidator(), calls),
            new RecordingInstaller(calls, recreatedStore), storage, updateServer: Server(), httpClient: http);
        await using var recreatedCoordinator = new AndroidUpdateCoordinator(recreatedBootstrap, recreatedStore);
        Assert.Equal(UpdateCoordinatorOutcome.Updated, (await recreatedCoordinator.ReconcileAsync("2.0")).Outcome);
        Assert.Equal(UpdateCoordinatorOutcome.NoPendingUpdate, (await recreatedCoordinator.ReconcileAsync("2.0")).Outcome);
        Assert.False(File.Exists(pendingPath));
        Assert.Equal(new[] { "check", "download", "verify", "install" }, calls);
    }

    [Fact]
    public async Task PersistedPrelaunchIntentSurvivesRestartAsUncertainWithoutAutomaticallyLaunching()
    {
        using var directory = new TestDirectory();
        var pendingPath = Path.Combine(directory.Path, "pending.json");
        var beforeInterruption = new JsonPendingUpdateStore(pendingPath);
        var intent = Intent();
        await beforeInterruption.WriteAsync(intent);

        var bootstrap = new StubBootstrap();
        var afterInterruption = new JsonPendingUpdateStore(pendingPath);
        await using var recreated = new AndroidUpdateCoordinator(bootstrap, afterInterruption);
        var result = await recreated.ReconcileAsync("1.0");
        Assert.Equal(UpdateCoordinatorOutcome.AwaitingInstallation, result.Outcome);
        Assert.Equal(PendingUpdatePhase.IntentPersisted, result.PendingUpdate!.Phase);
        Assert.Equal(intent, await afterInterruption.ReadAsync());
        Assert.Equal(UpdateCoordinatorOutcome.PendingUpdateExists, (await recreated.RunAsync("1.0")).Outcome);
        Assert.Empty(bootstrap.Calls);
    }

    [Theory]
    [InlineData("2.0", UpdateCoordinatorOutcome.Updated)]
    [InlineData("3.0", UpdateCoordinatorOutcome.Updated)]
    [InlineData("1.0", UpdateCoordinatorOutcome.AwaitingInstallation)]
    [InlineData("1.5", UpdateCoordinatorOutcome.AwaitingInstallation)]
    public async Task RecreatedCoordinatorReconcilesActualVersion(string actualVersion, UpdateCoordinatorOutcome expected)
    {
        using var directory = new TestDirectory();
        var file = Path.Combine(directory.Path, "pending.json");
        using (var first = new AndroidUpdateCoordinator(new StubBootstrap(), new JsonPendingUpdateStore(file)))
            Assert.Equal(UpdateCoordinatorOutcome.InstallerLaunched, (await first.RunAsync("1.0")).Outcome);

        var bootstrap = new StubBootstrap();
        await using var recreated = new AndroidUpdateCoordinator(bootstrap, new JsonPendingUpdateStore(file));
        var result = await recreated.ReconcileAsync(actualVersion);
        Assert.Equal(expected, result.Outcome);
        Assert.Empty(bootstrap.Calls);
        Assert.Equal(expected != UpdateCoordinatorOutcome.Updated, File.Exists(file));
        if (expected == UpdateCoordinatorOutcome.Updated)
            Assert.Equal(UpdateCoordinatorOutcome.NoPendingUpdate, (await recreated.ReconcileAsync(actualVersion)).Outcome);
    }

    [Theory]
    [InlineData(false, UpdateCoordinatorOutcome.NoUpdate)]
    [InlineData(true, UpdateCoordinatorOutcome.Skipped)]
    public async Task NoUpdateAndRealPrecheckDoNotDownload(bool skip, UpdateCoordinatorOutcome expected)
    {
        var calls = new List<string>();
        var store = new MemoryStore();
        using var http = new HttpClient(new MetadataHandler(() => skip ? Package() : null));
        using var bootstrap = new AndroidBootstrap(new SystemVersionComparer(),
            new RecordingDownloader(calls), new RecordingValidator(calls), new RecordingInstaller(calls, store),
            new PhysicalFileStorage(), updateServer: Server(), httpClient: http);
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);
        Assert.Same(coordinator, coordinator.AddListenerUpdatePrecheck(_ => skip));

        Assert.Equal(expected, (await coordinator.RunAsync("1.0")).Outcome);
        Assert.Empty(calls);
        Assert.Null(store.Pending);
    }

    [Theory]
    [InlineData(false, UpdateCoordinatorOutcome.Failed)]
    [InlineData(true, UpdateCoordinatorOutcome.InstallerLaunched)]
    public async Task CoordinatorPrecheckPreservesPolicyFailureAndForcedUpdateSemantics(bool forced, UpdateCoordinatorOutcome expected)
    {
        var calls = new List<string>();
        var store = new MemoryStore();
        using var http = new HttpClient(new MetadataHandler(() => Package() with { IsForced = forced }));
        using var bootstrap = new AndroidBootstrap(new SystemVersionComparer(),
            new RecordingDownloader(calls), new RecordingValidator(calls), new RecordingInstaller(calls, store),
            new PhysicalFileStorage(), updateServer: Server(), httpClient: http);
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);
        var prechecks = 0;
        coordinator.AddListenerUpdatePrecheck(_ =>
        {
            prechecks++;
            throw new InvalidOperationException("policy failed");
        });
        Assert.Equal(expected, (await coordinator.RunAsync("1.0")).Outcome);
        Assert.Equal(forced ? 0 : 1, prechecks);
    }

    [Theory]
    [InlineData(UpdateFailureReason.InstallPermissionDenied)]
    [InlineData(UpdateFailureReason.InstallLaunchFailed)]
    public async Task FailedHandoffIsRetryable(UpdateFailureReason reason)
    {
        var bootstrap = new StubBootstrap
        {
            Install = _ => Task.FromResult(new InstallResult { FailureReason = reason, State = UpdateState.Failed })
        };
        var store = new MemoryStore();
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);
        var result = await coordinator.RunAsync("1.0");
        Assert.Equal(UpdateCoordinatorOutcome.Failed, result.Outcome);
        Assert.Equal(reason, result.FailureReason);
        Assert.Equal(PendingUpdatePhase.Retryable, store.Pending!.Phase);
        Assert.Equal(UpdateCoordinatorOutcome.RecoveryRequired, (await coordinator.ReconcileAsync("1.0")).Outcome);
    }

    [Fact]
    public async Task FailedIntentWritePreventsLaunch()
    {
        var store = new MemoryStore { FailWriteNumber = 1 };
        var bootstrap = new StubBootstrap();
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);
        var result = await coordinator.RunAsync("1.0");
        Assert.Equal(UpdateCoordinatorOutcome.Failed, result.Outcome);
        Assert.Equal(UpdateFailureReason.FileIoError, result.FailureReason);
        Assert.DoesNotContain("install", bootstrap.Calls);
        Assert.Null(store.Pending);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedPostHandoffWriteRetainsUncertainIntent(bool launchSucceeds)
    {
        var store = new MemoryStore { FailWriteNumber = 2 };
        var bootstrap = new StubBootstrap
        {
            Install = _ => Task.FromResult(new InstallResult
            {
                Success = launchSucceeds,
                FailureReason = launchSucceeds ? UpdateFailureReason.None : UpdateFailureReason.InstallLaunchFailed
            })
        };
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);
        var result = await coordinator.RunAsync("1.0");
        Assert.Equal(launchSucceeds ? UpdateCoordinatorOutcome.RecoveryRequired : UpdateCoordinatorOutcome.Failed, result.Outcome);
        Assert.Equal(PendingUpdatePhase.IntentPersisted, store.Pending!.Phase);
        Assert.Equal(UpdateCoordinatorOutcome.AwaitingInstallation, (await coordinator.ReconcileAsync("1.0")).Outcome);
    }

    [Fact]
    public async Task ThrowingInstallerLeavesUncertainIntentInsteadOfClaimingFailureWasSafe()
    {
        var store = new MemoryStore();
        var bootstrap = new StubBootstrap { Install = _ => throw new InvalidOperationException("unknown external outcome") };
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);
        Assert.Equal(UpdateCoordinatorOutcome.RecoveryRequired, (await coordinator.RunAsync("1.0")).Outcome);
        Assert.Equal(PendingUpdatePhase.IntentPersisted, store.Pending!.Phase);
    }

    [Fact]
    public async Task ExistingPendingCannotBeOverwrittenByRun()
    {
        var pending = Intent();
        var store = new MemoryStore { Pending = pending };
        var bootstrap = new StubBootstrap();
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);
        Assert.Equal(UpdateCoordinatorOutcome.PendingUpdateExists, (await coordinator.RunAsync("1.0")).Outcome);
        Assert.Same(pending, store.Pending);
        Assert.Empty(bootstrap.Calls);
    }

    [Fact]
    public async Task FailedAcknowledgementRetainsRecordAndCanBeReconciledAgain()
    {
        var pending = Intent();
        var store = new MemoryStore { Pending = pending, FailClear = true };
        await using var coordinator = new AndroidUpdateCoordinator(new StubBootstrap(), store);
        var failed = await coordinator.ReconcileAsync("2.0");
        Assert.Equal(UpdateCoordinatorOutcome.Failed, failed.Outcome);
        Assert.Equal(UpdateFailureReason.FileIoError, failed.FailureReason);
        Assert.Same(pending, store.Pending);
        store.FailClear = false;
        Assert.Equal(UpdateCoordinatorOutcome.Updated, (await coordinator.ReconcileAsync("2.0")).Outcome);
        Assert.Null(store.Pending);
    }

    [Theory]
    [InlineData("not a version", "2.0")]
    [InlineData("1.0", "not a version")]
    public async Task InvalidVersionFailsExplicitlyAndRetainsPending(string actual, string target)
    {
        var store = new MemoryStore { Pending = Intent() with { TargetVersion = target } };
        await using var coordinator = new AndroidUpdateCoordinator(new StubBootstrap(), store);
        var result = await coordinator.ReconcileAsync(actual);
        Assert.Equal(UpdateCoordinatorOutcome.Failed, result.Outcome);
        Assert.NotEqual(UpdateFailureReason.None, result.FailureReason);
        Assert.NotNull(store.Pending);
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("02.0")]
    public async Task RetryRequeriesAndReverifiesUsingOnlyFreshPath(string equivalentTarget)
    {
        var store = new MemoryStore { Pending = Intent(PendingUpdatePhase.Retryable) };
        var previousId = store.Pending.AttemptId;
        var bootstrap = new StubBootstrap { Available = Package(equivalentTarget), PreparedPath = "newly-verified.apk" };
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);
        var result = await coordinator.RetryAsync("1.0");
        Assert.Equal(UpdateCoordinatorOutcome.InstallerLaunched, result.Outcome);
        Assert.Equal(new[] { "check", "download-verify", "install" }, bootstrap.Calls);
        Assert.Equal("newly-verified.apk", bootstrap.InstalledPath);
        Assert.Equal(equivalentTarget, store.Pending!.TargetVersion);
        Assert.NotEqual(previousId, store.Pending.AttemptId);
    }

    [Theory]
    [InlineData("3.0", "2.0")]
    [InlineData("3.0", "3.0")]
    [InlineData("1.5", "2.0")]
    public async Task RetryRefusesDifferentDiscoveredTargetBeforeDownloadingAndPreservesEarlierHandoff(
        string discoveredVersion, string eventuallyInstalledVersion)
    {
        var original = Intent(PendingUpdatePhase.InstallerLaunched);
        var store = new MemoryStore { Pending = original };
        var bootstrap = new StubBootstrap { Available = Package(discoveredVersion) };
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);

        var result = await coordinator.RetryAsync("1.0");

        Assert.Equal(UpdateCoordinatorOutcome.RecoveryRequired, result.Outcome);
        Assert.Contains("different target", result.Message);
        Assert.Contains("abandon", result.Message);
        Assert.Equal(new[] { "check" }, bootstrap.Calls);
        Assert.Equal(0, store.Writes);
        Assert.Same(original, store.Pending);
        Assert.Same(original, result.PendingUpdate);
        Assert.Equal(UpdateCoordinatorOutcome.Updated, (await coordinator.ReconcileAsync(eventuallyInstalledVersion)).Outcome);
        Assert.Null(store.Pending);
    }

    [Theory]
    [InlineData(UpdateFailureReason.Canceled)]
    [InlineData(UpdateFailureReason.InstallPermissionDenied)]
    public async Task FailedSameTargetRetryKeepsEarlierHandoffTargetReconcilable(UpdateFailureReason failure)
    {
        var store = new MemoryStore { Pending = Intent(PendingUpdatePhase.InstallerLaunched) };
        var bootstrap = new StubBootstrap
        {
            Install = _ => Task.FromResult(new InstallResult
            {
                State = failure == UpdateFailureReason.Canceled ? UpdateState.Canceled : UpdateState.Failed,
                FailureReason = failure
            })
        };
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);
        var result = await coordinator.RetryAsync("1.0");
        Assert.Equal(failure, result.FailureReason);
        Assert.Equal("2.0", store.Pending!.TargetVersion);
        Assert.Equal(UpdateCoordinatorOutcome.Updated, (await coordinator.ReconcileAsync("2.0")).Outcome);
        Assert.Null(store.Pending);
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("3.0")]
    public async Task RetryDoesNotInstallIfTargetAlreadyInstalled(string current)
    {
        var bootstrap = new StubBootstrap();
        var store = new MemoryStore { Pending = Intent() };
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);
        Assert.Equal(UpdateCoordinatorOutcome.Updated, (await coordinator.RetryAsync(current)).Outcome);
        Assert.Empty(bootstrap.Calls);
        Assert.Null(store.Pending);
    }

    [Fact]
    public async Task RetryWithNoPendingDoesNotStartAnUpdate()
    {
        var bootstrap = new StubBootstrap();
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, new MemoryStore());
        Assert.Equal(UpdateCoordinatorOutcome.NoPendingUpdate, (await coordinator.RetryAsync("1.0")).Outcome);
        Assert.Empty(bootstrap.Calls);
    }

    [Fact]
    public async Task RetryFailureRetainsOriginalAttempt()
    {
        var pending = Intent();
        var store = new MemoryStore { Pending = pending };
        var bootstrap = new StubBootstrap
        {
            Download = _ => Task.FromResult(new UpdateOperationResult { FailureReason = UpdateFailureReason.HashMismatch })
        };
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);
        Assert.Equal(UpdateFailureReason.HashMismatch, (await coordinator.RetryAsync("1.0")).FailureReason);
        Assert.Same(pending, store.Pending);
        Assert.DoesNotContain("install", bootstrap.Calls);
    }

    [Fact]
    public async Task AbandonForgetsTrackingButLeavesApkIntact()
    {
        using var directory = new TestDirectory();
        var apk = Path.Combine(directory.Path, "app.apk");
        await File.WriteAllTextAsync(apk, "apk");
        var store = new MemoryStore { Pending = Intent() };
        var bootstrap = new StubBootstrap();
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);
        var result = await coordinator.AbandonAsync();
        Assert.Equal(UpdateCoordinatorOutcome.Abandoned, result.Outcome);
        Assert.Contains("not canceled", result.Message);
        Assert.True(File.Exists(apk));
        Assert.Null(store.Pending);
        Assert.Empty(bootstrap.Calls);
    }

    [Fact]
    public async Task FailedAbandonRetainsPending()
    {
        var pending = Intent();
        var store = new MemoryStore { Pending = pending, FailClear = true };
        await using var coordinator = new AndroidUpdateCoordinator(new StubBootstrap(), store);
        Assert.Equal(UpdateFailureReason.FileIoError, (await coordinator.AbandonAsync()).FailureReason);
        Assert.Same(pending, store.Pending);
    }

    [Fact]
    public async Task CancellationBeforeStartIsTypedAndDoesNotTouchStore()
    {
        var store = new MemoryStore();
        var bootstrap = new StubBootstrap();
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);
        var result = await coordinator.RunAsync("1.0", new CancellationToken(true));
        Assert.Equal(UpdateCoordinatorOutcome.Canceled, result.Outcome);
        Assert.Equal(0, store.Reads);
        Assert.Empty(bootstrap.Calls);
    }

    [Fact]
    public async Task CancellationDuringDownloadDoesNotPersistOrLaunch()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var store = new MemoryStore();
        var bootstrap = new StubBootstrap
        {
            Download = async token =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new UpdateOperationResult();
            }
        };
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);
        var running = coordinator.RunAsync("1.0", cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        Assert.Equal(UpdateCoordinatorOutcome.Canceled, (await running).Outcome);
        Assert.Null(store.Pending);
        Assert.DoesNotContain("install", bootstrap.Calls);
    }

    [Fact]
    public async Task CancellationAfterPersistenceRetainsRetryableIntentAndDoesNotLaunch()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new MemoryStore();
        var bootstrap = new StubBootstrap();
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);
        coordinator.StateChanged += (_, args) =>
        {
            if (args.Result.Stage == UpdateCoordinatorStage.LaunchingInstaller) cancellation.Cancel();
        };
        Assert.Equal(UpdateCoordinatorOutcome.Canceled, (await coordinator.RunAsync("1.0", cancellation.Token)).Outcome);
        Assert.Equal(PendingUpdatePhase.Retryable, store.Pending!.Phase);
        Assert.DoesNotContain("install", bootstrap.Calls);
    }

    [Fact]
    public async Task SuccessfulExternalHandoffWinsOverLateCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new MemoryStore();
        var bootstrap = new StubBootstrap
        {
            Install = _ =>
            {
                cancellation.Cancel();
                return Task.FromResult(new InstallResult { Success = true });
            }
        };
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);
        Assert.Equal(UpdateCoordinatorOutcome.InstallerLaunched, (await coordinator.RunAsync("1.0", cancellation.Token)).Outcome);
        Assert.Equal(PendingUpdatePhase.InstallerLaunched, store.Pending!.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationInsideExternalInstallerRetainsUncertainIntent(bool throws)
    {
        using var cancellation = new CancellationTokenSource();
        var store = new MemoryStore();
        var bootstrap = new StubBootstrap
        {
            Install = token =>
            {
                cancellation.Cancel();
                if (throws) throw new OperationCanceledException(token);
                return Task.FromResult(new InstallResult
                {
                    State = UpdateState.Canceled,
                    FailureReason = UpdateFailureReason.Canceled
                });
            }
        };
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);
        Assert.Equal(UpdateCoordinatorOutcome.Canceled, (await coordinator.RunAsync("1.0", cancellation.Token)).Outcome);
        Assert.Equal(PendingUpdatePhase.IntentPersisted, store.Pending!.Phase);
        Assert.Equal(UpdateCoordinatorOutcome.AwaitingInstallation, (await coordinator.ReconcileAsync("1.0")).Outcome);
    }

    [Fact]
    public async Task CanceledWaiterDoesNotAffectActiveAttemptAndWholeRunsAreSerialized()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new MemoryStore();
        var bootstrap = new StubBootstrap
        {
            Download = async _ =>
            {
                entered.SetResult();
                await release.Task;
                return new UpdateOperationResult { Success = true, FilePath = "verified.apk" };
            }
        };
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, store);
        var active = coordinator.RunAsync("1.0");
        await entered.Task;
        using var cancellation = new CancellationTokenSource();
        var waiter = coordinator.RunAsync("1.0", cancellation.Token);
        var next = coordinator.RunAsync("1.0");
        cancellation.Cancel();
        Assert.Equal(UpdateCoordinatorOutcome.Canceled, (await waiter).Outcome);
        Assert.Equal(1, store.Reads);
        Assert.False(active.IsCompleted);
        release.SetResult();
        Assert.Equal(UpdateCoordinatorOutcome.InstallerLaunched, (await active).Outcome);
        Assert.Equal(UpdateCoordinatorOutcome.PendingUpdateExists, (await next).Outcome);
        Assert.Equal(new[] { "check", "download-verify", "install" }, bootstrap.Calls);
    }

    [Fact]
    public async Task SeparateCoordinatorsShareWorkflowLeaseAndCanceledLeaseWaitersDoNotTouchIntent()
    {
        using var directory = new TestDirectory();
        var file = Path.Combine(directory.Path, "pending.json");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstBootstrap = new StubBootstrap
        {
            Download = async _ =>
            {
                entered.SetResult();
                await release.Task;
                return new UpdateOperationResult { Success = true, FilePath = "verified.apk" };
            }
        };
        var secondBootstrap = new StubBootstrap();
        await using var first = new AndroidUpdateCoordinator(firstBootstrap, new JsonPendingUpdateStore(file));
        await using var second = new AndroidUpdateCoordinator(secondBootstrap, new JsonPendingUpdateStore(file));
        await using var third = new AndroidUpdateCoordinator(new StubBootstrap(), new JsonPendingUpdateStore(file));
        var active = first.RunAsync("1.0");
        await entered.Task;
        using var cancellation = new CancellationTokenSource();
        var canceledWaiter = third.RunAsync("1.0", cancellation.Token);
        var competing = second.RunAsync("1.0");
        cancellation.Cancel();
        UpdateCoordinatorResult canceled;
        try
        {
            canceled = await canceledWaiter.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.SetResult();
        }
        Assert.Equal(UpdateCoordinatorOutcome.Canceled, canceled.Outcome);
        Assert.Equal(UpdateCoordinatorOutcome.InstallerLaunched, (await active).Outcome);
        Assert.Equal(UpdateCoordinatorOutcome.PendingUpdateExists, (await competing).Outcome);
        Assert.Empty(secondBootstrap.Calls);
        Assert.Equal(PendingUpdatePhase.InstallerLaunched, (await new JsonPendingUpdateStore(file).ReadAsync())!.Phase);
    }

    [Fact]
    public async Task FileLeaseIsExclusiveAndCanBeReacquiredAfterReleaseWithoutDeletingLockFile()
    {
        using var directory = new TestDirectory();
        var file = Path.Combine(directory.Path, "pending.json");
        var firstStore = new JsonPendingUpdateStore(file);
        var secondStore = new JsonPendingUpdateStore(file);
        await using (await firstStore.AcquireLeaseAsync())
        {
            Assert.Throws<IOException>(() =>
            {
                using var competingFile = new FileStream(file + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            });
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondStore.AcquireLeaseAsync(cancellation.Token).AsTask());
        }
        Assert.True(File.Exists(file + ".lock"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reacquired = await secondStore.AcquireLeaseAsync(timeout.Token);
    }

    [Fact]
    public async Task ListenerAndDispatcherFailuresCannotChangeFlow()
    {
        var dispatcher = new RecordingDispatcher();
        await using var coordinator = new AndroidUpdateCoordinator(new StubBootstrap(), new MemoryStore(), eventDispatcher: dispatcher);
        var received = 0;
        coordinator.StateChanged += (_, _) => throw new InvalidOperationException("listener");
        coordinator.StateChanged += (_, _) => received++;
        Assert.Equal(UpdateCoordinatorOutcome.InstallerLaunched, (await coordinator.RunAsync("1.0")).Outcome);
        Assert.Equal(6, received);
        Assert.Equal(6, dispatcher.Calls);
        dispatcher.Throw = true;
        Assert.Equal(UpdateCoordinatorOutcome.Updated, (await coordinator.ReconcileAsync("2.0")).Outcome);
    }

    [Fact]
    public async Task ProgressUsesCoordinatorDispatcherAndDetachesOnNonOwningDisposal()
    {
        var bootstrap = new StubBootstrap();
        var dispatcher = new RecordingDispatcher();
        var coordinator = new AndroidUpdateCoordinator(bootstrap, new MemoryStore(), eventDispatcher: dispatcher);
        var received = 0;
        coordinator.AddListenerDownloadProgressChanged += (_, _) => throw new InvalidOperationException("progress listener");
        coordinator.AddListenerDownloadProgressChanged += (_, _) => received++;
        bootstrap.RaiseProgress();
        Assert.Equal(1, dispatcher.Calls);
        Assert.Equal(1, received);
        await coordinator.DisposeAsync();
        bootstrap.RaiseProgress();
        Assert.Equal(1, received);
        Assert.False(bootstrap.Disposed);
        Assert.Throws<ObjectDisposedException>(() => coordinator.AddListenerUpdatePrecheck(_ => false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalFromCallbackIsNonblockingAndOwnershipIsExplicit(bool ownsBootstrap)
    {
        var bootstrap = new StubBootstrap();
        var coordinator = new AndroidUpdateCoordinator(bootstrap, new MemoryStore(), ownsBootstrap: ownsBootstrap);
        coordinator.StateChanged += (_, args) =>
        {
            if (args.Result.Stage == UpdateCoordinatorStage.Checking) coordinator.Dispose();
        };
        Assert.Equal(UpdateCoordinatorOutcome.Canceled, (await coordinator.RunAsync("1.0")).Outcome);
        await coordinator.DisposeAsync();
        Assert.Equal(ownsBootstrap, bootstrap.Disposed);
        Assert.Empty(bootstrap.Calls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.RunAsync("1.0"));
    }

    [Fact]
    public async Task DisposeWaitsForActiveOperationBeforeReleasingOwnedBootstrap()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bootstrap = new StubBootstrap
        {
            Download = async _ =>
            {
                entered.SetResult();
                await release.Task;
                return new UpdateOperationResult { Success = true, FilePath = "verified.apk" };
            }
        };
        var coordinator = new AndroidUpdateCoordinator(bootstrap, new MemoryStore(), ownsBootstrap: true);
        var active = coordinator.RunAsync("1.0");
        await entered.Task;
        var waiting = coordinator.RunAsync("1.0");
        var disposing = coordinator.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        Assert.False(bootstrap.Disposed);
        Assert.Equal(UpdateCoordinatorOutcome.Canceled, (await waiting).Outcome);
        release.SetResult();
        Assert.Equal(UpdateCoordinatorOutcome.Canceled, (await active).Outcome);
        await disposing;
        Assert.True(bootstrap.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeDoesNotWaitForBlockingCancellationCallbacksAndIsolatesTheirExceptions(bool callbackThrows)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackRelease = new ManualResetEventSlim();
        var bootstrap = new StubBootstrap
        {
            Download = async token =>
            {
                using var registration = token.Register(() =>
                {
                    callbackEntered.TrySetResult();
                    callbackRelease.Wait();
                    if (callbackThrows) throw new InvalidOperationException("cancellation callback failed");
                });
                started.TrySetResult();
                await finish.Task;
                token.ThrowIfCancellationRequested();
                return new UpdateOperationResult { Success = true, FilePath = "verified.apk" };
            }
        };
        var coordinator = new AndroidUpdateCoordinator(bootstrap, new MemoryStore(), ownsBootstrap: true);
        var active = coordinator.RunAsync("1.0");
        await started.Task;
        var synchronousDispose = Task.Run(coordinator.Dispose);
        try
        {
            await synchronousDispose.WaitAsync(TimeSpan.FromSeconds(5));
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(bootstrap.Disposed);
            Assert.False(coordinator.DisposeAsync().IsCompleted);
        }
        finally
        {
            callbackRelease.Set();
            finish.TrySetResult();
            await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(UpdateCoordinatorOutcome.Canceled, (await active).Outcome);
        Assert.True(bootstrap.Disposed);
    }

    [Fact]
    public async Task JsonContainsOnlySafeMetadataAndNoCredentialsUrlPathOrExceptions()
    {
        using var directory = new TestDirectory();
        var file = Path.Combine(directory.Path, "pending.json");
        await using var coordinator = new AndroidUpdateCoordinator(new StubBootstrap(), new JsonPendingUpdateStore(file));
        await coordinator.RunAsync("1.0");
        var json = await File.ReadAllTextAsync(file);
        foreach (var forbidden in new[] { "private-", "https:", "DownloadUrl", "Auth", "Password", "FilePath", "apk", "Exception" })
            Assert.DoesNotContain(forbidden, json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "AttemptId", "CreatedAtUtc", "OriginalVersion", "Phase", "SchemaVersion", "TargetVersion" },
            document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"SchemaVersion\":99}")]
    public async Task CorruptStateFailsClosedWithoutLaunchingAndCanBeExplicitlyAbandoned(string json)
    {
        using var directory = new TestDirectory();
        var file = Path.Combine(directory.Path, "pending.json");
        await File.WriteAllTextAsync(file, json);
        var bootstrap = new StubBootstrap();
        await using var coordinator = new AndroidUpdateCoordinator(bootstrap, new JsonPendingUpdateStore(file));
        foreach (var result in new[]
        {
            await coordinator.RunAsync("1.0"), await coordinator.ReconcileAsync("1.0"), await coordinator.RetryAsync("1.0")
        })
        {
            Assert.Equal(UpdateCoordinatorOutcome.Failed, result.Outcome);
            Assert.Equal(UpdateFailureReason.InvalidMetadata, result.FailureReason);
        }
        Assert.Empty(bootstrap.Calls);
        Assert.Equal(json, await File.ReadAllTextAsync(file));
        Assert.Equal(UpdateCoordinatorOutcome.Abandoned, (await coordinator.AbandonAsync()).Outcome);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task UnsupportedSchemaMissingSchemaAndPersistedPathAreRejected()
    {
        using var directory = new TestDirectory();
        var file = Path.Combine(directory.Path, "pending.json");
        var valid = JsonSerializer.Serialize(Intent());
        var invalidRecords = new[]
        {
            valid.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":99"),
            valid.Replace("\"SchemaVersion\":1,", ""),
            valid.Insert(1, "\"SchemaVersion\":99,"),
            valid.Insert(1, "\"FilePath\":\"untrusted.apk\",")
        };
        foreach (var json in invalidRecords)
        {
            await File.WriteAllTextAsync(file, json);
            await Assert.ThrowsAsync<InvalidDataException>(() => new JsonPendingUpdateStore(file).ReadAsync());
        }
    }

    [Fact]
    public async Task CanceledOrInvalidWritesPreservePreviousRecordWithoutTemporaryFiles()
    {
        using var directory = new TestDirectory();
        var file = Path.Combine(directory.Path, "pending.json");
        var store = new JsonPendingUpdateStore(file);
        var original = Intent();
        await store.WriteAsync(original);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(Intent(), new CancellationToken(true)));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync(Intent() with { SchemaVersion = 99 }));
        Assert.Equal(original, await store.ReadAsync());
        Assert.Single(Directory.GetFiles(directory.Path));
        await store.ClearAsync();
        Assert.Null(await store.ReadAsync());
        await store.ClearAsync();
    }

    private static UpdateServerOptions Server() => new()
    {
        RequestUrl = "https://updates.example/metadata",
        UseJsonEndpoint = true
    };

    private sealed class MemoryStore : IPendingUpdateStore
    {
        public PendingUpdateAttempt? Pending { get; set; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public int FailWriteNumber { get; init; }
        public bool FailClear { get; set; }
        public Action<PendingUpdateAttempt>? OnWrite { get; init; }
        public Task<PendingUpdateAttempt?> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult(Pending);
        }
        public Task WriteAsync(PendingUpdateAttempt attempt, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++Writes == FailWriteNumber) throw new IOException("store failed");
            OnWrite?.Invoke(attempt);
            Pending = attempt;
            return Task.CompletedTask;
        }
        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailClear) throw new IOException("clear failed");
            Pending = null;
            return Task.CompletedTask;
        }
    }

    private sealed class StubBootstrap : IAndroidBootstrap
    {
        public List<string> Calls { get; } = [];
        public UpdatePackageInfo Available { get; init; } = Package();
        public string PreparedPath { get; init; } = "verified.apk";
        public string? InstalledPath { get; private set; }
        public bool Disposed { get; private set; }
        public Func<CancellationToken, Task<UpdateOperationResult>>? Download { get; init; }
        public Func<CancellationToken, Task<InstallResult>>? Install { get; init; }
        public event EventHandler<ValidateEventArgs>? AddListenerValidate { add { } remove { } }
        public event EventHandler<DownloadProgressChangedEventArgs>? AddListenerDownloadProgressChanged;
        public event EventHandler<UpdateCompletedEventArgs>? AddListenerUpdateCompleted { add { } remove { } }
        public event EventHandler<UpdateFailedEventArgs>? AddListenerUpdateFailed { add { } remove { } }
        public UpdateStateSnapshot GetSnapshot() => new(UpdateState.None, UpdateFailureReason.None, null);
        public IAndroidBootstrap AddListenerUpdatePrecheck(Func<UpdateInfoEventArgs, bool> func) => this;
        public void RaiseProgress() => AddListenerDownloadProgressChanged?.Invoke(this,
            new DownloadProgressChangedEventArgs(new DownloadProgressInfo
            {
                DownloadSpeedBytesPerSecond = 1,
                DownloadedBytes = 1,
                RemainingBytes = 0,
                TotalBytes = 1,
                ProgressPercentage = 100,
                PackageInfo = Available,
                StatusDescription = "Downloaded."
            }));
        public Task<UpdateCheckResult> ValidateAsync(string currentVersion, CancellationToken cancellationToken = default)
        {
            Calls.Add("check");
            return Task.FromResult(new UpdateCheckResult { Success = true, UpdateFound = true, PackageInfo = Available });
        }
        public Task<UpdateOperationResult> DownloadAndVerifyAsync(UpdatePackageInfo packageInfo, CancellationToken cancellationToken = default)
        {
            Calls.Add("download-verify");
            return Download?.Invoke(cancellationToken) ??
                Task.FromResult(new UpdateOperationResult { Success = true, FilePath = PreparedPath });
        }
        public Task<InstallResult> LaunchInstallerAsync(UpdatePackageInfo packageInfo, string apkFilePath, CancellationToken cancellationToken = default)
        {
            Calls.Add("install");
            InstalledPath = apkFilePath;
            return Install?.Invoke(cancellationToken) ?? Task.FromResult(new InstallResult { Success = true });
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingDownloader(List<string> calls) : IUpdateDownloader
    {
        public Task<DownloadResult> DownloadAsync(UpdatePackageInfo packageInfo,
            Action<DownloadProgressInfo>? progressCallback, CancellationToken cancellationToken = default)
        {
            calls.Add("download");
            return Task.FromResult(new DownloadResult { Success = true, FilePath = "verified.apk" });
        }
    }

    private sealed class RecordingValidator(List<string> calls) : IHashValidator
    {
        public Task<HashValidationResult> ValidateSha256Async(string filePath, string expectedSha256, CancellationToken cancellationToken = default)
        {
            calls.Add("verify");
            return Task.FromResult(new HashValidationResult { Success = true });
        }
    }

    private sealed class RecordingInstaller(List<string> calls, IPendingUpdateStore store) : IApkInstaller
    {
        public async Task<InstallResult> LaunchInstallAsync(UpdatePackageInfo packageInfo, string apkFilePath, CancellationToken cancellationToken = default)
        {
            var pending = await store.ReadAsync(cancellationToken);
            Assert.NotNull(pending);
            Assert.Equal(PendingUpdatePhase.IntentPersisted, pending.Phase);
            calls.Add("install");
            return new InstallResult { Success = true };
        }
    }

    private sealed class TrackingDownloader(IUpdateDownloader inner, List<string> calls) : IUpdateDownloader
    {
        public Task<DownloadResult> DownloadAsync(UpdatePackageInfo packageInfo, Action<DownloadProgressInfo>? progressCallback,
            CancellationToken cancellationToken = default)
        {
            calls.Add("download");
            return inner.DownloadAsync(packageInfo, progressCallback, cancellationToken);
        }
    }

    private sealed class TrackingValidator(IHashValidator inner, List<string> calls) : IHashValidator
    {
        public Task<HashValidationResult> ValidateSha256Async(string filePath, string expectedSha256, CancellationToken cancellationToken = default)
        {
            calls.Add("verify");
            return inner.ValidateSha256Async(filePath, expectedSha256, cancellationToken);
        }
    }

    private sealed class PackageHandler(UpdatePackageInfo package, byte[] apk, List<string> calls) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/metadata")
            {
                calls.Add("check");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(package))
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(apk) });
        }
    }

    private sealed class MetadataHandler(Func<UpdatePackageInfo?> getPackage) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var package = getPackage();
            return Task.FromResult(new HttpResponseMessage(package is null ? HttpStatusCode.NoContent : HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(package))
            });
        }
    }

    private sealed class RecordingDispatcher : IUpdateEventDispatcher
    {
        public int Calls { get; private set; }
        public bool Throw { get; set; }
        public void Dispatch(Action callback)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("dispatcher");
            callback();
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "coordinator-tests-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
