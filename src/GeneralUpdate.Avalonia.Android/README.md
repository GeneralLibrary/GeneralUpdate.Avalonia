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
- **Durable coordination** — pending-install tracking, next-launch version reconciliation, and explicit retry/abandon.

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

## Durable Coordinator (recommended)

`GeneralUpdateBootstrap.CreateCoordinator(options)` provides a higher-level `IAndroidUpdateCoordinator` without
changing `IAndroidBootstrap`. Use the same server, FileProvider and permission configuration shown above.

```csharp
using GeneralUpdate.Avalonia.Android.Enums;

await using var coordinator = GeneralUpdateBootstrap.CreateCoordinator(options);
coordinator.StateChanged += (_, e) => Console.WriteLine($"{e.Result.Stage}: {e.Result.Outcome}");
var startup = await coordinator.ReconcileAsync(installedVersion, ct);
if (startup.Outcome == UpdateCoordinatorOutcome.NoPendingUpdate)
{
    // On an explicit host update command:
    var result = await coordinator.RunAsync(installedVersion, ct);
}
```

Supply the actual installed version, not the server target. The coordinator serializes complete attempts and persists
intent before installer launch in `<NoBackupFilesDir>/generalupdate/pending-update.json`. The versioned, atomically replaced
record contains only attempt ID, original/target versions, timestamp and phase—never URLs, file paths or credentials.
Pass `pendingStore` to inject an `IPendingUpdateStore`; custom storage must be private, persistent and not restored from backups.

| Method | Outcome |
|---|---|
| `RunAsync` | Check → download/verify → persist → handoff. `InstallerLaunched` is **not** installed; existing intent returns `PendingUpdateExists`. |
| `ReconcileAsync` | Offline next-launch confirmation: `Updated` only when the observed installed version meets/exceeds the target; otherwise `AwaitingInstallation`/`RecoveryRequired`. |
| `RetryAsync` | Explicit recovery of a pending attempt; reconcile first, then rediscover/download/verify the same target. Changed targets return `RecoveryRequired` and preserve the old intent. No stored APK path is installed. Use `RunAsync` again if no intent was persisted. |
| `AbandonAsync` | Forget tracking, including corrupt state; no APK deletion, OS cancellation or rollback. |

Progress and pre-check remain available through `AddListenerDownloadProgressChanged` and `AddListenerUpdatePrecheck`.
Register policy before operations; `true` means skip an optional update, and forced updates bypass the callback.
The JSON store holds a workflow-wide exclusive `.lock` lease across cooperating instances/processes; do not remove it in use.
Custom stores without `IPendingUpdateStoreLeaseProvider` require a singleton coordinator. Different state files do not
protect the same staging directory.

Corrupt/unknown-schema state fails closed. Write failures prevent launch; an uncertain handoff remains recoverable.
Pass `eventDispatcher` for UI delivery of named stages and terminal outcomes. Cancellation returns `Canceled`, including
while waiting for the coordinator gate. The factory coordinator owns its bootstrap; direct `new AndroidUpdateCoordinator(...)`
leaves a supplied bootstrap host-owned unless `ownsBootstrap: true`. Dispose asynchronously outside callbacks to await shutdown.
Do not mix direct bootstrap calls with coordinator operations.

Call reconciliation at startup and after returning from the installer. An unchanged version may mean installation is still pending;
retry/abandon must be explicit. This confirms installed-version convergence, not app health, data migration, silent installation,
automatic relaunch or rollback. Those require host/platform policy and real-device validation.

## Host UI and Recovery

Global download authentication is limited to the configured verification origin. Set
`HttpDownloadOptions.AllowedDownloadAuthenticationOrigins` only for CDN origins trusted to receive the same credentials;
`TrustedAuthenticationOrigin` can override the verification origin. Origin checks include scheme, host and port, not paths.
Authenticated requests require HTTPS unless `AllowInsecureAuthentication` is explicitly enabled for development.
Internal clients reject redirects; configure final URLs. With an injected `HttpClient`, the host must disable redirects
and avoid unrestricted credential default headers. Per-package credentials retain precedence at the initial package URL.

The default dispatcher invokes events inline; it does not marshal to the Avalonia UI thread. In the host application,
implement `IUpdateEventDispatcher.Dispatch` using `Avalonia.Threading.Dispatcher.UIThread.Post(callback)` and pass it
as `eventDispatcher` to `CreateDefault`. Pre-check is synchronous and is not dispatched; it should read captured policy,
not controls. Unsubscribe ViewModel event handlers when released, throttle progress rendering, and never synchronously
wait for another update operation inside a callback. Catch exceptions inside `async void` handlers after awaits.

Use one coordinator per private staging directory and preserve the verified file while the installer may still read it.
The durable coordinator tracks the intended version; call its reconciliation method on next launch. With the low-level API,
the host must provide that tracking. Installer launch is not installation confirmation.
The host owns permission prompting, stale-cache retention, relaunch and failed-release/data-migration recovery.

`Dispose()` cancels without blocking and defers resource release until operations and waiters drain. The concrete
`AndroidBootstrap` also implements `IAsyncDisposable`; use it outside callbacks when cleanup must be awaited.
Cancellation while waiting for the operation gate still throws; cancellation during verification returns a canceled result.
Notification exceptions are isolated, but a pre-check exception fails validation rather than bypassing host policy.
Configured download retries cover HEAD, GET and interrupted bodies, not metadata discovery or local file errors.

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
