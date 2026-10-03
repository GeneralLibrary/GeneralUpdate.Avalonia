using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Models;
using GeneralUpdate.Avalonia.Android.Services;

namespace GeneralUpdate.Avalonia.Android.Tests;

internal sealed class DelegatePackageSource(Func<string, CancellationToken, Task<UpdatePackageInfo?>> query)
    : IUpdatePackageSource, IDisposable
{
    public bool Disposed { get; private set; }
    public Task<UpdatePackageInfo?> GetLatestAsync(string version, CancellationToken ct = default) => query(version, ct);
    public void Dispose() => Disposed = true;
}

internal sealed class MemoryInstallationStore : IInstallationStore
{
    public InstallationRecord? Record { get; set; }
    public Exception? LoadError { get; set; }
    public Exception? ClearError { get; set; }

    public Task<InstallationRecord?> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (LoadError is not null) throw LoadError;
        return Task.FromResult(Record);
    }

    public Task SaveAsync(InstallationRecord record, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Record = record;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (ClearError is not null) throw ClearError;
        Record = null;
        LoadError = null;
        return Task.CompletedTask;
    }
}

internal sealed class DelegateDownloader(Func<CancellationToken, Task<DownloadResult>> download)
    : IUpdateDownloader, IDisposable
{
    public bool Disposed { get; private set; }
    public Task<DownloadResult> DownloadAsync(UpdatePackageInfo package, Action<DownloadProgressInfo>? progress,
        CancellationToken cancellationToken = default) => download(cancellationToken);
    public void Dispose() => Disposed = true;
}

internal sealed class DelegateInstaller(Func<CancellationToken, Task<InstallResult>> install) : IApkInstaller
{
    public Task<InstallResult> LaunchInstallAsync(UpdatePackageInfo package, string path,
        CancellationToken cancellationToken = default) => install(cancellationToken);
}

internal sealed class DelegateHashValidator(Func<CancellationToken, Task<HashValidationResult>> validate) : IHashValidator
{
    public Task<HashValidationResult> ValidateSha256Async(string path, string expectedHash,
        CancellationToken cancellationToken = default) => validate(cancellationToken);
}

internal static class TestBootstrap
{
    public static UpdatePackageInfo Package => new()
    {
        Version = "2.0.0",
        DownloadUrl = "https://example.test/update.apk",
        Sha256 = new string('a', 64)
    };

    public static AndroidBootstrap Create(IUpdatePackageSource? source = null, IInstallationStore? store = null,
        IUpdateDownloader? downloader = null, IHashValidator? hash = null, IApkInstaller? installer = null) =>
        new(new SystemVersionComparer(),
            downloader ?? new DelegateDownloader(_ => Task.FromResult(new DownloadResult
            {
                Success = true,
                State = UpdateState.Completed,
                FilePath = "verified-test.apk"
            })),
            hash ?? new DelegateHashValidator(_ => Task.FromResult(new HashValidationResult { Success = true })),
            installer ?? new DelegateInstaller(_ => Task.FromResult(new InstallResult
            {
                Success = true,
                State = UpdateState.Installing
            })),
            new PhysicalFileStorage(),
            source ?? new DelegatePackageSource((_, _) => Task.FromResult<UpdatePackageInfo?>(Package)),
            store ?? new MemoryInstallationStore());
}
