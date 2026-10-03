using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Sample.Infrastructure;

internal interface IUpdateHost
{
    UpdateLanguage LoadLanguage();
    void SaveLanguage(UpdateLanguage language);
    string GetCurrentVersion(UpdateLanguage language);
    UpdateServerOptions LoadServerOptions(UpdateLanguage language);
    void SaveServerOptions(UpdateServerOptions options, UpdateLanguage language);
    IAndroidBootstrap CreateBootstrap(UpdateServerOptions? options, UpdateLanguage language);
    bool CanRequestInstalls();
    void RequestInstallPermission(UpdateLanguage language);
}
