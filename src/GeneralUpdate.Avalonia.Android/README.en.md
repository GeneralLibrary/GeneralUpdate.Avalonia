# GeneralUpdate.Avalonia.Android

UI-free Android auto-update core for Avalonia 12+ apps (`net8.0-android`).

## Features

- Android-only update core (`net8.0-android`; compatible design for `net9.0-android`)
- No built-in UI (host app owns dialogs/progress/error rendering)
- End-to-end orchestration: validate → download/resume → hash verify → installer launch
- Resumable HTTP download with sidecar metadata and streaming writes
- Replaceable abstractions for version comparison, downloader, hash validator, installer, logger, and event dispatcher

## Core API

- `GeneralUpdateBootstrap.CreateDefault(...)`
- `IAndroidBootstrap.ValidateAsync(...)`
- `IAndroidBootstrap.DownloadAndVerifyAsync(...)`
- `IAndroidBootstrap.LaunchInstallerAsync(...)`

Events:

- `AddListenerValidate`
- `AddListenerDownloadProgressChanged`
- `AddListenerUpdateCompleted`
- `AddListenerUpdateFailed`

## Quick Start

```csharp
using GeneralUpdate.Avalonia.Android;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Models;

public sealed class AvaloniaUiDispatcher : IUpdateEventDispatcher
{
    public void Dispatch(Action callback)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(callback);
    }
}

var options = new AndroidUpdateOptions
{
    DownloadDirectoryPath = Path.Combine(Android.App.Application.Context.CacheDir!.AbsolutePath!, "update"),
    FileProviderAuthority = "com.example.app.generalupdate.fileprovider"
};

var bootstrap = GeneralUpdateBootstrap.CreateDefault(
    options,
    eventDispatcher: new AvaloniaUiDispatcher());

var packageInfo = new UpdatePackageInfo
{
    Version = "2.3.0",
    DownloadUrl = "https://example.com/app-release.apk",
    Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
    FileSize = 52_428_800,
    FileName = "app-release.apk"
};

var check = await bootstrap.ValidateAsync(packageInfo, "2.2.1", CancellationToken.None);
if (check.UpdateFound)
{
    var prepared = await bootstrap.DownloadAndVerifyAsync(packageInfo, CancellationToken.None);
    if (prepared.Success && prepared.FilePath is not null)
    {
        await bootstrap.LaunchInstallerAsync(packageInfo, prepared.FilePath, CancellationToken.None);
    }
}
```

## Event payloads

- `ValidateEventArgs`: `PackageInfo`, `CurrentVersion`
- `DownloadProgressChangedEventArgs`: speed, downloaded bytes, remaining bytes, total bytes, percentage, package info, status
- `UpdateCompletedEventArgs` / `UpdateFailedEventArgs`: `Result` (`UpdateOperationResult`)

## Android FileProvider setup

```xml
<provider
  android:name="androidx.core.content.FileProvider"
  android:authorities="com.example.app.generalupdate.fileprovider"
  android:exported="false"
  android:grantUriPermissions="true">
  <meta-data
    android:name="android.support.FILE_PROVIDER_PATHS"
    android:resource="@xml/generalupdate_file_paths" />
</provider>
```

`Resources/xml/generalupdate_file_paths.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<paths>
  <cache-path name="update_cache" path="update/" />
  <files-path name="update_files" path="update/" />
</paths>
```
