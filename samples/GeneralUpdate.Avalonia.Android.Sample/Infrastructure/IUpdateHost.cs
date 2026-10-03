using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Sample.Infrastructure;

internal interface IUpdateHost
{
    string GetCurrentVersion();
    UpdateServerOptions LoadServerOptions();
    void SaveServerOptions(UpdateServerOptions options);
    IAndroidBootstrap CreateBootstrap(UpdateServerOptions? options);
    bool CanRequestInstalls();
    void RequestInstallPermission();
}
