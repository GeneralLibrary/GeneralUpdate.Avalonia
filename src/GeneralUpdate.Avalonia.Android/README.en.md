<p align="center">
  <img src="https://raw.githubusercontent.com/GeneralLibrary/GeneralUpdate.Avalonia/main/imgs/banner.png" alt="GeneralUpdate.Avalonia">
</p>

# GeneralUpdate.Avalonia.Android

UI-free Android auto-update core for Avalonia 12+ apps (`net10.0-android`).

## Features

- No built-in UI (host app owns dialogs/progress/error rendering)
- End-to-end orchestration: validate → resume-download → SHA-256 verify → install
- Resumable HTTP download with sidecar metadata and smoothed speed reporting
- Replaceable abstractions for every pipeline stage
- Operation serialization — concurrent calls are gated, safe from any thread

## Quick Start

```bash
dotnet add package GeneralUpdate.Avalonia.Android
```

```csharp
using GeneralUpdate.Avalonia.Android;
using GeneralUpdate.Avalonia.Android.Models;

var options = new AndroidUpdateOptions
{
    DownloadDirectoryPath = Path.Combine(
        Android.App.Application.Context.CacheDir!.AbsolutePath!, "update"),
    FileProviderAuthority = "com.example.app.generalupdate.fileprovider"
};

using IAndroidBootstrap bootstrap = GeneralUpdateBootstrap.CreateDefault(options);

var packageInfo = new UpdatePackageInfo
{
    Version     = "2.3.0",
    DownloadUrl = "https://example.com/app-release.apk",
    Sha256      = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
    FileSize    = 52_428_800,
    FileName    = "app-release.apk"
};

var check = await bootstrap.ValidateAsync(packageInfo, "2.2.1", CancellationToken.None);
if (check.UpdateFound)
{
    var result = await bootstrap.DownloadAndVerifyAsync(packageInfo, CancellationToken.None);
    if (result.Success && result.FilePath is not null)
    {
        await bootstrap.LaunchInstallerAsync(packageInfo, result.FilePath, CancellationToken.None);
    }
}
```

## API

### Factory

`GeneralUpdateBootstrap.CreateDefault(options, contextProvider?, activityProvider?, httpClient?, versionComparer?, eventDispatcher?, logger?)`

Default wiring:

| Abstraction | Default |
|---|---|
| `IAndroidContextProvider` | `DefaultAndroidContextProvider` |
| `IAndroidActivityProvider` | `NullAndroidActivityProvider` |
| `IUpdateLogger` | `NoOpUpdateLogger` |
| `IFileStorage` | `PhysicalFileStorage` |
| `IUpdateDownloader` | `HttpResumableApkDownloader` |
| `IHashValidator` | `Sha256HashValidator` |
| `IApkInstaller` | `AndroidApkInstaller` |
| `IVersionComparer` | `SystemVersionComparer` |

### IAndroidBootstrap Methods

| Method | Returns |
|---|---|
| `ValidateAsync(packageInfo, currentVersion, ct)` | `UpdateCheckResult` |
| `DownloadAndVerifyAsync(packageInfo, ct)` | `UpdateOperationResult` |
| `LaunchInstallerAsync(packageInfo, apkFilePath, ct)` | `InstallResult` |
| `GetSnapshot()` | `UpdateStateSnapshot` |

### Events

| Event | Args |
|---|---|
| `AddListenerValidate` | `ValidateEventArgs` |
| `AddListenerDownloadProgressChanged` | `DownloadProgressChangedEventArgs` |
| `AddListenerUpdateCompleted` | `UpdateCompletedEventArgs` |
| `AddListenerUpdateFailed` | `UpdateFailedEventArgs` |

### Model Hierarchy

```
UpdateOperationResult (base)
├── Success, State, FailureReason, Message, PackageInfo, FilePath, Exception
├── UpdateCheckResult  →  + UpdateFound, CurrentVersion
├── DownloadResult
├── HashValidationResult  →  + ActualSha256, ExpectedSha256
└── InstallResult
```

### Enums

`UpdateState`: `None`, `Checking`, `UpdateAvailable`, `Downloading`, `Verifying`, `ReadyToInstall`, `Installing`, `Completed`, `Failed`, `Canceled`

`UpdateFailureReason`: `None`, `NetworkError`, `Canceled`, `InvalidMetadata`, `FileIoError`, `HashMismatch`, `ServerDoesNotSupportRange`, `InstallPermissionDenied`, `InstallLaunchFailed`, `VersionComparisonFailed`, `Unknown`

## Android FileProvider Setup

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
