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
        IUpdateLogger? logger = null)
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
        var usedClient = httpClient ?? new HttpClient();

        var downloader = new HttpResumableApkDownloader(usedClient, usedStorage, effectiveOptions, usedLogger);
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
            usedLogger);
    }
}
