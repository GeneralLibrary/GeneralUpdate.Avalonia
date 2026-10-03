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

Read `currentVersion` from Android PackageManager and configure `androidPlatformId` for your server.
See the [full startup example](../../README-EN.md#basic-usage) for offline installation reconciliation.

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

using var bootstrap = GeneralUpdateBootstrap.CreateDefault(options);

bootstrap.AddListenerUpdateFailed += (_, args) => Console.Error.WriteLine(args.Result.Message);
var prepared = await bootstrap.PrepareUpdateAsync(currentVersion, CancellationToken.None);
if (prepared.IsReadyToInstall && prepared.PackageInfo is { } package && prepared.FilePath is { } path)
{
    await bootstrap.LaunchInstallerAsync(package, path, CancellationToken.None);
}
```

Preparation holds one lock across validation, pre-check, download and hash verification. It never opens UI.
No update or a skipped update succeeds with `IsReadyToInstall = false`; separate stage APIs remain available.

## API

### Factory

`GeneralUpdateBootstrap.CreateDefault(options, contextProvider?, activityProvider?, httpClient?, versionComparer?, eventDispatcher?, logger?, httpOptions?, packageSource?, installationStore?)`

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
| `IUpdatePackageSource` | `HttpUpdatePackageClient` |
| `IInstallationStore` | `JsonFileInstallationStore` |

Built-in messages default to English. Set `Language = UpdateLanguage.Chinese` on `AndroidUpdateOptions`
before calling `CreateDefault` to return built-in messages and download statuses in Chinese.

### IAndroidBootstrap Methods

| Method | Returns |
|---|---|
| `PrepareUpdateAsync(currentVersion, ct)` | `UpdatePreparationResult` (`IsReadyToInstall` indicates a verified package) |
| `ValidateAsync(currentVersion, ct)` | `UpdateCheckResult` (includes the discovered `PackageInfo`) |
| `DownloadAndVerifyAsync(packageInfo, ct)` | `UpdateOperationResult` |
| `LaunchInstallerAsync(packageInfo, apkFilePath, ct)` | `InstallResult` |
| `CheckInstallationAsync(currentVersion, ct)` | `InstallationCheckResult` |
| `ResetInstallationAsync(ct)` | `UpdateOperationResult` (clears only the journal) |
| `GetSnapshot()` | `UpdateStateSnapshot` |

### Events

| Event | Args |
|---|---|
| `AddListenerValidate` | `ValidateEventArgs` |
| `AddListenerDownloadProgressChanged` | `DownloadProgressChangedEventArgs` |
| `AddListenerUpdateCompleted` | `UpdateCompletedEventArgs` |
| `AddListenerInstallationConfirmed` | `InstallationConfirmedEventArgs` |
| `AddListenerUpdateFailed` | `UpdateFailedEventArgs` |

### Server-Driven Validation

`ValidateAsync(currentVersion, ct)` only takes the version installed on the device: the component queries
`AndroidUpdateOptions.UpdateServer`, picks the newest full APK and compares versions, so callers no longer build an
`UpdatePackageInfo` themselves — pass `check.PackageInfo` to `DownloadAndVerifyAsync` / `LaunchInstallerAsync` afterwards.

By default it POSTs the GeneralUpdate verification protocol (`version/appKey/appType/platform/productId` →
`{"code":200,"body":[...]}`); set `UpdateServer.UseJsonEndpoint = true` to GET a single `UpdatePackageInfo` JSON document
instead. HTTP 204 or an empty result means "no update"; transport, protocol and metadata failures are reported through
`UpdateCheckResult.Success`/`FailureReason` and `AddListenerUpdateFailed`, and never invoke the pre-check callback. Without a
configured `UpdateServer` or custom package source the call fails with `UpdateFailureReason.InvalidMetadata`. Validation requests reuse the
`httpOptions` passed to `CreateDefault` (`RequestTimeout`, proxy, TLS, `AuthProvider`). See the repository README for the
full contract and the JSON payload example.

### Installation Confirmation

`CreateDefault` atomically persists the target before installer handoff in `<FilesDir>/update/installation.json`
(override `InstallationStateFilePath` if needed). Persistence errors prevent handoff; use one bootstrap per journal.
At startup or installer return, call `CheckInstallationAsync` with the actual version from Android PackageManager.
It works offline: `State == None` means no attempt, `HasPendingInstallation` means the target is not installed yet,
and `IsInstalled` means the version reached or exceeded the target. Invalid versions and storage errors are reported explicitly.
The outcome survives restarts; `AddListenerInstallationConfirmed` fires on the first durable confirmation only.
This is a best-effort event, not a message queue: restore UI from the returned result.

`AddListenerUpdateCompleted` retains its legacy phase semantics (`ReadyToInstall` / `Installing`), not installation success.
The compatibility constructor uses `LocalApplicationData/update/installation.json` when `installationStateFilePath` is omitted.
The dependency-injection constructor requires `IInstallationStore`; tracking is never implicitly disabled.
After user confirmation, call `ResetInstallationAsync` to recover corrupt records and check its result.
User confirmation and reopening the app are still required; silent installation and rollback are not provided.

### Extension and Transport Contracts

Inject `packageSource:` / `installationStore:` through the factory, or use the `AndroidBootstrap` constructor accepting
`IUpdatePackageSource` and `IInstallationStore` for full dependency injection. Bootstrap owns disposable downloader/source
services; other dependencies remain host-owned. Cancel and await work before disposing; early disposal defers cleanup.

An external `HttpClient` is always preserved and borrowed, even with request timeout/retry/auth options.
TLS/proxy belong on its handler; combining handler options with an external client throws `ArgumentException`.
Otherwise the factory owns one shared transport. Transient download HEAD/GET/body failures retry with resume;
metadata queries, permanent HTTP errors and user cancellations do not retry. Timeouts report `Failed/NetworkError`,
user cancellation reports `Canceled`, and cancellation while waiting for the operation lock throws.
See [detailed contracts](../../README-EN.md#extension-points-and-lifetime).

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
├── UpdatePreparationResult → + UpdateFound, IsReadyToInstall
├── InstallationCheckResult → + CurrentVersion, Record, IsInstalled, HasPendingInstallation
├── DownloadResult
├── HashValidationResult  →  + ActualSha256, ExpectedSha256
└── InstallResult
```

### Enums

`UpdateState`: `None`, `Checking`, `UpdateAvailable`, `Downloading`, `Verifying`, `ReadyToInstall`, `Installing`, `Completed`, `Failed`, `Canceled`, `InstallationPending`, `Installed`

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
was launched — the process is killed on completion, so call `CheckInstallationAsync` on the next launch.

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
