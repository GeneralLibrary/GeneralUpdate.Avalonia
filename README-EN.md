# GeneralUpdate.Avalonia

[![GitHub Stars](https://img.shields.io/github/stars/GeneralLibrary/GeneralUpdate.Avalonia?style=flat-square)](https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/stargazers)
[![GitHub Forks](https://img.shields.io/github/forks/GeneralLibrary/GeneralUpdate.Avalonia?style=flat-square)](https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/network/members)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg?style=flat-square)](./LICENSE)
[![NuGet](https://img.shields.io/nuget/v/GeneralUpdate.Avalonia.Android?style=flat-square)](https://www.nuget.org/packages/GeneralUpdate.Avalonia.Android/)

---

![](./imgs/banner.png)

## Introduction

`GeneralUpdate.Avalonia` is a repository focused on update capabilities for Avalonia applications. Its current core module, `GeneralUpdate.Avalonia.Android`, provides a UI-free Android auto-update pipeline targeting `net8.0-android` for Avalonia 12+ apps.

The project uses composable abstractions so you can replace version comparison, downloading, hash validation, installer launching, logging, and event dispatching based on your application architecture.

## Core Features

- **UI-free Android update core**: Host applications fully control dialogs, progress, and error presentation.  
- **End-to-end update flow**: validation → resumable download → SHA-256 verification → installer launch.  
- **Extensible architecture**: `IVersionComparer`, `IUpdateDownloader`, `IHashValidator`, `IApkInstaller`, and more are replaceable.  
- **Resumable downloading**: sidecar metadata + streaming writes for better reliability on unstable networks.  
- **Unified event model**: built-in validation, progress, completion, and failure events for UI/log integration.  

## Quick Start

### Prerequisites

- .NET SDK: `8.0+`
- Platform: `Android (net8.0-android)`
- Avalonia: `12+`
- Git: `2.30+`

### Installation

1. Clone the repository

```bash
git clone https://github.com/GeneralLibrary/GeneralUpdate.Avalonia.git
cd GeneralUpdate.Avalonia
```

2. Install dependencies (NuGet package consumption)

```bash
dotnet add package GeneralUpdate.Avalonia.Android
```

3. Build and test locally (repository development)

```bash
dotnet test tests/GeneralUpdate.Avalonia.Android.Tests/GeneralUpdate.Avalonia.Android.Tests.csproj
```

### Basic Usage

Supply `androidPlatformId` from the host configuration to match the deployed server.

```csharp
using GeneralUpdate.Avalonia.Android;
using GeneralUpdate.Avalonia.Android.Models;

var cacheDirPath = Android.App.Application.Context.CacheDir?.AbsolutePath
    ?? Path.GetTempPath();

var options = new AndroidUpdateOptions
{
    DownloadDirectoryPath = Path.Combine(cacheDirPath, "update"),
    FileProviderAuthority = "com.example.app.generalupdate.fileprovider",

    // ValidateAsync queries this server internally; callers only pass the installed version
    UpdateServer = new UpdateServerOptions
    {
        RequestUrl = "https://example.com/Upgrade/Verification",
        AppKey = "your-app-key",
        AppType = 1,
        Platform = androidPlatformId,
        ProductId = "your-product-id"
    }
};

using var bootstrap = GeneralUpdateBootstrap.CreateDefault(options);

bootstrap.AddListenerUpdateFailed += (_, args) => Console.Error.WriteLine(args.Result.Message);
var context = global::Android.App.Application.Context;
var currentVersion = context.PackageManager?.GetPackageInfo(context.PackageName!,
    global::Android.Content.PM.PackageInfoFlags.Activities)?.VersionName
    ?? throw new InvalidOperationException("Cannot read the installed version.");

var installation = await bootstrap.CheckInstallationAsync(currentVersion);
if (!installation.Success) return;

var prepared = await bootstrap.PrepareUpdateAsync(currentVersion, CancellationToken.None);
if (prepared.IsReadyToInstall && prepared.PackageInfo is { } package && prepared.FilePath is { } path)
{
    await bootstrap.LaunchInstallerAsync(package, path, CancellationToken.None);
}
```

`PrepareUpdateAsync` holds one operation lock across query, comparison, pre-check, download and hash verification.
No update or a skipped update returns `Success = true`, `IsReadyToInstall = false`. It never opens an installer or
permission screen. Keep using `ValidateAsync` / `DownloadAndVerifyAsync` when separate stages are needed.
Use `IUpdateEventDispatcher` to marshal UI events; see permission handling below.

### Server-Driven Version Validation

`ValidateAsync(currentVersion, cancellationToken)` only needs the version installed on the device: the component queries
the server configured through `AndroidUpdateOptions.UpdateServer`, picks the newest full APK, compares it with
`currentVersion`, and exposes the discovered package through `UpdateCheckResult.PackageInfo` for download and installation.

The default protocol is GeneralUpdate's sample server `POST /Upgrade/Verification`: the request body carries
`version/appKey/appType/platform/productId` and the response is `{"code":200,"body":[...]}`. The client maps
`version/url/hash/size/name/updateLog/releaseDate/isForcibly/authScheme/authToken` and selects the newest non-frozen full APK
(`packageType` 2, 0 or omitted; `format` `apk`/`.apk`, or a `.apk` URL path when omitted). ZIP, patch and driver packages are
never handed to the Android installer; an empty `body` or no matching package means "no update".
**Verify the deployed URL, response shape and Android platform id — do not assume a fixed id**; when the protocol differs,
have the server expose the standard JSON endpoint below.

Static JSON server: set `UpdateServer.UseJsonEndpoint = true` and point `RequestUrl` at the JSON address; the component
then GETs a single `UpdatePackageInfo` (property names are case-insensitive), for example:

```json
{
  "version": "2.3.0",
  "downloadUrl": "https://example.com/app-release.apk",
  "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "description": "Release notes",
  "isForced": false
}
```

- `sha256` must be the APK's real 64-character hexadecimal SHA-256 (never MD5); `fileSize` may be omitted or 0 when unknown.
- HTTP 204 or a JSON `null` body means "no package"; transport, protocol and metadata errors are reported through
  `UpdateCheckResult.Success = false`, `FailureReason` and `AddListenerUpdateFailed`, and never invoke the pre-check callback.
- Cancellation during the request returns `UpdateState.Canceled`; cancelling while waiting on the operation gate throws
  `OperationCanceledException`.
- Validation and downloads share one HTTP client. A supplied `httpClient` is always borrowed and preserved, including its
  handler and `Timeout`; request timeout, retry and authentication policies may be supplied alongside it. The earlier timeout wins.
  Configure TLS/proxy on that client's handler: combining those handler settings in `httpOptions` with an external client
  throws `ArgumentException` instead of silently replacing it. Otherwise the library creates and disposes its own client.
- Factory defaults: 30-second query/HEAD timeout, 10-minute total download timeout (including backoff), up to 3 download
  attempts. Transient HEAD/GET/response-stream failures retry with resume; HEAD 405/501 falls back to GET.
  Permanent errors such as 401 and user cancellation do not retry. `MaxRetryAttempts` includes the initial attempt and does
  not apply to metadata queries. Timeouts report `Failed/NetworkError`; user cancellation reports `Canceled`.
- Validation without either `UpdateServer` or an injected `IUpdatePackageSource` fails with `InvalidMetadata`.
- Only query trusted servers and use HTTPS in production.

Once a newer version is found, the `AddListenerUpdatePrecheck` callback receives the discovered package metadata and returns
`true` to skip or `false` to continue (forced updates bypass it), matching `GeneralUpdate.Core`.

## Android Prerequisites

The library ships no UI and does not request permissions on your behalf. A full update only completes when the host app
configures all four items below:

1. **Install permission (Android 8.0+)** — declare it in `AndroidManifest.xml`:

   ```xml
   <uses-permission android:name="android.permission.REQUEST_INSTALL_PACKAGES" />
   ```

   When the user has not granted it, `LaunchInstallerAsync` returns `FailureReason = InstallPermissionDenied`. Send the
   user to the "install unknown apps" screen and retry afterwards:

   ```csharp
   var context = Android.App.Application.Context;
   context.StartActivity(new Android.Content.Intent(
           Android.Provider.Settings.ActionManageUnknownAppSources,
           Android.Net.Uri.Parse("package:" + context.PackageName))
       .AddFlags(Android.Content.ActivityFlags.NewTask));
   ```

2. **FileProvider** — the `android:authorities` in `AndroidManifest.xml` must match
   `AndroidUpdateOptions.FileProviderAuthority` exactly, and `generalupdate_file_paths.xml` must cover
   `DownloadDirectoryPath` (defaults to `<CacheDir>/update`). A mismatch returns `InstallLaunchFailed`.

3. **Current Activity** — `CreateDefault` uses `NullAndroidActivityProvider` by default, in which case the installer is
   launched through `Application.Context` + `FLAG_ACTIVITY_NEW_TASK`. Passing an `IAndroidActivityProvider` that returns
   the current `Activity` is more robust:

   ```csharp
   using var bootstrap = GeneralUpdateBootstrap.CreateDefault(options, activityProvider: myActivityProvider);
   ```

4. **Server** — configure `AndroidUpdateOptions.UpdateServer` (or a static JSON endpoint / custom `IUpdatePackageSource`),
   and provide a 64-character hexadecimal SHA-256.

`LaunchInstallerAsync` returning `Success = true` only means the installer intent was launched; it **does not** mean the user
finished installing. The process is killed on completion, so call `CheckInstallationAsync` with the actual installed version
on the next launch to confirm the update took effect.

### Closing the Installation Loop

`CreateDefault` atomically records the latest installation target **before** launching the installer in
`<FilesDir>/update/installation.json`, outside the disposable APK cache. Override `InstallationStateFilePath` if needed,
and use only one bootstrap per journal. If persistence fails, installation is not launched and `FileIoError` is reported.
The journal contains no download credentials.

At startup or when returning from the installer, read the actual version from Android `PackageManager` and call:

```csharp
bootstrap.AddListenerInstallationConfirmed += (_, args) =>
{
    // args.Result.Record.TargetVersion is the previous target.
    // args.Result.CurrentVersion is the version read from the device.
};
var installation = await bootstrap.CheckInstallationAsync(currentVersion, CancellationToken.None);
```

`Success` with `State == None` means no recorded attempt, not a successful installation.
`HasPendingInstallation` means the installed version is below the target: installation may be pending, canceled or failed.
`IsInstalled` means the installed version reached or exceeded the target (`Installed` state); the outcome is persisted.
Invalid versions or storage errors return `Success == false` and raise `AddListenerUpdateFailed`, never a silent empty result.

Reconciliation works offline; query `ValidateAsync` separately for further updates. `AddListenerInstallationConfirmed` fires
only on the first durable confirmation, not on repeated checks or restarts. It is not a reliable message queue; restore UI
from the returned durable result. The legacy `AddListenerUpdateCompleted` remains a phase notification
(`ReadyToInstall` / `Installing`), **not installation success**. The compatibility constructor now uses
`LocalApplicationData/update/installation.json` when `installationStateFilePath` is omitted, rather than disabling tracking.
The new dependency-injection constructor requires an explicit `IInstallationStore`.

For corrupt records, ask the user before calling `await bootstrap.ResetInstallationAsync()` and check its `Success`.
Reset only removes the journal, not the installed app, downloads or server settings. Never reset automatically on every failure.

### Extension Points and Lifetime

Implement `IUpdatePackageSource.GetLatestAsync(currentVersion, ct)` for a custom protocol and
`IInstallationStore.LoadAsync/SaveAsync/ClearAsync` for custom persistence, without duplicating orchestration:

```csharp
using var bootstrap = GeneralUpdateBootstrap.CreateDefault(options,
    packageSource: myPackageSource, installationStore: myInstallationStore);
```

For full dependency injection, use `AndroidBootstrap(versionComparer, downloader, hashValidator, apkInstaller,
fileStorage, packageSource, installationStore, eventDispatcher?, logger?)`. The default implementations are
`HttpUpdatePackageClient` and `JsonFileInstallationStore`; the orchestrator no longer depends on their concrete protocols/storage.
A source returns `null` only for no update, and throws transport/protocol exceptions for failures.
A store must provide atomic durable writes and report corruption rather than return an empty record.

Use one bootstrap per journal. Bootstrap owns disposable downloader/package-source services; the host owns other injected
dependencies, including the store. Do not share bootstrap-owned service instances across updaters.
Cancel and await active work before disposal. Early disposal rejects new/queued operations and defers cleanup until the active
operation exits; it does not cancel that operation or dispose its semaphore while it is still in use.

The sample persists server settings, reconciles the previous attempt and checks the server at startup, without automatically
reopening the installer. Users still confirm installation and reopen the app. APKs must have the same package ID, compatible
signatures and an increasing `versionCode`. Version reconciliation is not APK signature verification or an application health
check; silent installation, automatic restart and rollback are not provided.

## Directory Structure

```text
GeneralUpdate.Avalonia/
├── src/
│   └── GeneralUpdate.Avalonia.Android/   # Android auto-update core library
├── tests/
│   └── GeneralUpdate.Avalonia.Android.Tests/ # Unit tests
├── README.md
├── README-EN.md
└── LICENSE
```

## Contributing

Contributions are welcome through the standard GitHub workflow:

1. Fork this repository and create a branch from `main`: `feature/{{short-description}}`.  
2. Keep changes focused and follow existing style and naming conventions.  
3. Run the existing tests before submitting:
   ```bash
   dotnet test tests/GeneralUpdate.Avalonia.Android.Tests/GeneralUpdate.Avalonia.Android.Tests.csproj
   ```
4. Open a Pull Request describing motivation, implementation details, and compatibility impact.  
5. Iterate based on review feedback, then merge and delete the branch.  

## License

This project is licensed under the **Apache License 2.0**. See [LICENSE](./LICENSE) for details.

## Contact

- Repository: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia
- Issue Tracker: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/issues
- Discussions: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/discussions
