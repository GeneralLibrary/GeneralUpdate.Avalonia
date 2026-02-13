using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeneralUpdate.Avalonia.Mobile.Services;
using System.Threading.Tasks;

namespace GeneralUpdate.Avalonia.Mobile.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly UpdateService _updateService;

    [ObservableProperty]
    private string _greeting = "欢迎使用 Avalonia 移动端自动更新\nWelcome to Avalonia Mobile Auto-Update";

    [ObservableProperty]
    private string _currentVersion = "1.0.0";

    [ObservableProperty]
    private string _updateStatus = "准备就绪 / Ready";

    [ObservableProperty]
    private int _downloadProgress = 0;

    [ObservableProperty]
    private bool _isCheckingUpdate = false;

    [ObservableProperty]
    private bool _isDownloading = false;

    [ObservableProperty]
    private bool _hasUpdate = false;

    public MainViewModel()
    {
        _updateService = new UpdateService();
        _updateService.StatusChanged += OnUpdateStatusChanged;
        _updateService.ProgressChanged += OnUpdateProgressChanged;
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingUpdate = true;
        HasUpdate = false;

        // Initialize update service with your server URL
        // 使用您的服务器 URL 初始化更新服务
        _updateService.Initialize(
            updateUrl: "https://your-update-server.com/updates",
            appName: "GeneralUpdate.Avalonia.Mobile",
            currentVersion: CurrentVersion
        );

        HasUpdate = await _updateService.CheckForUpdatesAsync();
        IsCheckingUpdate = false;
    }

    [RelayCommand]
    private async Task DownloadAndInstallAsync()
    {
        IsDownloading = true;
        DownloadProgress = 0;

        await _updateService.DownloadAndInstallAsync();

        IsDownloading = false;
    }

    private void OnUpdateStatusChanged(object? sender, string status)
    {
        UpdateStatus = status;
    }

    private void OnUpdateProgressChanged(object? sender, ProgressEventArgs args)
    {
        DownloadProgress = args.ProgressPercentage;
        if (!string.IsNullOrEmpty(args.CurrentFile))
        {
            UpdateStatus = $"正在下载 / Downloading: {args.CurrentFile} ({args.ProgressPercentage}%)";
        }
    }
}
