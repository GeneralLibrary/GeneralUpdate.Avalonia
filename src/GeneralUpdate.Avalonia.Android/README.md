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

## Automatic server verification and pre-check

Configure `AndroidUpdateOptions.UpdateServer`, then call `ValidateAsync(currentVersion)`.
The component requests metadata internally, selects the latest full APK and compares versions before invoking
`AddListenerUpdatePrecheck` with the latest package information. No explicit HTTP client calls are required.

```csharp
var options = new AndroidUpdateOptions
{
    FileProviderAuthority = "com.example.app.generalupdate.fileprovider",
    UpdateServer = new UpdateServerOptions
    {
        RequestUrl = verificationUrl,
        AppKey = appKey,
        AppType = 1,
        Platform = serverPlatformId,
        ProductId = productId
    }
};
using var bootstrap = GeneralUpdateBootstrap.CreateDefault(options);
bootstrap.AddListenerUpdatePrecheck(args =>
{
    // Inspect args.PackageInfo: latest version, Description/release notes, URL, SHA256, etc.
    return false; // true skips, false continues; forced updates bypass this callback.
});
var check = await bootstrap.ValidateAsync(currentVersion, ct);
if (check.Success && check.UpdateFound && check.PackageInfo is { } packageInfo)
{
    var prepared = await bootstrap.DownloadAndVerifyAsync(packageInfo, ct);
    if (prepared.Success && prepared.FilePath is not null)
    {
        var install = await bootstrap.LaunchInstallerAsync(packageInfo, prepared.FilePath, ct);
        // Success means the system installer was launched, not that installation completed.
    }
}
```

Returning `true` produces `UpdateFound == false` and suppresses `AddListenerValidate`. No-update checks do not invoke pre-check.
The callback is synchronous and is not automatically dispatched to the UI thread.
The existing `ValidateAsync(packageInfo, currentVersion)` overload remains available and never queries the server.

For a static JSON endpoint, set `UpdateServer.UseJsonEndpoint = true` and point `RequestUrl` at a JSON `UpdatePackageInfo`
(case-insensitive property names). The component performs the GET internally:

```json
{
  "version": "2.3.0",
  "downloadUrl": "https://example.com/app-release.apk",
  "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "description": "Release notes",
  "isForced": false
}
```

Replace `sha256` with the actual APK's SHA-256, never an MD5 digest. Optional `fileSize` is in bytes; zero means unknown.
HTTP 204 or GET JSON `null` produces a successful no-update result. HTTP/protocol/metadata failures produce
`Success == false`, a `FailureReason` and `AddListenerUpdateFailed`, not pre-check.
Cancellation during the request returns `Canceled`; cancellation while waiting for the operation lock throws
`OperationCanceledException`, matching the existing overload. Use trusted endpoints and HTTPS in production.

### GeneralSpacestation / GeneralUpdate reference protocol

For deployments implementing the [GeneralUpdate sample server](https://github.com/GeneralLibrary/GeneralUpdate-Samples/tree/main/src/Server)
`POST /Upgrade/Verification` (or `/Update/Verification`) contract, use the default configuration shown above.

The request uses `version/appKey/appType/platform/productId`. The response must be `{"code":200,"body":[...]}`.
The client maps `version/url/hash/size/name/updateLog/releaseDate/isForcibly/authScheme/authToken` to package metadata and selects
the newest non-frozen full APK. `packageType` must be 2, 0 or absent; `format` must be `apk`/`.apk`, or, if omitted, the URL path
must end in `.apk`. ZIP, differential and driver packages are ignored. An empty eligible list means no update;
an unsuccessful application code or missing/null `body` reports a failure. Version ordering defaults to `System.Version`;
pass `versionComparer` to `CreateDefault` to customize both remote selection and local comparison.

The commercial GeneralSpacestation API is not publicly specified: **verify your deployment's route, schema and Android platform identifier**.
Do not assume a fixed Android platform number. For other protocols, expose the standard JSON endpoint shown above.
`CreateDefault`'s `httpOptions` configures both requests and downloads: `AuthProvider` (Bearer, API key, Basic or HMAC),
TLS and proxy settings. Verification uses `RequestTimeout`. `AppKey` is only a request field; it does not automatically enable HMAC.
Without `httpOptions`, an injected `httpClient` is reused and remains owned by the caller.

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
| `ValidateAsync(currentVersion, ct)` | Query the configured server internally, compare versions and apply pre-check; return metadata in `UpdateCheckResult.PackageInfo` |
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
