using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace GeneralUpdate.Avalonia.Android.Sample;

[global::Android.App.Application]
public sealed class MainApplication : AvaloniaAndroidApplication<App>
{
    public MainApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
