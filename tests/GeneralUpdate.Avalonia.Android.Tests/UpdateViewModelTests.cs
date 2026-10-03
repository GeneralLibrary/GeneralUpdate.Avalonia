using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Models;
using GeneralUpdate.Avalonia.Android.Sample.Infrastructure;
using GeneralUpdate.Avalonia.Android.Sample.ViewModels;
using GeneralUpdate.Avalonia.Android.Services;
using Xunit;

namespace GeneralUpdate.Avalonia.Android.Tests;

public sealed class UpdateViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_ConfirmsOfflineEvenIfServerOrSettingsFail(bool settingsFailure)
    {
        using var host = new FakeHost { Version = "2.0.0", FailQuery = !settingsFailure, FailSettingsLoad = settingsFailure };
        host.Store.Record = new InstallationRecord { TargetVersion = "2.0.0", RequestedAt = DateTimeOffset.UtcNow };
        var model = Create(host);
        await model.InitializeAsync();
        Assert.Contains("升级已确认", model.InstallationStatus);
        Assert.NotNull(host.Store.Record.ConfirmedAt);
        Assert.Contains("失败", model.Status);
        Assert.Equal(0, host.InstallerCalls);
        Assert.True(model.CanStart);
    }

    [Fact]
    public async Task PermissionReturn_RetriesOnceAndReconcilesWithoutReinstalling()
    {
        using var host = new FakeHost();
        var model = Create(host);
        await model.InitializeAsync();
        model.AppKey = "changed-key";
        await model.StartAsync();
        Assert.Equal("changed-key", host.SavedOptions!.AppKey);
        Assert.Equal(1, host.Downloads);
        Assert.Equal(1, host.InstallerCalls);
        Assert.Equal(1, host.PermissionRequests);
        Assert.Contains("尚未确认", model.InstallationStatus);

        await model.ResumeAsync();
        Assert.Equal(1, host.InstallerCalls);
        host.CanInstall = true;
        await model.ResumeAsync();
        Assert.Equal(2, host.InstallerCalls);
        Assert.Equal(1, host.Downloads);
        await model.ResumeAsync();
        Assert.Equal(2, host.InstallerCalls);
        Assert.Contains("尚未确认", model.InstallationStatus);

        host.Version = "2.0.0";
        host.FailQuery = true;
        await model.ResumeAsync();
        Assert.Contains("升级已确认", model.InstallationStatus);
        Assert.Equal(2, host.InstallerCalls);
    }

    [Fact]
    public async Task CorruptRecord_BlocksUpgradeUntilExplicitReset()
    {
        using var host = new FakeHost { CanInstall = true };
        host.Store.LoadError = new InvalidDataException("corrupt record");
        var model = Create(host);
        await model.InitializeAsync();
        Assert.Equal("demo", model.AppKey);
        await model.StartAsync();
        Assert.Equal(0, host.Downloads);
        Assert.Contains("重置", model.Status);

        await model.ResetInstallationAsync();
        Assert.Contains("已重置", model.Status);
        await model.StartAsync();
        Assert.Equal(1, host.Downloads);
        Assert.Equal(1, host.InstallerCalls);
    }

    [Fact]
    public async Task Reset_DoesNotHideSubsequentReconciliationFailure()
    {
        using var host = new FakeHost();
        var model = Create(host);
        await model.InitializeAsync();
        host.Version = "invalid-version";
        await model.ResetInstallationAsync();
        Assert.Contains("失败", model.Status);
        Assert.DoesNotContain("已重置", model.Status);
    }

    [Fact]
    public async Task Deactivation_CancelsAndAwaitsWorkBeforeDisposal()
    {
        using var host = new FakeHost { BlockDownload = true };
        var model = Create(host);
        await model.InitializeAsync();
        var starting = model.StartAsync();
        await host.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await model.DeactivateAsync();
        await starting;
        Assert.Equal(0, host.InstallerCalls);
        Assert.True(model.CanStart);
        Assert.Contains("取消", model.Status);
        Assert.Throws<ObjectDisposedException>(() => host.Bootstraps.Last().GetSnapshot());
        await model.InitializeAsync();
        Assert.Equal("1.0.0", model.CurrentVersion);
    }

    [Fact]
    public async Task InvalidInputOrSaveFailure_DoesNotDownload()
    {
        using var host = new FakeHost();
        var model = Create(host);
        await model.InitializeAsync();
        model.RequestUrl = "not-a-url";
        await model.StartAsync();
        Assert.Contains("失败", model.Status);
        Assert.Null(host.SavedOptions);
        Assert.Equal(0, host.Downloads);
        model.RequestUrl = "https://example.test/check";
        host.FailSettingsSave = true;
        await model.StartAsync();
        Assert.Contains("失败", model.Status);
        Assert.Equal(0, host.Downloads);
    }

    private static UpdateViewModel Create(FakeHost host) => new(host, new NoOpUpdateLogger());

    private sealed class FakeHost : IUpdateHost, IDisposable
    {
        public string Version { get; set; } = "1.0.0";
        public bool FailQuery { get; set; }
        public bool FailSettingsLoad { get; set; }
        public bool FailSettingsSave { get; set; }
        public bool CanInstall { get; set; }
        public bool BlockDownload { get; set; }
        public int InstallerCalls { get; private set; }
        public int PermissionRequests { get; private set; }
        public int Downloads { get; private set; }
        public UpdateServerOptions? SavedOptions { get; private set; }
        public MemoryInstallationStore Store { get; } = new();
        public List<IAndroidBootstrap> Bootstraps { get; } = [];
        public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string GetCurrentVersion() => Version;
        public bool CanRequestInstalls() => CanInstall;
        public void RequestInstallPermission() => PermissionRequests++;

        public UpdateServerOptions LoadServerOptions() => FailSettingsLoad ? throw new IOException("settings unavailable")
            : new() { RequestUrl = "https://example.test/check", AppKey = "demo", ProductId = "product", Platform = 3 };

        public void SaveServerOptions(UpdateServerOptions options)
        {
            if (FailSettingsSave) throw new IOException("settings read-only");
            SavedOptions = options;
        }

        public IAndroidBootstrap CreateBootstrap(UpdateServerOptions? options)
        {
            var bootstrap = TestBootstrap.Create(store: Store,
                source: new DelegatePackageSource((_, _) => FailQuery
                    ? throw new HttpRequestException("offline")
                    : Task.FromResult<UpdatePackageInfo?>(TestBootstrap.Package)),
                downloader: new DelegateDownloader(async ct =>
                {
                    Downloads++;
                    DownloadStarted.TrySetResult();
                    if (BlockDownload) await Task.Delay(Timeout.Infinite, ct);
                    return new DownloadResult { Success = true, FilePath = "test.apk" };
                }),
                installer: new DelegateInstaller(_ =>
                {
                    InstallerCalls++;
                    return Task.FromResult(new InstallResult
                    {
                        Success = CanInstall,
                        State = CanInstall ? UpdateState.Installing : UpdateState.Failed,
                        FailureReason = CanInstall ? UpdateFailureReason.None : UpdateFailureReason.InstallPermissionDenied
                    });
                }));
            Bootstraps.Add(bootstrap);
            return bootstrap;
        }

        public void Dispose()
        {
            foreach (var bootstrap in Bootstraps) bootstrap.Dispose();
        }
    }
}
