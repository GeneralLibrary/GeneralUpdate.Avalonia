using System.ComponentModel;
using System.Runtime.CompilerServices;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Models;
using GeneralUpdate.Avalonia.Android.Sample.Infrastructure;

namespace GeneralUpdate.Avalonia.Android.Sample.ViewModels;

internal sealed class UpdateViewModel(IUpdateHost host, IUpdateLogger logger) : INotifyPropertyChanged
{
    private IAndroidBootstrap? _bootstrap;
    private CancellationTokenSource? _cancellation;
    private Task _activeTask = Task.CompletedTask;
    private UpdatePreparationResult? _pending;
    private bool _waitingForPermission;
    private bool _forced;
    private bool _running;
    private string _requestUrl = "", _appKey = "", _platform = "", _productId = "";
    private string _currentVersion = "", _targetVersion = "尚未检查", _releaseNotes = "检查到新版本后显示";
    private string _status = "等待检查更新", _installationStatus = "正在核对上次升级结果...", _progressText = "0%";
    private double _progress;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string RequestUrl { get => _requestUrl; set => Set(ref _requestUrl, value ?? string.Empty); }
    public string AppKey { get => _appKey; set => Set(ref _appKey, value ?? string.Empty); }
    public string Platform { get => _platform; set => Set(ref _platform, value ?? string.Empty); }
    public string ProductId { get => _productId; set => Set(ref _productId, value ?? string.Empty); }
    public string CurrentVersion { get => _currentVersion; private set => Set(ref _currentVersion, value); }
    public string TargetVersion { get => _targetVersion; private set => Set(ref _targetVersion, value); }
    public string ReleaseNotes { get => _releaseNotes; private set => Set(ref _releaseNotes, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string InstallationStatus { get => _installationStatus; private set => Set(ref _installationStatus, value); }
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool CanStart => !_running;
    public bool CanCancel => _running && !_forced;

    public Task InitializeAsync() => RunAsync(async ct =>
    {
        ReplaceBootstrap(null);
        var reconciled = await RefreshInstallationAsync(ct);
        var options = host.LoadServerOptions();
        RequestUrl = options.RequestUrl;
        AppKey = options.AppKey;
        Platform = options.Platform.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ProductId = options.ProductId;
        ReplaceBootstrap(options);
        if (!reconciled) return;
        Status = "正在自动检查服务端版本...";
        var check = await _bootstrap!.ValidateAsync(CurrentVersion, ct);
        if (!check.Success) { Status = Describe(check); return; }
        TargetVersion = check.UpdateFound ? check.PackageInfo!.Version : "已是最新版本";
        ReleaseNotes = check.PackageInfo?.Description ?? "服务端未提供更新说明。";
        Status = check.UpdateFound ? "发现新版本，点击“检查并自动升级”开始下载和安装。" : "当前已经是最新版本。";
    });

    public Task StartAsync() => RunAsync(async ct =>
    {
        _pending = null;
        _waitingForPermission = false;
        var options = ReadOptions();
        host.SaveServerOptions(options);
        ReplaceBootstrap(options);
        Progress = 0;
        ProgressText = "0%";
        if (!await RefreshInstallationAsync(ct)) return;

        Status = "正在检查并准备升级包...";
        var prepared = await _bootstrap!.PrepareUpdateAsync(CurrentVersion, ct);
        if (!prepared.Success) { Status = Describe(prepared); return; }
        if (!prepared.IsReadyToInstall)
        {
            TargetVersion = "已是最新版本";
            Status = "当前已经是最新版本。";
            return;
        }
        _pending = prepared;
        await LaunchPendingAsync(ct);
    });

    public Task ResumeAsync() => RunAsync(async ct =>
    {
        if (_bootstrap is null) return;
        if (_waitingForPermission && _pending is not null && host.CanRequestInstalls())
        {
            _waitingForPermission = false;
            await LaunchPendingAsync(ct);
        }
        else
        {
            await RefreshInstallationAsync(ct);
        }
    });

    public Task ResetInstallationAsync() => RunAsync(async ct =>
    {
        if (_bootstrap is null) ReplaceBootstrap(null);
        var result = await _bootstrap!.ResetInstallationAsync(ct);
        if (!result.Success) { Status = Describe(result); return; }
        _pending = null;
        _waitingForPermission = false;
        if (await RefreshInstallationAsync(ct))
            Status = "升级记录已重置，未修改已安装应用。可以重新检查升级。";
    });

    public void Cancel() => _cancellation?.Cancel();

    public async Task DeactivateAsync()
    {
        Cancel();
        await _activeTask;
        _bootstrap?.Dispose();
        _bootstrap = null;
        _pending = null;
        _waitingForPermission = false;
    }

    private Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (_running) return Task.CompletedTask;
        _activeTask = RunCoreAsync(operation);
        return _activeTask;
    }

    private async Task RunCoreAsync(Func<CancellationToken, Task> operation)
    {
        _running = true;
        _forced = false;
        NotifyAvailability();
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Status = "更新操作已取消。";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException)
        {
            logger.LogError("Update host operation failed.", ex);
            Status = $"更新操作失败：{ex.Message}";
        }
        finally
        {
            _cancellation = null;
            _running = false;
            NotifyAvailability();
        }
    }

