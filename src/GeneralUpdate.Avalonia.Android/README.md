<p align="center">
  <img src="https://raw.githubusercontent.com/GeneralLibrary/GeneralUpdate.Avalonia/main/imgs/banner.png" alt="GeneralUpdate.Avalonia">
</p>

# GeneralUpdate.Avalonia.Android

[![NuGet](https://img.shields.io/nuget/v/GeneralUpdate.Avalonia.Android?style=flat-square)](https://www.nuget.org/packages/GeneralUpdate.Avalonia.Android/)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg?style=flat-square)](./LICENSE)

UI-free Android auto-update core library for Avalonia 12+ apps (`net10.0-android`).

---

## Features

- **No built-in UI** — host app owns dialogs, progress bars, and error rendering.
- **Update pipeline orchestration** — validate version → resume-download → SHA-256 verify → install.
- **Resumable HTTP download** with sidecar metadata and smoothed speed reporting.
- **Replaceable abstractions** — every stage is an interface you can swap.
- **Operation serialization** — concurrent calls are gated, safe to call from any thread.

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
    FileName    = "app-release.apk",
    FileSize    = 52_428_800
};

var check = await bootstrap.ValidateAsync(packageInfo, "2.2.1", ct);
if (check.UpdateFound)
{
    var result = await bootstrap.DownloadAndVerifyAsync(packageInfo, ct);
    if (result.Success && result.FilePath is not null)
    {
        await bootstrap.LaunchInstallerAsync(packageInfo, result.FilePath, ct);
    }
}
```

## API

### Static Factory

```csharp
// GeneralUpdateBootstrap.CreateDefault wires the full default dependency chain:
//   IAndroidContextProvider  → DefaultAndroidContextProvider
//   IAndroidActivityProvider → NullAndroidActivityProvider
//   IUpdateLogger            → NoOpUpdateLogger
//   IFileStorage             → PhysicalFileStorage
//   IUpdateDownloader        → HttpResumableApkDownloader
//   IHashValidator           → Sha256HashValidator
//   IApkInstaller            → AndroidApkInstaller
//   IVersionComparer         → SystemVersionComparer

public static IAndroidBootstrap CreateDefault(
    AndroidUpdateOptions options,
    IAndroidContextProvider? contextProvider = null,
    IAndroidActivityProvider? activityProvider = null,
    HttpClient? httpClient = null,
    IVersionComparer? versionComparer = null,
    IUpdateEventDispatcher? eventDispatcher = null,
    IUpdateLogger? logger = null,
    HttpDownloadOptions? httpOptions = null);
```

### IAndroidBootstrap (implements IDisposable)

| Method | Description |
|---|---|
| `ValidateAsync(packageInfo, currentVersion, ct)` | Compare versions, fire `AddListenerValidate` or `AddListenerUpdateFailed`, return `UpdateCheckResult` |
| `DownloadAndVerifyAsync(packageInfo, ct)` | Resume-download APK, SHA-256 verify, fire progress/completed/failed events, return `UpdateOperationResult` |
| `LaunchInstallerAsync(packageInfo, apkFilePath, ct)` | Launch Android `ACTION_VIEW` intent via FileProvider, return `InstallResult` |
| `GetSnapshot()` | Thread-safe snapshot of current `(State, FailureReason, Message)` |
| `AddListenerUpdatePrecheck(func)` | Register a pre-check callback (see below), return the bootstrap for chaining |

| Event | Payload |
|---|---|
| `AddListenerValidate` | `ValidateEventArgs` — `PackageInfo`, `CurrentVersion` |
| `AddListenerDownloadProgressChanged` | `DownloadProgressChangedEventArgs` — speed, bytes, percentage, status |
| `AddListenerUpdateCompleted` | `UpdateCompletedEventArgs` — `Result` (`UpdateOperationResult`) |
| `AddListenerUpdateFailed` | `UpdateFailedEventArgs` — `Result`, `FailureReason` |

### Pre-check Hook

`AddListenerUpdatePrecheck` mirrors `GeneralUpdate.Core`'s `AddListenerUpdatePrecheck` / `ClientStrategy.UseUpdatePrecheck`:
it runs when `ValidateAsync` finds a newer version and **before** the APK is downloaded, and it hands the discovered update
information (`UpdateInfoEventArgs`: package metadata, current version, and the `UpdateCheckResult` being produced) to your
business logic.

```csharp
bootstrap.AddListenerUpdatePrecheck(args =>
{
    // args.PackageInfo    — Version / DownloadUrl / Sha256 / FileSize / IsForced / ...
    // args.CurrentVersion — the version running on the device
    // args.Result         — the UpdateCheckResult ValidateAsync is about to return

    if (args.PackageInfo.Version == "1.2.0" && !IsWifiConnected())
    {
        return true; // skip: wait for Wi-Fi before pulling 1.2.0
    }

    return false;
});
```

Return `true` to skip the update (same contract as `GeneralUpdate.Core`'s `CanSkip`), `false` to continue. A skipped update
makes `ValidateAsync` return `UpdateFound == false` with `UpdateState.Completed` and does **not** raise
`AddListenerValidate`, so the usual `if (check.UpdateFound) { ... }` flow stops before downloading. Forced updates
(`UpdatePackageInfo.IsForced`) never invoke the callback.

### Enums

```csharp
enum UpdateState
{
    None, Checking, UpdateAvailable, Downloading, Verifying,
    ReadyToInstall, Installing, Completed, Failed, Canceled
}

enum UpdateFailureReason
{
    None, NetworkError, Canceled, InvalidMetadata, FileIoError,
    HashMismatch, ServerDoesNotSupportRange, InstallPermissionDenied,
    InstallLaunchFailed, VersionComparisonFailed, Unknown
}
```

### Model Hierarchy

```
UpdateOperationResult (base record)
├── Success, State, FailureReason, Message, PackageInfo, FilePath, Exception
├── UpdateCheckResult  →  + UpdateFound, CurrentVersion, TargetVersion
├── DownloadResult
├── HashValidationResult  →  + ActualSha256, ExpectedSha256
└── InstallResult
```

Other models: `AndroidUpdateOptions`, `DownloadProgressInfo`, `DownloadResumeMetadata`, `UpdatePackageInfo`, `UpdateStateSnapshot`.

## Android FileProvider Setup

Add to `AndroidManifest.xml`:

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

## Project Structure

```
src/GeneralUpdate.Avalonia.Android
├── Abstractions/         # 9 interfaces: IAndroidBootstrap, IApkInstaller, IFileStorage, …
├── Enums/                # UpdateState, UpdateFailureReason
├── Events/               # 5 event arg types
├── Models/               # 10 model records
├── Services/             # 9 default implementations
├── GeneralUpdateBootstrap.cs   # Static factory
└── GeneralUpdate.Avalonia.Android.csproj
```

## License

Apache License 2.0 — see [LICENSE](./LICENSE).

---

**Other languages:** [English](./README.en.md) | [中文](./README.zh-CN.md)
