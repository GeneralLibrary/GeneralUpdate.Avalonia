using Android.Content;
using Android.Content.PM;
using Android.Provider;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Enums;
using GeneralUpdate.Avalonia.Android.Models;
using GeneralUpdate.Avalonia.Android.Sample.Infrastructure;

namespace GeneralUpdate.Avalonia.Android.Sample.Views;

public partial class MainView : UserControl
{
    private IAndroidBootstrap? _bootstrap;
    private CancellationTokenSource? _operationCts;
    private UpdatePackageInfo? _pendingPackage;
    private string? _pendingApkFilePath;
    private bool _isRunning;
    private bool _isForcedUpdate;

    public MainView()
    {
        InitializeComponent();
        CurrentVersionText.Text = GetCurrentVersion();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MainActivity.Resumed += OnActivityResumed;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        MainActivity.Resumed -= OnActivityResumed;
        base.OnDetachedFromVisualTree(e);
    }

    private async void OnStartUpdate(object? sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        if (!TryReadServerOptions(out var serverOptions))
        {
            return;
        }

        ResetOperation();
        SetRunning(true);
        SetStatus("正在连接 GeneralSpacestation 检查更新...");

        try
        {
            _operationCts = new CancellationTokenSource();
            _bootstrap = CreateBootstrap(serverOptions);
            var cancellationToken = _operationCts.Token;
            var currentVersion = GetCurrentVersion();

            var check = await _bootstrap.ValidateAsync(currentVersion, cancellationToken);
            if (!check.Success)
            {
                SetStatus(DescribeFailure(check));
                return;
            }

            if (!check.UpdateFound || check.PackageInfo is not { } packageInfo)
            {
                TargetVersionText.Text = "已是最新版本";
                SetStatus("当前已经是最新版本。");
                return;
            }

            TargetVersionText.Text = packageInfo.Version;
            _isForcedUpdate = packageInfo.IsForced;
            SetRunning(true);
            ReleaseNotesText.Text = string.IsNullOrWhiteSpace(packageInfo.Description)
                ? "服务端未提供更新说明。"
                : packageInfo.Description;
            SetStatus(packageInfo.IsForced
                ? $"发现强制更新 {packageInfo.Version}，开始下载..."
                : $"发现新版本 {packageInfo.Version}，开始下载...");

            var prepared = await _bootstrap.DownloadAndVerifyAsync(packageInfo, cancellationToken);
            if (!prepared.Success || string.IsNullOrWhiteSpace(prepared.FilePath))
            {
                SetStatus(DescribeFailure(prepared));
                return;
            }

            _pendingPackage = packageInfo;
            _pendingApkFilePath = prepared.FilePath;
            SetStatus("APK 下载完成且 SHA-256 校验通过，正在拉起系统安装器...");
            await TryLaunchPendingInstallerAsync(cancellationToken);
        }
        catch (System.OperationCanceledException)
        {
            SetStatus("更新操作已取消。");
        }
        finally
        {
            SetRunning(false);
        }
    }

    private void OnCancelUpdate(object? sender, RoutedEventArgs e)
    {
        _operationCts?.Cancel();
    }

    private IAndroidBootstrap CreateBootstrap(UpdateServerOptions serverOptions)
    {
        var context = global::Android.App.Application.Context;
        var options = new AndroidUpdateOptions
        {
            DownloadDirectoryPath = Path.Combine(
                context.CacheDir?.AbsolutePath ?? Path.GetTempPath(),
                "update"),
            FileProviderAuthority = $"{context.PackageName}.generalupdate.fileprovider",
            UpdateServer = serverOptions
        };

        var bootstrap = GeneralUpdateBootstrap.CreateDefault(
            options,
            activityProvider: new CurrentActivityProvider(),
            eventDispatcher: new AvaloniaUpdateEventDispatcher(),
            logger: new AndroidUpdateLogger(),
            httpOptions: new HttpDownloadOptions
            {
                RequestTimeout = TimeSpan.FromSeconds(30),
                DownloadTimeout = TimeSpan.FromMinutes(15),
                MaxRetryAttempts = 3
            });

        bootstrap.AddListenerDownloadProgressChanged += (_, args) =>
        {
            DownloadProgressBar.Value = args.ProgressPercentage;
            ProgressText.Text =
                $"{args.ProgressPercentage:F1}%  {FormatBytes(args.DownloadSpeedBytesPerSecond)}/s";
        };
        bootstrap.AddListenerUpdateFailed += (_, args) =>
        {
            SetStatus(DescribeFailure(args.Result));
        };

        return bootstrap;
    }

