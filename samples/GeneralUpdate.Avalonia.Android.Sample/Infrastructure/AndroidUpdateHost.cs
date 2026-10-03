using Android.Content;
using Android.Content.PM;
using Android.Provider;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Sample.Infrastructure;

internal sealed class AndroidUpdateHost(IUpdateLogger logger) : IUpdateHost
{
    private static Context Context => global::Android.App.Application.Context;

    public IAndroidBootstrap CreateBootstrap(UpdateServerOptions? options) =>
        GeneralUpdateBootstrap.CreateDefault(new AndroidUpdateOptions
        {
            FileProviderAuthority = $"{Context.PackageName}.generalupdate.fileprovider",
            UpdateServer = options
        }, activityProvider: new CurrentActivityProvider(),
            eventDispatcher: new AvaloniaUpdateEventDispatcher(), logger: logger,
            httpOptions: new HttpDownloadOptions { DownloadTimeout = TimeSpan.FromMinutes(15) });

    public string GetCurrentVersion()
    {
        if (Context.PackageManager is not { } manager || string.IsNullOrWhiteSpace(Context.PackageName))
            throw new InvalidOperationException("无法读取本机应用版本。");
        try
        {
            var version = manager.GetPackageInfo(Context.PackageName, PackageInfoFlags.Activities)?.VersionName;
            return !string.IsNullOrWhiteSpace(version) ? version
                : throw new InvalidOperationException("本机应用未提供版本号，不能确认升级结果。");
        }
        catch (PackageManager.NameNotFoundException ex)
        {
            throw new InvalidOperationException("无法读取本机应用版本。", ex);
        }
    }

    public bool CanRequestInstalls() =>
        global::Android.OS.Build.VERSION.SdkInt < global::Android.OS.BuildVersionCodes.O ||
        Context.PackageManager?.CanRequestPackageInstalls() == true;

    public void RequestInstallPermission()
    {
        if (string.IsNullOrWhiteSpace(Context.PackageName))
            throw new InvalidOperationException("无法读取应用包名。");
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
            throw new InvalidOperationException("无法打开未知来源安装授权页面。", ex);
        }
    }

    public UpdateServerOptions LoadServerOptions()
    {
        using var preferences = Context.GetSharedPreferences("generalupdate-server", FileCreationMode.Private)
            ?? throw new IOException("无法读取更新服务配置。");
        var platform = preferences.GetString("platform", "3");
        if (!int.TryParse(platform, out var platformId) || platformId <= 0)
            throw new InvalidDataException("保存的平台编号无效，请重新填写并保存。");
        return new UpdateServerOptions
        {
            RequestUrl = preferences.GetString("url", "http://127.0.0.1:5080/Upgrade/Verification")!,
            AppKey = preferences.GetString("appKey", "demo-client")!,
            Platform = platformId,
            ProductId = preferences.GetString("productId", "demo-product")!,
            AppType = 1
        };
    }

    public void SaveServerOptions(UpdateServerOptions options)
    {
        using var preferences = Context.GetSharedPreferences("generalupdate-server", FileCreationMode.Private)
            ?? throw new IOException("无法读取更新服务配置。");
        using var editor = preferences.Edit() ?? throw new IOException("无法保存更新服务配置。");
        editor.PutString("url", options.RequestUrl);
        editor.PutString("appKey", options.AppKey);
        editor.PutString("platform", options.Platform.ToString(System.Globalization.CultureInfo.InvariantCulture));
        editor.PutString("productId", options.ProductId);
        if (!editor.Commit())
            throw new IOException("保存更新服务配置失败，未开始更新。");
    }
}
