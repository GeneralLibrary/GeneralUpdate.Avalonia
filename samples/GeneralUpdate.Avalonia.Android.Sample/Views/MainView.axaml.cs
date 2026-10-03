using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GeneralUpdate.Avalonia.Android.Sample.Infrastructure;
using GeneralUpdate.Avalonia.Android.Sample.ViewModels;

namespace GeneralUpdate.Avalonia.Android.Sample.Views;

public partial class MainView : UserControl
{
    private readonly UpdateViewModel _viewModel;
    private Task _lifecycle = Task.CompletedTask;
    private bool _attached;

    public MainView()
    {
        InitializeComponent();
        var logger = new AndroidUpdateLogger();
        DataContext = _viewModel = new UpdateViewModel(new AndroidUpdateHost(logger), logger);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        MainActivity.Resumed += OnActivityResumed;
        _lifecycle = InitializeAfterAsync(_lifecycle);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        MainActivity.Resumed -= OnActivityResumed;
        _viewModel.Cancel();
        _lifecycle = DeactivateAfterAsync(_lifecycle);
        base.OnDetachedFromVisualTree(e);
    }

    private async Task InitializeAfterAsync(Task previous)
    {
        await previous;
        if (_attached) await _viewModel.InitializeAsync();
    }

    private async Task DeactivateAfterAsync(Task previous)
    {
        await previous;
        await _viewModel.DeactivateAsync();
    }

    private async void OnActivityResumed(object? sender, EventArgs e)
    {
        await _lifecycle;
        if (_attached) await _viewModel.ResumeAsync();
    }

    private async void OnStartUpdate(object? sender, RoutedEventArgs e)
    {
        await _lifecycle;
        if (_attached) await _viewModel.StartAsync();
    }
    private void OnCancelUpdate(object? sender, RoutedEventArgs e) => _viewModel.Cancel();
    private async void OnResetInstallation(object? sender, RoutedEventArgs e)
    {
        await _lifecycle;
        if (_attached) await _viewModel.ResetInstallationAsync();
    }
}
