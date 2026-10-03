using System.Text.Json;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Models;
using GeneralUpdate.Avalonia.Android.Services;
using Xunit;

namespace GeneralUpdate.Avalonia.Android.Tests;

public sealed class InstallationTrackingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"gu-installation-{Guid.NewGuid():N}");
    private string StatePath => Path.Combine(_directory, "state", "installation.json");

    [Fact]
    public async Task CheckWithoutAttempt_DoesNotInventASuccessfulInstallation()
    {
        using var bootstrap = CreateBootstrap();
        var result = await bootstrap.CheckInstallationAsync("1.0.0");

        Assert.True(result.Success);
        Assert.Equal(UpdateState.None, result.State);
        Assert.Null(result.Record);
        Assert.False(result.IsInstalled);
        Assert.False(result.HasPendingInstallation);
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public async Task Launch_PersistsBeforeHandoff_AndDoesNotConfirmInstallation()
    {
        var installer = new RecordingInstaller
        {
            BeforeLaunch = () =>
            {
                var record = JsonSerializer.Deserialize<InstallationRecord>(File.ReadAllText(StatePath));
                Assert.Equal("2.0.0", record!.TargetVersion);
                Assert.Null(record.ConfirmedAt);
            }
        };
        using var bootstrap = CreateBootstrap(installer);
        var confirmed = 0;
        var phaseCompletions = new List<UpdateState>();
        bootstrap.AddListenerInstallationConfirmed += (_, _) => confirmed++;
        bootstrap.AddListenerUpdateCompleted += (_, args) => phaseCompletions.Add(args.Result.State);

        var result = await bootstrap.LaunchInstallerAsync(Package, "app.apk");

        Assert.True(result.Success);
        Assert.Equal(1, installer.Calls);
        Assert.Equal(0, confirmed);
        Assert.Equal(new[] { UpdateState.Installing }, phaseCompletions);
        Assert.DoesNotContain("AuthToken", await File.ReadAllTextAsync(StatePath));
        Assert.DoesNotContain("DownloadUrl", await File.ReadAllTextAsync(StatePath));
    }

    [Theory]
    [InlineData("2.0.0")]
    [InlineData("2.1.0")]
    public async Task Restart_WithTargetOrNewerVersion_ConfirmsOnceAndKeepsDurableOutcome(string installedVersion)
    {
        using (var original = CreateBootstrap())
        {
            await original.LaunchInstallerAsync(Package, "app.apk");
        }

        var confirmed = 0;
        using (var restarted = CreateBootstrap())
        {
            restarted.AddListenerInstallationConfirmed += (_, args) =>
            {
                confirmed++;
                Assert.True(args.Result.IsInstalled);
                var saved = JsonSerializer.Deserialize<InstallationRecord>(File.ReadAllText(StatePath));
                Assert.Equal(installedVersion, saved!.InstalledVersion);
                Assert.NotNull(saved.ConfirmedAt);
            };
            var result = await restarted.CheckInstallationAsync(installedVersion);
            Assert.True(result.IsInstalled);
            Assert.Equal(installedVersion, result.CurrentVersion);
            Assert.Equal(UpdateState.Installed, restarted.GetSnapshot().State);
            Assert.NotNull(result.Record!.ConfirmedAt);
            Assert.True((await restarted.CheckInstallationAsync(installedVersion)).IsInstalled);
            Assert.Equal(1, confirmed);
        }

        using var nextRestart = CreateBootstrap();
        nextRestart.AddListenerInstallationConfirmed += (_, _) => confirmed++;
        Assert.True((await nextRestart.CheckInstallationAsync(installedVersion)).IsInstalled);
        Assert.Equal(1, confirmed);
    }

    [Theory]
    [InlineData(UpdateFailureReason.None)]
    [InlineData(UpdateFailureReason.InstallPermissionDenied)]
    [InlineData(UpdateFailureReason.InstallLaunchFailed)]
    public async Task Restart_WithOldVersion_KeepsUnconfirmedAttemptForRetry(UpdateFailureReason launchFailure)
    {
        using (var original = CreateBootstrap(new RecordingInstaller { Failure = launchFailure }))
        {
            await original.LaunchInstallerAsync(Package, "app.apk");
        }
        var beforeCheck = await File.ReadAllTextAsync(StatePath);

        using var restarted = CreateBootstrap();
        var confirmed = false;
        restarted.AddListenerInstallationConfirmed += (_, _) => confirmed = true;
        var result = await restarted.CheckInstallationAsync("1.0.0");

        Assert.True(result.Success);
        Assert.True(result.HasPendingInstallation);
        Assert.False(result.IsInstalled);
        Assert.False(confirmed);
        Assert.Null(result.Record!.ConfirmedAt);
        Assert.Equal(beforeCheck, await File.ReadAllTextAsync(StatePath));
        Assert.Equal(UpdateState.InstallationPending, restarted.GetSnapshot().State);
    }

    [Fact]
    public async Task NewAttempt_ReplacesPreviousConfirmedTarget()
    {
        using var bootstrap = CreateBootstrap();
        await bootstrap.LaunchInstallerAsync(Package, "app.apk");
        Assert.True((await bootstrap.CheckInstallationAsync("2.0.0")).IsInstalled);

        await bootstrap.LaunchInstallerAsync(Package with { Version = "3.0.0" }, "app-v3.apk");
        var pending = await bootstrap.CheckInstallationAsync("2.0.0");

        Assert.True(pending.HasPendingInstallation);
        Assert.Equal("3.0.0", pending.Record!.TargetVersion);
        Assert.Null(pending.Record.ConfirmedAt);
        Assert.Null(pending.Record.InstalledVersion);
    }

    [Fact]
    public async Task JournalWriteFailure_PreventsInstallerAndReportsFailure()
    {
        Directory.CreateDirectory(StatePath);
        var installer = new RecordingInstaller();
        using var bootstrap = CreateBootstrap(installer);
        UpdateOperationResult? failure = null;
        bootstrap.AddListenerUpdateFailed += (_, args) => failure = args.Result;

        var result = await bootstrap.LaunchInstallerAsync(Package, "app.apk");

        Assert.False(result.Success);
        Assert.Equal(UpdateFailureReason.FileIoError, result.FailureReason);
        Assert.Same(result, failure);
        Assert.Equal(0, installer.Calls);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StatePath)!, "*.tmp"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"TargetVersion\":\"2.0.0\"}")]
    [InlineData("{\"TargetVersion\":\"2.0.0\",\"RequestedAt\":\"2026-01-01T00:00:00Z\",\"InstalledVersion\":\"1.0.0\",\"ConfirmedAt\":\"2026-01-01T00:00:01Z\"}")]
    public async Task CorruptJournal_IsReportedAndNotDiscarded(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath, json);
        using var bootstrap = CreateBootstrap();
        UpdateOperationResult? failure = null;
        bootstrap.AddListenerUpdateFailed += (_, args) => failure = args.Result;

        var result = await bootstrap.CheckInstallationAsync("2.0.0");

        Assert.False(result.Success);
        Assert.Equal(UpdateFailureReason.FileIoError, result.FailureReason);
        Assert.Same(result, failure);
        Assert.Equal(json, await File.ReadAllTextAsync(StatePath));
    }

    [Fact]
    public async Task InvalidInstalledVersion_CannotConfirmOrEraseAttempt()
    {
        using var bootstrap = CreateBootstrap();
        await bootstrap.LaunchInstallerAsync(Package, "app.apk");
        var json = await File.ReadAllTextAsync(StatePath);

        var result = await bootstrap.CheckInstallationAsync("unknown");

        Assert.False(result.Success);
        Assert.Equal(UpdateFailureReason.VersionComparisonFailed, result.FailureReason);
        Assert.Equal(json, await File.ReadAllTextAsync(StatePath));
    }

    [Fact]
    public async Task InvalidTarget_CannotLaunchOrOverwriteAttempt()
    {
        var installer = new RecordingInstaller();
        using var bootstrap = CreateBootstrap(installer);
        await bootstrap.LaunchInstallerAsync(Package, "app.apk");
        var json = await File.ReadAllTextAsync(StatePath);

        var result = await bootstrap.LaunchInstallerAsync(Package with { Version = "invalid" }, "other.apk");

        Assert.False(result.Success);
        Assert.Equal(UpdateFailureReason.VersionComparisonFailed, result.FailureReason);
        Assert.Equal(1, installer.Calls);
        Assert.Equal(json, await File.ReadAllTextAsync(StatePath));
    }

    [Fact]
    public async Task CanceledCheck_LeavesAttemptAvailableForNextStartup()
    {
        using var bootstrap = CreateBootstrap();
        await bootstrap.LaunchInstallerAsync(Package, "app.apk");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => bootstrap.CheckInstallationAsync("2.0.0", canceled.Token));

        Assert.True((await bootstrap.CheckInstallationAsync("1.0.0")).HasPendingInstallation);
        Assert.True((await bootstrap.CheckInstallationAsync("2.0.0")).IsInstalled);
    }

    [Fact]
    public async Task JournalParentIsAFile_IsNotReportedAsNoAttempt()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.GetDirectoryName(StatePath)!, "not a directory");
        using var bootstrap = CreateBootstrap();

        var result = await bootstrap.CheckInstallationAsync("2.0.0");

        Assert.False(result.Success);
        Assert.Equal(UpdateFailureReason.FileIoError, result.FailureReason);
    }

    [Fact]
    public async Task ConcurrentChecks_ConfirmOnlyOnce()
    {
        using var bootstrap = CreateBootstrap();
        await bootstrap.LaunchInstallerAsync(Package, "app.apk");
        var confirmations = 0;
        bootstrap.AddListenerInstallationConfirmed += (_, _) => Interlocked.Increment(ref confirmations);

        var results = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => bootstrap.CheckInstallationAsync("2.0.0")));

        Assert.All(results, result => Assert.True(result.IsInstalled));
        Assert.Equal(1, confirmations);
    }

    [Fact]
    public void DependencyConstructor_RequiresInstallationStore()
    {
        using var http = new HttpClient();
        using var source = new HttpUpdatePackageClient(http);
        Assert.Throws<ArgumentNullException>(() => new AndroidBootstrap(
            new SystemVersionComparer(), new UnusedDownloader(), new Sha256HashValidator(),
            new RecordingInstaller(), new PhysicalFileStorage(), source, installationStore: null!));
    }

    private AndroidBootstrap CreateBootstrap(RecordingInstaller? installer = null) =>
        new(new SystemVersionComparer(), new UnusedDownloader(), new Sha256HashValidator(),
            installer ?? new RecordingInstaller(), new PhysicalFileStorage(),
            installationStateFilePath: StatePath);

    private static UpdatePackageInfo Package => new()
    {
        Version = "2.0.0",
        DownloadUrl = "https://example.com/app.apk",
        Sha256 = new string('a', 64),
        AuthToken = "test-only-token"
    };

    private sealed class UnusedDownloader : IUpdateDownloader
    {
        public Task<DownloadResult> DownloadAsync(UpdatePackageInfo packageInfo,
            Action<DownloadProgressInfo>? progressCallback, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Startup reconciliation must not download or access the network.");
    }

    private sealed class RecordingInstaller : IApkInstaller
    {
        public int Calls { get; private set; }
        public Action? BeforeLaunch { get; init; }
        public UpdateFailureReason Failure { get; init; }

        public Task<InstallResult> LaunchInstallAsync(UpdatePackageInfo packageInfo, string apkFilePath,
            CancellationToken cancellationToken = default)
        {
            BeforeLaunch?.Invoke();
            Calls++;
            return Task.FromResult(new InstallResult
            {
                Success = Failure == UpdateFailureReason.None,
                State = Failure == UpdateFailureReason.None ? UpdateState.Installing : UpdateState.Failed,
                FailureReason = Failure
            });
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
