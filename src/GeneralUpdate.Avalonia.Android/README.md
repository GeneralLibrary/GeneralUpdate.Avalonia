# GeneralUpdate.Avalonia.Android

## 1. Overall architecture design

- `AndroidBootstrap` orchestrates validation, download, verification, and installer launching.
- `IVersionComparer` provides replaceable version comparison strategy (`SystemVersionComparer` by default).
- `IUpdateDownloader` encapsulates resumable HTTP download with sidecar metadata and smoothed speed reporting.
- `IHashValidator` validates package integrity (SHA256).
- `IApkInstaller` triggers Android package installer through `Intent` + `FileProvider`.
- `IFileStorage`, `IUpdateLogger`, `IUpdateEventDispatcher` isolate environment concerns.
- `IAndroidContextProvider` and `IAndroidActivityProvider` isolate Android lifecycle access.

## 2. Project directory structure

```text
src/GeneralUpdate.Avalonia.Android
├── Abstractions/
├── Enums/
├── Events/
├── Models/
├── Services/
├── GeneralUpdateBootstrap.cs
└── GeneralUpdate.Avalonia.Android.csproj
```

## 3-10. Core files

The implementation is split by files in the folders above and is intended to compile as a UI-free Android library.

## 11. Avalonia Android integration notes

Inject an event dispatcher to marshal callbacks to Avalonia UI thread:

```csharp
using GeneralUpdate.Avalonia.Android.Abstractions;

public sealed class AvaloniaUiDispatcher : IUpdateEventDispatcher
{
    public void Dispatch(Action callback)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(callback);
    }
}
```

## 12. AndroidManifest / FileProvider configuration example

```xml
<application ...>
  <provider
    android:name="androidx.core.content.FileProvider"
    android:authorities="com.example.app.generalupdate.fileprovider"
    android:exported="false"
    android:grantUriPermissions="true">
    <meta-data
      android:name="android.support.FILE_PROVIDER_PATHS"
      android:resource="@xml/generalupdate_file_paths" />
  </provider>
</application>
```

> If your Android manifest placeholders support `${applicationId}`, you can use `${applicationId}.generalupdate.fileprovider` instead.

`Resources/xml/generalupdate_file_paths.xml`

```xml
<?xml version="1.0" encoding="utf-8"?>
<paths>
  <cache-path name="update_cache" path="update/" />
  <files-path name="update_files" path="update/" />
</paths>
```

## 13. csproj for NuGet packaging

See `GeneralUpdate.Avalonia.Android.csproj`.

## 14. Minimal usage example

```csharp
using GeneralUpdate.Avalonia.Android;
using GeneralUpdate.Avalonia.Android.Models;

var options = new AndroidUpdateOptions
{
    DownloadDirectoryPath = Path.Combine(Android.App.Application.Context.CacheDir!.AbsolutePath!, "update"),
    FileProviderAuthority = "com.example.app.generalupdate.fileprovider"
};

var manager = GeneralUpdateBootstrap.CreateDefault(options, eventDispatcher: new AvaloniaUiDispatcher());

var check = await manager.ValidateAsync(packageInfo, currentVersion, ct);
if (check.UpdateFound)
{
    var prepared = await manager.DownloadAndVerifyAsync(packageInfo, ct);
    if (prepared.Success && prepared.FilePath is not null)
    {
        await manager.LaunchInstallerAsync(packageInfo, prepared.FilePath, ct);
    }
}
```

## 15. Extension suggestions

- Retry/backoff policy for transient network failures.
- WorkManager integration for background/constrained download.
- Delta update package support.
- Installer result listening via host lifecycle callbacks.
- Secondary signature validation in addition to SHA256.