    private async Task TryLaunchPendingInstallerAsync(CancellationToken cancellationToken)
    {
        if (_bootstrap is null ||
            _pendingPackage is null ||
            string.IsNullOrWhiteSpace(_pendingApkFilePath))
        {
            return;
        }

        var result = await _bootstrap.LaunchInstallerAsync(
            _pendingPackage,
            _pendingApkFilePath,
            cancellationToken);

        if (result.Success)
        {
            SetStatus("系统安装器已打开，请确认安装。是否完成安装由 Android 和用户决定，下次启动时会重新校验版本。");
            _pendingPackage = null;
            _pendingApkFilePath = null;
            return;
        }

        if (result.FailureReason == UpdateFailureReason.InstallPermissionDenied)
        {
            SetStatus("请开启“允许安装未知应用”。返回本应用后会自动再次拉起安装器。");
            OpenInstallPermissionSettings();
            return;
        }

        SetStatus(DescribeFailure(result));
    }

    private async void OnActivityResumed(object? sender, EventArgs e)
    {
        if (_isRunning ||
            _pendingPackage is null ||
            string.IsNullOrWhiteSpace(_pendingApkFilePath) ||
            !CanRequestPackageInstalls())
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            SetRunning(true);
            try
            {
                SetStatus("安装权限已授予，正在重新拉起系统安装器...");
                await TryLaunchPendingInstallerAsync(CancellationToken.None);
            }
            finally
            {
                SetRunning(false);
            }
        });
    }

    private bool TryReadServerOptions(out UpdateServerOptions options)
    {
        options = null!;
        if (!Uri.TryCreate(RequestUrlTextBox.Text, UriKind.Absolute, out var requestUri) ||
            (requestUri.Scheme != Uri.UriSchemeHttp && requestUri.Scheme != Uri.UriSchemeHttps))
        {
            SetStatus("请输入有效的 HTTP 或 HTTPS 验证接口地址。");
            return false;
        }

        if (!int.TryParse(PlatformTextBox.Text, out var platform) || platform <= 0)
        {
            SetStatus("Platform 必须是 GeneralSpacestation 中配置的正整数平台编号。");
            return false;
        }

        if (string.IsNullOrWhiteSpace(ProductIdTextBox.Text))
        {
            SetStatus("请输入 GeneralSpacestation 中的 ProductId。");
            return false;
        }

        options = new UpdateServerOptions
        {
            RequestUrl = requestUri.AbsoluteUri,
            AppKey = AppKeyTextBox.Text?.Trim() ?? string.Empty,
            AppType = 1,
            Platform = platform,
            ProductId = ProductIdTextBox.Text.Trim()
        };
        return true;
    }

    private static string GetCurrentVersion()
    {
        var context = global::Android.App.Application.Context;
        var packageManager = context.PackageManager;
        if (packageManager is null || string.IsNullOrWhiteSpace(context.PackageName))
        {
            return "0.0.0";
        }

        var packageInfo = packageManager.GetPackageInfo(
            context.PackageName,
            PackageInfoFlags.Activities);
        return packageInfo?.VersionName ?? "0.0.0";
    }

    private static bool CanRequestPackageInstalls()
    {
        if (global::Android.OS.Build.VERSION.SdkInt < global::Android.OS.BuildVersionCodes.O)
        {
            return true;
        }

        return global::Android.App.Application.Context.PackageManager?.CanRequestPackageInstalls() == true;
    }

    private static void OpenInstallPermissionSettings()
    {
        var activity = MainActivity.Current;
        var context = global::Android.App.Application.Context;
        if (activity is null || string.IsNullOrWhiteSpace(context.PackageName))
        {
            return;
        }

        var intent = new Intent(
            Settings.ActionManageUnknownAppSources,
            global::Android.Net.Uri.Parse($"package:{context.PackageName}"));
        activity.StartActivity(intent);
    }

    private void ResetOperation()
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = null;
        _bootstrap?.Dispose();
        _bootstrap = null;
        _pendingPackage = null;
        _pendingApkFilePath = null;
        _isForcedUpdate = false;
        DownloadProgressBar.Value = 0;
        ProgressText.Text = "0%";
        TargetVersionText.Text = "正在检查";
        ReleaseNotesText.Text = "检查到新版本后显示";
    }

    private void SetRunning(bool running)
    {
        _isRunning = running;
        StartButton.IsEnabled = !running;
        CancelButton.IsEnabled = running && !_isForcedUpdate;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private static string DescribeFailure(UpdateOperationResult result)
    {
        var detail = result.Exception?.Message ?? result.Message ?? "未知错误";
        return $"更新失败：{result.FailureReason}。{detail}";
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = Math.Max(0, bytes);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:F1} {units[unitIndex]}";
    }
}
