using System.Net.Http;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Models;
using GeneralUpdate.Avalonia.Android.Services;

namespace GeneralUpdate.Avalonia.Android;

public static class GeneralUpdateBootstrap
{
    public static IAndroidBootstrap CreateDefault(
        AndroidUpdateOptions options,
        IAndroidContextProvider? contextProvider = null,
        IAndroidActivityProvider? activityProvider = null,
        HttpClient? httpClient = null,
        IVersionComparer? versionComparer = null,
        IUpdateEventDispatcher? eventDispatcher = null,
        IUpdateLogger? logger = null,
        HttpDownloadOptions? httpOptions = null,
        IUpdatePackageSource? packageSource = null,
        IInstallationStore? installationStore = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.DownloadBufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.DownloadBufferSize));
        var usedContextProvider = contextProvider ?? new DefaultAndroidContextProvider();
        var context = usedContextProvider.GetContext();
        var effectiveDownloadDirectory = options.DownloadDirectoryPath;
        if (string.IsNullOrWhiteSpace(effectiveDownloadDirectory) && context?.CacheDir?.AbsolutePath is string cacheDirPath)
        {
            effectiveDownloadDirectory = Path.Combine(cacheDirPath, "update");
        }

        if (string.IsNullOrWhiteSpace(effectiveDownloadDirectory))
        {
            effectiveDownloadDirectory = Path.Combine(Path.GetTempPath(), "update");
        }

        var installationStateFilePath = options.InstallationStateFilePath;
        if (installationStore is null && string.IsNullOrWhiteSpace(installationStateFilePath))
        {
            var filesDirectory = context?.FilesDir?.AbsolutePath
                ?? throw new InvalidOperationException("Android FilesDir is unavailable. Configure InstallationStateFilePath explicitly.");
            installationStateFilePath = Path.Combine(filesDirectory, "update", "installation.json");
        }

        var effectiveOptions = options with
        {
            DownloadDirectoryPath = effectiveDownloadDirectory,
            InstallationStateFilePath = installationStateFilePath
        };
        var usedLogger = logger ?? new NoOpUpdateLogger();
        var usedStorage = new PhysicalFileStorage();
        var usedInstallationStore = installationStore ?? new JsonFileInstallationStore(installationStateFilePath);

        var transportOptions = httpOptions ?? new HttpDownloadOptions();
        var usedClient = UpdateHttpClientFactory.Create(httpClient, httpOptions, out var ownsClient);
        // Handler settings were applied above; both consumers share the same client and request policies.
        var requestOptions = transportOptions with { SslValidationPolicy = null, Proxy = null, UseProxy = false };
        var downloader = new HttpResumableApkDownloader(
            usedClient, usedStorage, effectiveOptions, usedLogger, requestOptions, ownsClient);
        var usedComparer = versionComparer ?? new SystemVersionComparer();
        var usedSource = packageSource ?? new HttpUpdatePackageClient(
            usedClient, requestOptions.AuthProvider, usedComparer,
            updateServer: options.UpdateServer, requestTimeout: requestOptions.RequestTimeout);
        var validator = new Sha256HashValidator(options.Language);
        var installer = new AndroidApkInstaller(
            usedContextProvider,
            activityProvider ?? new NullAndroidActivityProvider(),
            effectiveOptions,
            usedLogger);

        return new AndroidBootstrap(
            usedComparer,
            downloader,
            validator,
            installer,
            usedStorage,
            usedSource,
            usedInstallationStore,
            eventDispatcher,
            usedLogger,
            options.Language);
    }
}
