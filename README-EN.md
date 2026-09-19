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

var check = await bootstrap.ValidateAsync("2.2.1", CancellationToken.None);
if (check.Success && check.UpdateFound && check.PackageInfo is { } packageInfo)
{
    var prepared = await bootstrap.DownloadAndVerifyAsync(packageInfo, CancellationToken.None);
    if (prepared.Success && prepared.FilePath is not null)
    {
        await bootstrap.LaunchInstallerAsync(packageInfo, prepared.FilePath, CancellationToken.None);
    }
}
```

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
- Validation and downloads share the `httpOptions` passed to `CreateDefault` (`RequestTimeout`, proxy, TLS, `AuthProvider`).
  Without `httpOptions` the supplied `httpClient` is reused and its lifetime stays with the host.
- Calling `ValidateAsync` without `UpdateServer` fails with `UpdateFailureReason.InvalidMetadata`.
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

4. **Server** — `AndroidUpdateOptions.UpdateServer` must be configured (or use the static JSON endpoint through
   `UseJsonEndpoint`), and `sha256` must be a 64-character hexadecimal SHA-256.

`LaunchInstallerAsync` returning `Success = true` only means the installer intent was launched; it **does not** mean the user
finished installing. The process is killed on completion, so compare the installed version with the server again on the next
launch to confirm the update actually took effect.

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
