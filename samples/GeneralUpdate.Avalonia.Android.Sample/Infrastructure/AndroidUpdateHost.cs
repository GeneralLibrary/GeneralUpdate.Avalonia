using Android.Content;
using Android.Content.PM;
using Android.Provider;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Sample.Infrastructure;

internal sealed class AndroidUpdateHost(IUpdateLogger logger) : IUpdateHost
{
    private static Context Context => global::Android.App.Application.Context;

    public IAndroidBootstrap CreateBootstrap(UpdateServerOptions? options, UpdateLanguage language) =>
        GeneralUpdateBootstrap.CreateDefault(new AndroidUpdateOptions
        {
            FileProviderAuthority = $"{Context.PackageName}.generalupdate.fileprovider",
            UpdateServer = options,
            Language = language,
            AllowInsecureHttpDownloads = options?.RequestUrl.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase) == true
        }, activityProvider: new CurrentActivityProvider(),
            eventDispatcher: new AvaloniaUpdateEventDispatcher(), logger: logger,
            httpOptions: new HttpDownloadOptions { DownloadTimeout = TimeSpan.FromMinutes(15) });

    public UpdateLanguage LoadLanguage()
    {
        using var preferences = GetPreferences();
        var language = preferences.GetInt("language", (int)UpdateLanguage.English);
        return Enum.IsDefined((UpdateLanguage)language) ? (UpdateLanguage)language : UpdateLanguage.English;
    }

    public void SaveLanguage(UpdateLanguage language)
    {
        using var preferences = GetPreferences(language);
        using var editor = preferences.Edit() ?? throw new IOException(UpdateViewText.Get(language, "Unable to save language preference."));
        if (!editor.PutInt("language", (int)language).Commit())
            throw new IOException(UpdateViewText.Get(language, "Unable to save language preference."));
    }

    public string GetCurrentVersion(UpdateLanguage language)
    {
        if (Context.PackageManager is not { } manager || string.IsNullOrWhiteSpace(Context.PackageName))
            throw new InvalidOperationException(UpdateViewText.Get(language, "Unable to read installed app version."));
        try
        {
            var version = manager.GetPackageInfo(Context.PackageName, PackageInfoFlags.Activities)?.VersionName;
            return !string.IsNullOrWhiteSpace(version) ? version
                : throw new InvalidOperationException(UpdateViewText.Get(language,
                    "The installed app does not provide a version, so update confirmation is unavailable."));
        }
        catch (PackageManager.NameNotFoundException ex)
        {
            throw new InvalidOperationException(UpdateViewText.Get(language, "Unable to read installed app version."), ex);
        }
    }

    public bool CanRequestInstalls() =>
        global::Android.OS.Build.VERSION.SdkInt < global::Android.OS.BuildVersionCodes.O ||
        Context.PackageManager?.CanRequestPackageInstalls() == true;

    public void RequestInstallPermission(UpdateLanguage language)
    {
        if (string.IsNullOrWhiteSpace(Context.PackageName))
            throw new InvalidOperationException(UpdateViewText.Get(language, "Unable to read app package name."));
        using var intent = new Intent(Settings.ActionManageUnknownAppSources,
            global::Android.Net.Uri.Parse($"package:{Context.PackageName}"));
        try
        {
            if (MainActivity.Current is { } activity)
                activity.StartActivity(intent);
            else
                Context.StartActivity(intent.AddFlags(ActivityFlags.NewTask));
        }
        catch (Exception ex) when (ex is ActivityNotFoundException or Java.Lang.SecurityException)
        {
            throw new InvalidOperationException(UpdateViewText.Get(language,
                "Unable to open the unknown sources installation permission page."), ex);
        }
    }

    public UpdateServerOptions LoadServerOptions(UpdateLanguage language)
    {
        using var preferences = GetPreferences(language);
        var platform = preferences.GetString("platform", "3");
        if (!int.TryParse(platform, out var platformId) || platformId <= 0)
            throw new InvalidDataException(UpdateViewText.Get(language, "Saved platform number is invalid. Re-enter and save it."));
        return new UpdateServerOptions
        {
            RequestUrl = preferences.GetString("url", "http://127.0.0.1:5080/Upgrade/Verification")!,
            AppKey = preferences.GetString("appKey", "demo-client")!,
            Platform = platformId,
            ProductId = preferences.GetString("productId", "demo-product")!,
            AppType = 1
        };
    }

    public void SaveServerOptions(UpdateServerOptions options, UpdateLanguage language)
    {
        using var preferences = GetPreferences(language);
        using var editor = preferences.Edit() ?? throw new IOException(UpdateViewText.Get(language, "Unable to save update server settings."));
        editor.PutString("url", options.RequestUrl);
        editor.PutString("appKey", options.AppKey);
        editor.PutString("platform", options.Platform.ToString(System.Globalization.CultureInfo.InvariantCulture));
        editor.PutString("productId", options.ProductId);
        if (!editor.Commit())
            throw new IOException(UpdateViewText.Get(language, "Saving update server settings failed; update was not started."));
    }

    private static ISharedPreferences GetPreferences(UpdateLanguage language) =>
        Context.GetSharedPreferences("generalupdate-server", FileCreationMode.Private)
        ?? throw new IOException(UpdateViewText.Get(language, "Unable to read update server settings."));

    private static ISharedPreferences GetPreferences() => GetPreferences(UpdateLanguage.English);
}
