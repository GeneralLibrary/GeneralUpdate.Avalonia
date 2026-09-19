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
    FileProviderAuthority = "com.example.app.generalupdate.fileprovider",

    // ValidateAsync queries this server internally; callers only pass the installed version
    UpdateServer = new UpdateServerOptions
    {
        RequestUrl     = "https://example.com/Upgrade/Verification",
        AppKey         = "your-app-key",
        Platform       = androidPlatformId,
        ProductId      = "your-product-id"
    }
};

using IAndroidBootstrap bootstrap = GeneralUpdateBootstrap.CreateDefault(options);

var check = await bootstrap.ValidateAsync("2.2.1", ct);
if (check.Success && check.UpdateFound && check.PackageInfo is { } packageInfo)
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
| `ValidateAsync(currentVersion, ct)` | Query `AndroidUpdateOptions.UpdateServer` for the newest package, compare versions, fire `AddListenerValidate` or `AddListenerUpdateFailed`, return `UpdateCheckResult` (with the discovered `PackageInfo`) |
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

### Server-Driven Validation

`ValidateAsync(currentVersion, ct)` only takes the version installed on the device. The component queries
`AndroidUpdateOptions.UpdateServer`, picks the newest full APK and compares versions, so callers no longer build an
`UpdatePackageInfo` themselves — pass `check.PackageInfo` to `DownloadAndVerifyAsync` / `LaunchInstallerAsync` afterwards.

By default it POSTs the GeneralUpdate verification protocol (`version/appKey/appType/platform/productId` →
`{"code":200,"body":[...]}`); set `UpdateServer.UseJsonEndpoint = true` to GET a single `UpdatePackageInfo` JSON document
instead. HTTP 204 or an empty result means "no update"; transport, protocol and metadata failures are reported through
`UpdateCheckResult.Success`/`FailureReason` and `AddListenerUpdateFailed`, and never invoke the pre-check callback. Without a
configured `UpdateServer` the call fails with `UpdateFailureReason.InvalidMetadata`. Validation requests reuse the
`httpOptions` passed to `CreateDefault` (`RequestTimeout`, proxy, TLS, `AuthProvider`). See the repository README for the
full contract and the JSON payload example.

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

Other models: `AndroidUpdateOptions`, `DownloadProgressInfo`, `DownloadResumeMetadata`, `HttpDownloadOptions`,
`UpdatePackageInfo`, `UpdateServerOptions`, `UpdateStateSnapshot`.

## Android Setup and Prerequisites

Declare the install permission (Android 8.0+). Without it `LaunchInstallerAsync` returns
`InstallPermissionDenied`; guide the user to `Settings.ActionManageUnknownAppSources` and retry afterwards:

```xml
<uses-permission android:name="android.permission.REQUEST_INSTALL_PACKAGES" />
```

Add the FileProvider to `AndroidManifest.xml`. Its authority must equal
`AndroidUpdateOptions.FileProviderAuthority`, and the paths below must cover `DownloadDirectoryPath`
(defaults to `<CacheDir>/update`); a mismatch returns `InstallLaunchFailed`. Pass an `IAndroidActivityProvider`
to `CreateDefault` to launch the installer from the current Activity. `Success = true` only means the installer
was launched — the process is killed on completion, so re-check the version on the next launch.

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
├── Abstractions/         # interfaces: IAndroidBootstrap, IApkInstaller, IFileStorage, …
├── Enums/                # UpdateState, UpdateFailureReason
├── Events/               # event arg types
├── Models/               # model records (UpdatePackageInfo, UpdateServerOptions, …)
├── Services/             # default implementations (HTTP download/query, installer, storage, …)
├── GeneralUpdateBootstrap.cs   # Static factory
└── GeneralUpdate.Avalonia.Android.csproj
```

## License

Apache License 2.0 — see [LICENSE](./LICENSE).

---

**Other languages:** [English](./README.en.md) | [中文](./README.zh-CN.md)