    private void ReplaceBootstrap(UpdateServerOptions? options)
    {
        var bootstrap = host.CreateBootstrap(options);
        _bootstrap?.Dispose();
        _bootstrap = bootstrap;
        _bootstrap.AddListenerValidate += (_, args) =>
        {
            if (!ReferenceEquals(_bootstrap, bootstrap)) return;
            _forced = args.PackageInfo.IsForced;
            NotifyAvailability();
            TargetVersion = args.PackageInfo.Version;
            ReleaseNotes = args.PackageInfo.Description ?? "服务端未提供更新说明。";
        };
        _bootstrap.AddListenerDownloadProgressChanged += (_, args) =>
        {
            if (!ReferenceEquals(_bootstrap, bootstrap)) return;
            Progress = args.ProgressPercentage;
            ProgressText = $"{args.ProgressPercentage:F1}%  {args.DownloadSpeedBytesPerSecond / 1024:F1} KB/s";
        };
        _bootstrap.AddListenerInstallationConfirmed += (_, args) =>
            logger.LogInformation($"Installation confirmed: {args.Result.CurrentVersion}.");
    }

    private async Task LaunchPendingAsync(CancellationToken ct)
    {
        var pending = _pending;
        if (_bootstrap is null || pending is not { IsReadyToInstall: true }) return;
        var result = await _bootstrap.LaunchInstallerAsync(pending.PackageInfo!, pending.FilePath!, ct);
        var reconciled = await RefreshInstallationAsync(CancellationToken.None);
        if (result.Success)
        {
            _pending = null;
            _waitingForPermission = false;
            if (reconciled) Status = "系统安装器已打开，请确认安装。重新打开应用后会核对升级结果。";
        }
        else if (reconciled && result.FailureReason == UpdateFailureReason.InstallPermissionDenied)
        {
            _waitingForPermission = true;
            Status = "请开启“允许安装未知应用”，返回后会重试安装。";
            host.RequestInstallPermission();
        }
        else if (reconciled)
        {
            _waitingForPermission = false;
            Status = Describe(result);
        }
    }

    private async Task<bool> RefreshInstallationAsync(CancellationToken ct)
    {
        CurrentVersion = host.GetCurrentVersion();
        var result = await _bootstrap!.CheckInstallationAsync(CurrentVersion, ct);
        InstallationStatus = !result.Success ? Describe(result)
            : result.IsInstalled ? $"升级已确认：目标 {result.Record!.TargetVersion}，当前 {CurrentVersion}。"
            : result.HasPendingInstallation ? $"升级尚未确认：目标 {result.Record!.TargetVersion}，当前 {CurrentVersion}。可以重新检查并重试。"
            : "暂无升级记录。";
        if (!result.Success) Status = Describe(result) + " 如记录损坏，可显式重置升级记录。";
        return result.Success;
    }

    private UpdateServerOptions ReadOptions()
    {
        if (!Uri.TryCreate(RequestUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("请输入有效的 HTTP 或 HTTPS 验证接口地址。");
        if (!int.TryParse(Platform, out var platform) || platform <= 0)
            throw new ArgumentException("Platform 必须是服务端配置的正整数平台编号。");
        if (string.IsNullOrWhiteSpace(ProductId))
            throw new ArgumentException("请输入 ProductId。");
        return new UpdateServerOptions
        {
            RequestUrl = uri.AbsoluteUri,
            AppKey = AppKey.Trim(),
            AppType = 1,
            Platform = platform,
            ProductId = ProductId.Trim()
        };
    }

    private static string Describe(UpdateOperationResult result) =>
        result.State == UpdateState.Canceled ? "更新操作已取消。"
            : $"更新失败：{result.FailureReason}。{result.Exception?.Message ?? result.Message}";

    private void NotifyAvailability()
    {
        PropertyChanged?.Invoke(this, new(nameof(CanStart)));
        PropertyChanged?.Invoke(this, new(nameof(CanCancel)));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}
