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
- Durable coordination — pending-install tracking, next-launch version reconciliation, and explicit retry/abandon

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
        RequestUrl = "https://example.com/Upgrade/Verification",
        AppKey     = "your-app-key",
        Platform   = androidPlatformId,
        ProductId  = "your-product-id"
    }
};

using IAndroidBootstrap bootstrap = GeneralUpdateBootstrap.CreateDefault(options);

var check = await bootstrap.ValidateAsync("2.2.1", CancellationToken.None);
if (check.Success && check.UpdateFound && check.PackageInfo is { } packageInfo)
{
    var result = await bootstrap.DownloadAndVerifyAsync(packageInfo, CancellationToken.None);
    if (result.Success && result.FilePath is not null)
    {
        await bootstrap.LaunchInstallerAsync(packageInfo, result.FilePath, CancellationToken.None);
    }
}
```

## Durable Coordinator (recommended)

Use `GeneralUpdateBootstrap.CreateCoordinator(options)` for whole-flow orchestration with the same server/FileProvider
configuration. It returns `IAndroidUpdateCoordinator`, supporting `await using`, `StateChanged`, and:

- `RunAsync(installedVersion, ct)`: check → download/verify → durably record intent → installer handoff.
- `ReconcileAsync(installedVersion, ct)`: offline next-launch confirmation; only a version at or above the pending target
  returns `Updated`. `InstallerLaunched` never implies installation success.
- `RetryAsync(installedVersion, ct)`: explicitly recover a pending attempt by reconciling, rediscovering and re-verifying;
  never install from a persisted path. A changed server target returns `RecoveryRequired`, preserving the earlier handoff
  until explicitly reconciled/abandoned. Without a pending intent, use `RunAsync` for a new attempt.
- `AbandonAsync(ct)`: forget tracking (including corrupt state), not OS installation cancellation, APK deletion or rollback.

Always pass the actual installed version. The factory stores versioned minimal intent in
`<NoBackupFilesDir>/generalupdate/pending-update.json`, atomically replaced after flushing. No URL/path/credentials are persisted.
Inject `IPendingUpdateStore` through `pendingStore` for other private, persistent storage. Corrupt state fails closed;
a persistence failure before handoff prevents installation. Pending state blocks another `RunAsync` until explicitly reconciled,
retried or abandoned. Pass a UI `eventDispatcher`, keep one coordinator per staging directory, and do not mix direct bootstrap calls.
Coordinator cancellation returns `Canceled`, including gate waits. Factory-created bootstraps are coordinator-owned;
direct construction is non-owning by default. Await disposal outside callbacks.
The coordinator also forwards `AddListenerDownloadProgressChanged` and `AddListenerUpdatePrecheck` (configure policy before
operations; `true` skips, forced updates bypass). The default store holds an exclusive `.lock` lease across cooperating
instances/processes for each complete workflow. Do not remove the lock file in use. Custom stores without
`IPendingUpdateStoreLeaseProvider` require a singleton coordinator; separate state files do not protect shared staging paths.

The host invokes reconciliation at startup/installer return and decides when to retry or abandon. This closes the observed
installed-version loop, not application-health/data-migration recovery, silent installation, automatic relaunch or OS rollback.
See the repository README for the complete integration example and device-validation requirements.

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

### Factory

`GeneralUpdateBootstrap.CreateDefault(options, contextProvider?, activityProvider?, httpClient?, versionComparer?, eventDispatcher?, logger?, httpOptions?)`

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
| `ValidateAsync(currentVersion, ct)` | `UpdateCheckResult` (includes the discovered `PackageInfo`) |
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

### Server-Driven Validation

`ValidateAsync(currentVersion, ct)` only takes the version installed on the device: the component queries
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
