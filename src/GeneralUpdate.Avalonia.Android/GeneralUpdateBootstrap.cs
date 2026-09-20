using System.Net.Http;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Models;
using GeneralUpdate.Avalonia.Android.Services;

namespace GeneralUpdate.Avalonia.Android;

public static class GeneralUpdateBootstrap
{
    /// <summary>
    /// Creates an owning coordinator for complete update attempts and next-launch reconciliation.
    /// Pending state defaults to the app-private no-backup files directory, never the APK cache.
    /// Supply a store explicitly when an Android context is unavailable.
    /// </summary>
    public static IAndroidUpdateCoordinator CreateCoordinator(
        AndroidUpdateOptions options,
        IPendingUpdateStore? pendingStore = null,
        IAndroidContextProvider? contextProvider = null,
        IAndroidActivityProvider? activityProvider = null,
        HttpClient? httpClient = null,
        IVersionComparer? versionComparer = null,
        IUpdateEventDispatcher? eventDispatcher = null,
        IUpdateLogger? logger = null,
        HttpDownloadOptions? httpOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var usedContextProvider = contextProvider ?? new DefaultAndroidContextProvider();
        if (pendingStore is null)
        {
            var filesDirectory = usedContextProvider.GetContext()?.NoBackupFilesDir?.AbsolutePath;
            if (string.IsNullOrWhiteSpace(filesDirectory))
            {
                throw new InvalidOperationException(
                    "A persistent app-private directory is unavailable. Supply an IPendingUpdateStore.");
            }

            pendingStore = new JsonPendingUpdateStore(Path.Combine(filesDirectory, "generalupdate", "pending-update.json"));
        }

        var usedVersionComparer = versionComparer ?? new SystemVersionComparer();
        var bootstrap = CreateDefault(options, usedContextProvider, activityProvider, httpClient,
            usedVersionComparer, eventDispatcher, logger, httpOptions);
        return new AndroidUpdateCoordinator(bootstrap, pendingStore, usedVersionComparer, eventDispatcher, ownsBootstrap: true);
    }

    public static IAndroidBootstrap CreateDefault(
        AndroidUpdateOptions options,
        IAndroidContextProvider? contextProvider = null,
        IAndroidActivityProvider? activityProvider = null,
        HttpClient? httpClient = null,
        IVersionComparer? versionComparer = null,
        IUpdateEventDispatcher? eventDispatcher = null,
        IUpdateLogger? logger = null,
        HttpDownloadOptions? httpOptions = null)
    {
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

        var effectiveOptions = options with { DownloadDirectoryPath = effectiveDownloadDirectory };
        var usedLogger = logger ?? new NoOpUpdateLogger();
        var usedStorage = new PhysicalFileStorage();

        HttpResumableApkDownloader downloader;
        if (httpOptions != null)
        {
            // Use internal constructor that builds HttpClient from HttpDownloadOptions
            // (SSL validation, proxy, auth, timeouts)
            downloader = new HttpResumableApkDownloader(
                usedStorage, effectiveOptions, httpOptions, usedLogger);
        }
        else
        {
            var usedClient = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            downloader = new HttpResumableApkDownloader(
                usedClient, usedStorage, effectiveOptions, null, ownsClient: httpClient is null, logger: usedLogger);
        }
        var validator = new Sha256HashValidator();
        var installer = new AndroidApkInstaller(
            usedContextProvider,
            activityProvider ?? new NullAndroidActivityProvider(),
            effectiveOptions,
            usedLogger);

        return new AndroidBootstrap(
            versionComparer ?? new SystemVersionComparer(),
            downloader,
            validator,
            installer,
            usedStorage,
            eventDispatcher,
            usedLogger,
            options.UpdateServer,
            httpClient,
            httpOptions);
    }
}
