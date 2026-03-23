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
using GeneralUpdate.Avalonia.Android.Abstractions;
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

## 15. Automatic upgrade process (end-to-end)

The library executes the upgrade process in four explicit phases:

1. **Validate**
   - Call `ValidateAsync(packageInfo, currentVersion, ct)`.
   - Uses `IVersionComparer` to compare `currentVersion` vs `packageInfo.Version`.
   - If a newer version is detected, `AddListenerValidate` is raised.
   - Returns `UpdateCheckResult` with:
     - `Success`, `UpdateFound`, `State`, `FailureReason`, `Message`
     - `CurrentVersion`, `TargetVersion`, `PackageInfo`

2. **Download + Resume + Verify**
   - Call `DownloadAndVerifyAsync(packageInfo, ct)`.
   - Downloader streams to disk, supports `.part` resume metadata, and reports progress continuously.
   - After download, SHA256 integrity is validated.
   - Returns `UpdateOperationResult`:
     - success path: `State = ReadyToInstall`, `FilePath` is APK path
     - failure path: includes `FailureReason`, `Message`, optional `Exception`

3. **Install trigger**
   - Call `LaunchInstallerAsync(packageInfo, apkFilePath, ct)`.
   - Uses Android `Intent` + `FileProvider` URI.
   - Returns `InstallResult` (inherits `UpdateOperationResult`) with launch outcome.

4. **Host-side interaction**
   - This library does not show any UI.
   - Host app decides how to render progress/errors and when to call each phase.

## 16. Event notifications and payload content

Public events are:

- `AddListenerValidate` (`ValidateEventArgs`)
  - `PackageInfo`: target package metadata (`Version`, `DownloadUrl`, `Sha256`, etc.)
  - `CurrentVersion`: host-provided current app version string

- `AddListenerDownloadProgressChanged` (`DownloadProgressChangedEventArgs`)
  - `DownloadSpeedBytesPerSecond`
  - `DownloadedBytes`
  - `RemainingBytes`
  - `TotalBytes`
  - `ProgressPercentage`
  - `PackageInfo`
  - `StatusDescription`

- `AddListenerUpdateCompleted` (`UpdateCompletedEventArgs`)
  - `Result` (`UpdateOperationResult`): `Success`, `State`, `FailureReason`, `Message`, `PackageInfo`, `FilePath`, `Exception`

- `AddListenerUpdateFailed` (`UpdateFailedEventArgs`)
  - `Result` (`UpdateOperationResult`): same shape as above, populated for failure/cancel scenarios

## 17. Complete sample code (subscribe + orchestrate)

```csharp
using GeneralUpdate.Avalonia.Android;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Enums;
using GeneralUpdate.Avalonia.Android.Models;

public sealed class AvaloniaUiDispatcher : IUpdateEventDispatcher
{
    public void Dispatch(Action callback)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(callback);
    }
}

public static class AndroidUpdateRunner
{
    public static async Task RunAsync(CancellationToken ct)
    {
        // 1) Build package metadata from your update API response.
        var packageInfo = new UpdatePackageInfo
        {
            Version = "2.3.0",
            VersionName = "2.3.0",
            Description = "Bug fixes and performance improvements.",
            DownloadUrl = "https://example.com/app-release.apk",
            FileSize = 52_428_800,
            Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            PublishTime = DateTimeOffset.UtcNow,
            IsForced = false,
            FileName = "app-release.apk"
        };

        // 2) Configure core bootstrap.
        var options = new AndroidUpdateOptions
        {
            DownloadDirectoryPath = Path.Combine(
                Android.App.Application.Context.CacheDir!.AbsolutePath!,
                "update"),
            FileProviderAuthority = "com.example.app.generalupdate.fileprovider"
        };

        var bootstrap = GeneralUpdateBootstrap.CreateDefault(
            options,
            eventDispatcher: new AvaloniaUiDispatcher());

        // 3) Subscribe to notifications.
        bootstrap.AddListenerValidate += (_, e) =>
        {
            Console.WriteLine($"Update found: {e.CurrentVersion} -> {e.PackageInfo.Version}");
        };

        bootstrap.AddListenerDownloadProgressChanged += (_, e) =>
        {
            Console.WriteLine(
                $"Downloading {e.PackageInfo.FileName ?? "package"}: " +
                $"{e.ProgressPercentage:F2}% " +
                $"({e.DownloadedBytes}/{e.TotalBytes} bytes), " +
                $"speed={e.DownloadSpeedBytesPerSecond:F0} B/s, " +
                $"remaining={e.RemainingBytes}, status={e.StatusDescription}");
        };

        bootstrap.AddListenerUpdateCompleted += (_, e) =>
        {
            Console.WriteLine(
                $"Completed: success={e.Result.Success}, state={e.Result.State}, " +
                $"message={e.Result.Message}, file={e.Result.FilePath}");
        };

        bootstrap.AddListenerUpdateFailed += (_, e) =>
        {
            Console.WriteLine(
                $"Failed: state={e.Result.State}, reason={e.Result.FailureReason}, " +
                $"message={e.Result.Message}, ex={e.Result.Exception?.Message}");
        };

        // 4) Validate current version.
        //    Replace with your real app version source (e.g., PackageInfo.VersionName).
        var currentVersion = "2.2.1";
        var validateResult = await bootstrap.ValidateAsync(packageInfo, currentVersion, ct);

        if (!validateResult.Success)
        {
            Console.WriteLine($"Validate failed: {validateResult.Message}");
            return;
        }

        if (!validateResult.UpdateFound)
        {
            Console.WriteLine("No update available.");
            return;
        }

        // 5) Download and verify.
        var prepareResult = await bootstrap.DownloadAndVerifyAsync(packageInfo, ct);
        if (!prepareResult.Success || string.IsNullOrWhiteSpace(prepareResult.FilePath))
        {
            Console.WriteLine(
                $"Prepare failed: reason={prepareResult.FailureReason}, message={prepareResult.Message}");
            return;
        }

        // 6) Launch Android installer.
        var installResult = await bootstrap.LaunchInstallerAsync(packageInfo, prepareResult.FilePath, ct);
        if (!installResult.Success)
        {
            Console.WriteLine(
                $"Installer launch failed: reason={installResult.FailureReason}, message={installResult.Message}");
        }

        // Optional: inspect current state snapshot at any time.
        var snapshot = bootstrap.GetSnapshot();
        if (snapshot.State == UpdateState.Failed || snapshot.State == UpdateState.Canceled)
        {
            Console.WriteLine($"Snapshot: {snapshot.State}, reason={snapshot.FailureReason}, msg={snapshot.Message}");
        }
    }
}
```

## 18. Extension suggestions

- Retry/backoff policy for transient network failures.
- WorkManager integration for background/constrained download.
- Delta update package support.
- Installer result listening via host lifecycle callbacks.
- Secondary signature validation in addition to SHA256.
