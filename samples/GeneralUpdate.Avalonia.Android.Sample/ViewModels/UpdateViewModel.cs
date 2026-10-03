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
    private bool _initialized;
    private UpdateLanguage _language = UpdateLanguage.English;
    private UpdateServerOptions? _serverOptions;
    private string _requestUrl = "", _appKey = "", _platform = "", _productId = "";
    private string _currentVersion = "", _progressText = "0%";
    private string? _targetVersion, _releaseNotes;
    private string _targetVersionFallback = "Not checked", _releaseNotesFallback = "Shown when an update is found";
    private string _statusKey = "Waiting for update check", _installationStatusKey = "Reconciling previous update...";
    private object?[] _statusArguments = [], _installationStatusArguments = [];
    private double _progress;

    public event PropertyChangedEventHandler? PropertyChanged;
    public UpdateLanguage Language
    {
        get => _language;
        set
        {
            if (_language == value || _running) return;
            _language = value;
            NotifyLanguage();
            if (!_initialized) return;
            try
            {
                host.SaveLanguage(value);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogError("Unable to save language preference.", ex);
                SetStatus("Unable to save language preference.");
            }
            if (_bootstrap is not null) ReplaceBootstrap(_serverOptions);
        }
    }
    public int LanguageIndex
    {
        get => Language == UpdateLanguage.Chinese ? 1 : 0;
        set => Language = value == 1 ? UpdateLanguage.Chinese : UpdateLanguage.English;
    }
    public string LanguageLabel => Text("Language");
    public string Title => Text("GeneralUpdate mobile updater");
    public string Description => Text("Connect to GeneralSpacestation to check versions, download APKs, verify SHA-256, and install.");
    public string CurrentVersionLabel => Text("Current version:");
    public string TargetVersionLabel => Text("Target version:");
    public string SettingsTitle => Text("GeneralSpacestation settings");
    public string VerificationEndpointLabel => Text("Verification endpoint");
    public string AppKeyPlaceholder => Text("AppKey configured in admin console");
    public string PlatformPlaceholder => Text("Android platform number");
    public string ProductIdPlaceholder => Text("Product ID");
    public string ReleaseNotesLabel => Text("Release notes");
    public string StartButtonText => Text("Check and update");
    public string CancelButtonText => Text("Cancel");
    public string ResetButtonText => Text("Reset update record (does not modify installed app)");
    public bool CanChangeLanguage => !_running;
    public string RequestUrl { get => _requestUrl; set => Set(ref _requestUrl, value ?? string.Empty); }
    public string AppKey { get => _appKey; set => Set(ref _appKey, value ?? string.Empty); }
    public string Platform { get => _platform; set => Set(ref _platform, value ?? string.Empty); }
    public string ProductId { get => _productId; set => Set(ref _productId, value ?? string.Empty); }
    public string CurrentVersion { get => _currentVersion; private set => Set(ref _currentVersion, value); }
    public string TargetVersion => _targetVersion ?? Text(_targetVersionFallback);
    public string ReleaseNotes => _releaseNotes ?? Text(_releaseNotesFallback);
    public string Status => UpdateViewText.Format(Language, _statusKey, _statusArguments);
    public string InstallationStatus => UpdateViewText.Format(Language, _installationStatusKey, _installationStatusArguments);
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool CanStart => !_running;
    public bool CanCancel => _running && !_forced;

    public Task InitializeAsync() => RunAsync(async ct =>
    {
        _language = host.LoadLanguage();
        NotifyLanguage();
        _initialized = true;
        ReplaceBootstrap(null);
        var reconciled = await RefreshInstallationAsync(ct);
        var options = host.LoadServerOptions(Language);
        RequestUrl = options.RequestUrl;
        AppKey = options.AppKey;
        Platform = options.Platform.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ProductId = options.ProductId;
        _serverOptions = options;
        ReplaceBootstrap(options);
        if (!reconciled) return;
        SetStatus("Checking server version automatically...");
        var check = await _bootstrap!.ValidateAsync(CurrentVersion, ct);
        if (!check.Success) { SetFailureStatus(check); return; }
        SetTargetVersion(check.UpdateFound ? check.PackageInfo!.Version : null, "Latest version");
        SetReleaseNotes(check.PackageInfo?.Description, "The server did not provide release notes.");
        SetStatus(check.UpdateFound
            ? "New version found. Select “Check and update” to download and install."
            : "The app is up to date.");
    });

    public Task StartAsync() => RunAsync(async ct =>
    {
        _pending = null;
        _waitingForPermission = false;
        var options = ReadOptions();
        host.SaveServerOptions(options, Language);
        _serverOptions = options;
        ReplaceBootstrap(options);
        Progress = 0;
        ProgressText = "0%";
        if (!await RefreshInstallationAsync(ct)) return;

        SetStatus("Checking and preparing update...");
        var prepared = await _bootstrap!.PrepareUpdateAsync(CurrentVersion, ct);
        if (!prepared.Success) { SetFailureStatus(prepared); return; }
        if (!prepared.IsReadyToInstall)
        {
            SetTargetVersion(null, "Latest version");
            SetStatus("The app is up to date.");
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
        if (!result.Success) { SetFailureStatus(result); return; }
        _pending = null;
        _waitingForPermission = false;
        if (await RefreshInstallationAsync(ct))
            SetStatus("Update record reset. The installed app was not changed. You can check for updates again.");
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
            SetStatus("Update operation canceled.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException)
        {
            logger.LogError("Update host operation failed.", ex);
            SetStatus("Update operation failed: {0}", ex.Message);
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
        var bootstrap = host.CreateBootstrap(options, Language);
        _bootstrap?.Dispose();
        _bootstrap = bootstrap;
        _bootstrap.AddListenerValidate += (_, args) =>
        {
            if (!ReferenceEquals(_bootstrap, bootstrap)) return;
            _forced = args.PackageInfo.IsForced;
            NotifyAvailability();
            SetTargetVersion(args.PackageInfo.Version, "Not checked");
            SetReleaseNotes(args.PackageInfo.Description, "The server did not provide release notes.");
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
            if (reconciled) SetStatus("System installer opened. Confirm installation; the app will check the result when reopened.");
        }
        else if (reconciled && result.FailureReason == UpdateFailureReason.InstallPermissionDenied)
        {
            _waitingForPermission = true;
            SetStatus("Allow installation from unknown sources, then return to retry.");
            host.RequestInstallPermission(Language);
        }
        else if (reconciled)
        {
            _waitingForPermission = false;
            SetFailureStatus(result);
        }
    }

    private async Task<bool> RefreshInstallationAsync(CancellationToken ct)
    {
        CurrentVersion = host.GetCurrentVersion(Language);
        var result = await _bootstrap!.CheckInstallationAsync(CurrentVersion, ct);
        if (!result.Success)
        {
            var message = UpdateViewText.Format(Language, "Update failed ({0}): {1}",
                result.FailureReason, result.Exception?.Message ?? result.Message);
            SetInstallationStatus("Update error: {0}. If the record is corrupt, explicitly reset the update record.", message);
            SetStatus("Update error: {0}. If the record is corrupt, explicitly reset the update record.", message);
        }
        else if (result.IsInstalled)
            SetInstallationStatus("Update confirmed: target {0}, current {1}.", result.Record!.TargetVersion, CurrentVersion);
        else if (result.HasPendingInstallation)
            SetInstallationStatus("Update not confirmed: target {0}, current {1}. You can check and retry.", result.Record!.TargetVersion, CurrentVersion);
        else
            SetInstallationStatus("No update record.");
        return result.Success;
    }

    private UpdateServerOptions ReadOptions()
    {
        if (!Uri.TryCreate(RequestUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException(Text("Enter a valid HTTP or HTTPS verification URL."));
        if (!int.TryParse(Platform, out var platform) || platform <= 0)
            throw new ArgumentException(Text("Platform must be a positive integer configured on the server."));
        if (string.IsNullOrWhiteSpace(ProductId))
            throw new ArgumentException(Text("Enter a ProductId."));
        return new UpdateServerOptions
        {
            RequestUrl = uri.AbsoluteUri,
            AppKey = AppKey.Trim(),
            AppType = 1,
            Platform = platform,
            ProductId = ProductId.Trim()
        };
    }

    private void SetFailureStatus(UpdateOperationResult result) =>
        SetStatus(result.State == UpdateState.Canceled
            ? "Update operation canceled."
            : "Update failed ({0}): {1}", result.FailureReason, result.Exception?.Message ?? result.Message);

    private string Text(string key) => UpdateViewText.Get(Language, key);

    private void SetStatus(string key, params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        PropertyChanged?.Invoke(this, new(nameof(Status)));
    }

    private void SetInstallationStatus(string key, params object?[] arguments)
    {
        _installationStatusKey = key;
        _installationStatusArguments = arguments;
        PropertyChanged?.Invoke(this, new(nameof(InstallationStatus)));
    }

    private void SetTargetVersion(string? value, string fallbackKey)
    {
        _targetVersion = value;
        _targetVersionFallback = fallbackKey;
        PropertyChanged?.Invoke(this, new(nameof(TargetVersion)));
    }

    private void SetReleaseNotes(string? value, string fallbackKey)
    {
        _releaseNotes = value;
        _releaseNotesFallback = fallbackKey;
        PropertyChanged?.Invoke(this, new(nameof(ReleaseNotes)));
    }

    private void NotifyLanguage()
    {
        PropertyChanged?.Invoke(this, new(nameof(Language)));
        PropertyChanged?.Invoke(this, new(nameof(LanguageIndex)));
        PropertyChanged?.Invoke(this, new(nameof(LanguageLabel)));
        PropertyChanged?.Invoke(this, new(nameof(Title)));
        PropertyChanged?.Invoke(this, new(nameof(Description)));
        PropertyChanged?.Invoke(this, new(nameof(CurrentVersionLabel)));
        PropertyChanged?.Invoke(this, new(nameof(TargetVersionLabel)));
        PropertyChanged?.Invoke(this, new(nameof(SettingsTitle)));
        PropertyChanged?.Invoke(this, new(nameof(VerificationEndpointLabel)));
        PropertyChanged?.Invoke(this, new(nameof(AppKeyPlaceholder)));
        PropertyChanged?.Invoke(this, new(nameof(PlatformPlaceholder)));
        PropertyChanged?.Invoke(this, new(nameof(ProductIdPlaceholder)));
        PropertyChanged?.Invoke(this, new(nameof(ReleaseNotesLabel)));
        PropertyChanged?.Invoke(this, new(nameof(StartButtonText)));
        PropertyChanged?.Invoke(this, new(nameof(CancelButtonText)));
        PropertyChanged?.Invoke(this, new(nameof(ResetButtonText)));
        PropertyChanged?.Invoke(this, new(nameof(TargetVersion)));
        PropertyChanged?.Invoke(this, new(nameof(ReleaseNotes)));
        PropertyChanged?.Invoke(this, new(nameof(Status)));
        PropertyChanged?.Invoke(this, new(nameof(InstallationStatus)));
    }

    private void NotifyAvailability()
    {
        PropertyChanged?.Invoke(this, new(nameof(CanStart)));
        PropertyChanged?.Invoke(this, new(nameof(CanCancel)));
        PropertyChanged?.Invoke(this, new(nameof(CanChangeLanguage)));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}
