# GeneralUpdate.Avalonia

[![GitHub Stars](https://img.shields.io/github/stars/GeneralLibrary/GeneralUpdate.Avalonia?style=flat-square)](https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/stargazers)
[![GitHub Forks](https://img.shields.io/github/forks/GeneralLibrary/GeneralUpdate.Avalonia?style=flat-square)](https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/network/members)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg?style=flat-square)](./LICENSE)
[![NuGet](https://img.shields.io/nuget/v/GeneralUpdate.Avalonia.Android?style=flat-square)](https://www.nuget.org/packages/GeneralUpdate.Avalonia.Android/)

---

![](./imgs/banner.png)

## Introduction

`GeneralUpdate.Avalonia` is a repository focused on update capabilities for Avalonia applications. Its current core module, `GeneralUpdate.Avalonia.Android`, provides a UI-free Android auto-update pipeline targeting `net10.0-android` for Avalonia 12+ apps.

The project uses composable abstractions so you can replace version comparison, downloading, hash validation, installer launching, logging, and event dispatching based on your application architecture.

## Core Features

- **UI-free Android update core**: Host applications fully control dialogs, progress, and error presentation.  
- **End-to-end update flow**: validation → resumable download → SHA-256 verification → installer launch.  
- **Extensible architecture**: `IVersionComparer`, `IUpdateDownloader`, `IHashValidator`, `IApkInstaller`, and more are replaceable.  
- **Resumable downloading**: sidecar metadata + streaming writes for better reliability on unstable networks.  
- **Unified event model**: built-in validation, progress, completion, and failure events for UI/log integration.  

## Quick Start

### Prerequisites

- .NET SDK: `10.0+`
- Platform: `Android (net10.0-android, API 26+)`
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

## Host UI and Recovery Responsibilities

### Authentication and transport migration

`HttpDownloadOptions.AuthProvider` authenticates the configured verification endpoint. For downloads it is now applied
only to that endpoint's origin (scheme, host and effective port) or an origin explicitly listed by the host in
`AllowedDownloadAuthenticationOrigins`. Paths do not narrow that trust. `TrustedAuthenticationOrigin` overrides the default
origin for both verification and downloads; without a valid trusted origin, downloads get no global credentials unless explicitly allowed.
An unlisted CDN remains anonymous; valid per-package credentials still take precedence at the original package URL.

```csharp
var httpOptions = new HttpDownloadOptions
{
    AuthProvider = myAuthenticationProvider,
    AllowedDownloadAuthenticationOrigins = new[] { new Uri("https://packages.example.com") },
    MaxRetryAttempts = 3,
    DownloadTimeout = TimeSpan.FromMinutes(10)
};
```

Only opt in a CDN if it is trusted to receive those credentials. Library-applied authentication requires HTTPS by default;
`AllowInsecureAuthentication = true` is an explicitly unsafe development-only escape hatch, not a production recommendation.
It does not disable origin checks. Anonymous HTTP remains supported for compatibility; use trusted HTTPS metadata and APK endpoints
in production, with system certificate validation (never `AllowAllSslValidationPolicy`).

**Compatibility changes:** internally created clients no longer follow redirects, even within the same origin.
Configure the final verification/APK URLs rather than relying on 3xx responses. If you inject an `HttpClient`, you own its
handler/default headers: disable automatic redirects and avoid unrestricted credential headers yourself. The library cannot
prevent an injected handler from following redirects or transmitting its own headers. When `httpOptions` is provided,
the existing factory behavior still constructs internal clients instead of using the supplied client.
Retry limits apply to the complete download attempt (HEAD + GET/body); they do not add retry to metadata discovery.
With no `httpOptions`, downloads retain a single attempt.

### UI and lifecycle

Keep the core UI-free: implement this adapter in the **Avalonia host**, not in the Android library:

```csharp
using Avalonia.Threading;
using GeneralUpdate.Avalonia.Android.Abstractions;

public sealed class AvaloniaUpdateDispatcher : IUpdateEventDispatcher
{
    public void Dispatch(Action callback) => Dispatcher.UIThread.Post(callback);
}
```

Pass `eventDispatcher: new AvaloniaUpdateDispatcher()` to `CreateDefault`. Bind progress/results to your ViewModel
from the dispatched events, and retain the event delegates so the ViewModel can unsubscribe with `-=` when released.
Coalesce frequent progress updates in the host rather than enqueueing expensive rendering for every buffer.
Pre-check is synchronous and is **not** dispatched: read already-captured application policy, not controls, inside it.
Never call `.Wait()`/`.Result` on another update operation or on asynchronous disposal from a callback.
An `async void` event handler must catch its own exceptions after an `await`; synchronous subscriber isolation cannot catch them.

`Dispose()` now requests shutdown without blocking and releases resources after current operations and gate waiters finish.
When deterministic cleanup is needed, the concrete bootstrap implements `IAsyncDisposable`; callers holding `IAndroidBootstrap`
can use `if (bootstrap is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync(); else bootstrap.Dispose();`.
Await it outside callbacks. A custom dependency that ignores cancellation can delay cleanup.
Cancellation while waiting for the gate still throws `OperationCanceledException` without changing the active operation's state;
cancellation during verification returns a canceled result. Notification exceptions are logged and isolated, whereas a pre-check
exception produces a failed validation result. Installed-version confirmation is still not part of disposal or a completed event.

Use a single host coordinator and a private staging directory for the full check → download/verify → install sequence.
Only hand the returned verified path to the installer; do not modify or remove the APK while installation may be reading it.
The public installer method also supports independent calls, so it does not establish verification provenance for arbitrary paths.
Persist the target version before handoff, reconcile the actual installed version on next launch, and clear obsolete staging files
only when no update/installer is using them. Keep resumable partial files for a bounded retention period.
Permission prompting, actual installation outcome, app relaunch and recovery from a bad release or data migration remain host/platform
responsibilities; they are not made reliable merely by a successful installer intent.

## Source Review and Production Readiness

### Remediation status

The following fixes address the concrete defects identified in the original review. The historical assessment below is
retained with revision-pinned evidence; **its defect descriptions refer to the pre-fix revision**, not the current implementation.

| Finding | Current behavior | Regression coverage |
|---|---|---|
| B1 — retry/resume | Configured attempts cover HEAD, GET and interrupted body reads with fresh requests. Only transport failures retry; local storage failures do not. Stale 416/invalid range responses discard unsafe partial state; retries restart from zero. Resume uses `If-Range` and validates `Content-Range`; absent validators cause a full download. | `HttpResumableApkDownloaderTests`: transient statuses, dropped bodies, retry exhaustion, 200/206/416, inconsistent ranges/validators, corrupt sidecars and storage errors. |
| B2 — terminal state | Hash cancellation returns `Canceled`; storage/permission failures return failed results. Rejected-file cleanup is best-effort and cannot replace the original failure. | `BootstrapLifecycleTests`: hash cancellation, size lookup and cleanup failures, one failure notification and released gate. |
| B3 — client ownership | Factory-created download clients are owned and released; supplied clients remain host-owned. | `HttpResumableApkDownloaderTests`: owned versus injected client/handler disposal. The Android factory itself still needs platform-build/device validation. |
| B4 — shutdown | `Dispose()` requests cancellation without blocking, rejects new calls, and defers resource release until active operations and waiters drain. Concrete `AndroidBootstrap.DisposeAsync()` waits for release. | `BootstrapLifecycleTests`: active operations, gate waiters, repeated disposal and disposal from callbacks. Noncooperative dependencies can still delay shutdown. |
| B5 — timeout | Internal probe/download timeout reports `NetworkError`; caller cancellation reports `Canceled`. The overall token covers asynchronous storage writes and flushes as well as network reads. | `HttpResumableApkDownloaderTests`: caller cancellation, probe/body timeout and storage-write timeout. |
| S1 — credential forwarding | Global download authentication is origin-scoped; additional CDN origins require host opt-in. Library-created clients do not follow redirects. | `AuthenticationOriginTests`: exact origin matching, off-origin and missing-trust cases, CDN opt-in, package-auth precedence and redirect rejection. |
| Callback errors | Synchronous notification subscriber, dispatcher and logger exceptions cannot replace operation outcomes. A throwing pre-check fails validation instead of bypassing host policy. | `BootstrapLifecycleTests`: throwing subscribers/loggers/dispatchers and fail-closed pre-check. |
| Package license | NuGet metadata now declares Apache-2.0, matching the existing LICENSE. | MSBuild property evaluation against LICENSE. |

Validation after remediation: **160/160 core tests passed** (no failures or skips), including HEAD-rejection fallback and
authentication-policy failures returning terminal validation results before provider/network invocation.
The local Android build could not run because the `android` workload is missing (`NETSDK1147`); current PR CI requires approval.
Tests for these fixes use the existing .NET core test project; they are not Android device installation tests.
The fixes do **not** add desktop support, installer completion callbacks, automatic restart/rollback, independent manifest signing,
APK identity preflight, persisted workflow state, directory-wide coordination, or a runnable Avalonia sample.
UI dispatch, verified-path handoff, cache retention and next-launch reconciliation remain explicit host responsibilities described above.
Metadata-provider/storage extensibility and production dependency/device validation remain follow-up work, not silently resolved findings.

### Historical scope and evidence (before remediation)

Review for issue #18, dated **2026-09-19**, against commit
[`c3d8751`](https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/commit/c3d87519de8fb97d3bebd1b1b0177b33cf0047e3).
Source links below are pinned to that revision; findings are not claims about other GeneralUpdate repositories or future releases.
This checkout contains an **Android-only, UI-free library**, not an Avalonia desktop updater. The [project target and dependencies][review-project]
specify `net10.0-android`, API 26+, AndroidX Core and build-time SourceLink; there is no Avalonia or GeneralUpdate.Core package reference.
References to GeneralUpdate.Core describe matching API semantics, not delegation to its implementation.

**Verdict: not ready as a turnkey, cross-platform, closed-loop production updater.**
It is a useful foundation for a controlled Android integration. Concrete defects now have the fixes listed above;
the platform boundaries and application/device release gates still apply.

Classification: **bug** = source-supported incorrect behavior under the stated trigger;
**architecture risk** = missing guarantee or integration responsibility;
**experience improvement** = documentation, API or operational usability.
P1 means address before production in affected deployments; P2 means planned hardening. These priorities are not CVSS scores.

### 1. Update process closed-loop

| Stage | Implemented behavior and evidence | Breakpoint / recovery assessment |
|---|---|---|
| Version check | [Bootstrap, lines 73–215][review-bootstrap] queries the configured server, compares versions, and invokes the pre-check only for a newer non-forced update. [Package client][review-client] supports verification POST or JSON GET, filters full APKs in the POST protocol and validates metadata. | Request/protocol failures become failed results; request cancellation becomes `Canceled`. No automatic check retry or persisted check state. Forced updates only bypass pre-check; they do not force Android installation. |
| Download | [Downloader, lines 75–188 and 329–393][review-downloader] streams to `.part`, keeps a JSON sidecar, compares URL/hash/size/ETag/Last-Modified, and resumes using Range. A server returning 200 to a Range request restarts from zero. | Interrupted partial files are intentionally retained for a later call, not automatically resumed in the background. Missing/corrupt/inconsistent sidecars restart the download. Retry does not cover the GET/body (B1). A 416 response is returned as a network error without resetting the partial file; a repeated attempt can hit the same breakpoint. No `If-Range` or `Content-Range` validation; final SHA-256 still rejects incorrect bytes. |
| Verification | [Bootstrap, lines 236–285][review-bootstrap] checks size when supplied, then SHA-256; rejected files are deleted. [Validator, lines 37–72][review-hash] streams the file and disposes the hash and stream. | Detects corruption against the supplied digest, not independent publisher authenticity. Hash cancellation and cleanup failures can escape without a terminal snapshot/event (B2). There is no automatic retry after a corrupted package. |
| Installation | [Installer, lines 28–128][review-installer] checks authority, file presence, context and install permission, then grants read access through FileProvider and starts the Android installer. | Missing permission returns `InstallPermissionDenied`; URI/intent launch exceptions return `InstallLaunchFailed`. The host must obtain permission and retry. User rejection, signing mismatch, invalid APK contents and actual install completion are outside the returned result. |
| Restart and rollback | [Bootstrap, lines 293–320][review-bootstrap] remains in `Installing` after a successful handoff. It has no installed-version confirmation, relaunch, backup or rollback phase. | `AddListenerUpdateCompleted` also fires at `ReadyToInstall` and installer handoff: **neither means installation succeeded**. Persist the intended version in the host and reconcile it on the next launch. Plan recovery for a correctly signed but broken release and incompatible data migrations; Android installation checks are not an application-health rollback mechanism. |

Network failures, disk-full and permission failures are not equivalent: the downloader maps `IOException` to `FileIoError`,
but `UnauthorizedAccessException` falls into `Unknown`. There is no free-space preflight or cache-retention policy.
Keep one coordinator per download directory and define bounded retry, user-visible recovery and cleanup policies.
Do not delete a verified APK while the external installer may still need it.

### 2. Developer-friendliness

- **Strengths:** the [module guide][review-guide] includes API/result tables, pre-check examples, manifest permission,
  FileProvider XML and Activity-provider guidance. Three explicit async operations make progress and error presentation host-controlled.
- **Experience improvement — samples:** no runnable Avalonia host/sample is included. Add a minimal Android MVVM sample covering
  permission return, cancellation, UI dispatch, subscription cleanup and next-launch version reconciliation; a code snippet alone
  does not validate those integration steps.
- **API clarity:** `true` from pre-check means *skip*, and forced updates bypass it. `Completed` events describe a stage, not the
  whole update. Cancellation sometimes returns a result and sometimes throws. Document these contracts together; consider
  distinct package-ready and installer-launched notifications and a consistent cancellation contract.
- **UI customization:** there is no built-in UI to theme or replace, so customization is unrestricted but entirely the host's work.
  The default dispatcher is not an Avalonia dispatcher (A1 below). Numeric versions use
  [`System.Version`, not SemVer prerelease ordering][review-versions]; inject `IVersionComparer` when needed.
- **Cross-platform cost:** Android requires the workload, API 26+, installation permission, FileProvider paths and Activity lifecycle
  handling. Windows/macOS/Linux need separate installers and orchestration; passing core tests on Linux is not desktop-update support.
  The stale .NET 8 prerequisites in this English README are corrected by this review.
- **Distribution metadata defect:** [package metadata declares MIT][review-props], while the [repository license is Apache-2.0][review-license].
  The maintainer should reconcile the intended license before distributing a production package; this review does not choose a license.

### 3. Potential bugs and security risks

The following were source-confirmed findings at the reviewed revision. See the remediation table above for fixes and new tests.

| ID / priority / classification | Trigger, evidence and impact | Suggested correction / focused regression |
|---|---|---|
| B1 / P1 / reliability bug | `MaxRetryAttempts` suggests transient download retries, but [the only `WithRetryAsync` call wraps HEAD; GET/body are outside it][review-downloader] (101–123, 288–327). HEAD error status codes are converted to a tuple instead of thrown (239–247), and ordinary connection exceptions with no HTTP status are not considered transient. A transient GET/503 therefore fails immediately. | Define which requests/statuses retry, recreate each request, and resume safely after a body interruption. Test GET 503→success, a dropped stream and retry exhaustion. The catch filter does correctly propagate an exhausted exception; there is no infinite-retry-loop finding. |
| B2 / P1 / state/error-handling bug | [Hash cancellation is rethrown][review-hash] (57–59), while [download/verify orchestration only has `finally`][review-bootstrap] (217–290). Cancel during hashing and the snapshot remains `Verifying`, with no failure event. A size lookup or rejected-file deletion throwing also escapes and leaves a stale stage. | Normalize cancellation and storage failures at the orchestration boundary without hiding the original failure. Test hash cancellation and denied cleanup; assert terminal state, one notification and gate release. |
| B3 / P2 / resource ownership bug | With neither `httpClient` nor `httpOptions`, [the factory creates a download client][review-factory] (47–50), but [the receiving constructor marks it externally owned][review-downloader] (29–38) and disposal only closes owned clients (450–455). Disposing that default bootstrap never disposes this client/handler. | Track ownership of factory-created clients; continue leaving genuinely injected clients host-owned. Test repeated factory create/dispose and handler disposal. |
| B4 / P1 / lifecycle race bug | [Dispose destroys the semaphore immediately][review-bootstrap] (382–397), but in-flight methods still release it in `finally` (287–290, 316–319). Disposing during an operation can cause `ObjectDisposedException` and mask its outcome. | Until coordinated shutdown exists, cancel and await operations before disposal. Add an asynchronous shutdown/drain contract and a dispose-during-download regression. |
| B5 / P2 / diagnostic bug | [All download `OperationCanceledException`s map to user cancellation][review-downloader] (83–103, 190–199), including internally configured HEAD/download timeout expiry. This differs from version-check timeout handling, which reports `NetworkError`. | Distinguish caller cancellation from timeout; test both tokens independently. The overall timeout also does not bound storage writes, which use the caller token (140, 162). |
| S1 / P1 / security bug (medium severity) | [Global authentication is applied to metadata-selected download URLs][review-downloader] (239–243, 257–284) without origin restrictions; [metadata permits arbitrary HTTP(S) hosts][review-client] (194–208). If global reusable credentials are configured and an attacker controls a referenced host or published download URL, that host receives them on HEAD, before any hash check. | Separate verification/download credentials or require an explicit trusted-origin policy in the auth provider. Test that an off-origin metadata URL never receives credentials. This finding does not require installing a malicious APK. |

Additional architecture/integration risks:

- **A1 / P1 — UI thread and callbacks:** [the default dispatcher invokes inline][review-dispatcher], while bootstrap/download awaits use
  `ConfigureAwait(false)`. Event handlers and the synchronous pre-check are not guaranteed to run on the Avalonia UI thread.
  Supply an `IUpdateEventDispatcher` that posts to `Dispatcher.UIThread`; pre-check runs separately and must not directly access controls.
  Callbacks execute while the operation gate is held: synchronously waiting for another update operation can deadlock, and a throwing
  subscriber can escape or turn progress reporting into a download failure. Keep callbacks short/nonthrowing and throttle UI progress.
  Normal C# events support `-=`; unsubscribe when a ViewModel is released if the bootstrap outlives it. There is no demonstrated
  Avalonia-control leak because this library contains no controls.
- **A2 / P1 — trust boundary:** SHA-256 uses the checksum from the same metadata source, not a signed manifest.
  [HTTP is accepted][review-client]; [permissive TLS is an explicit option][review-tls], not the default.
  Require trusted HTTPS endpoints and certificate validation, consider signed metadata, and restrict credential origins.
  The installer does not preflight package ID/signing certificate/version code, nor does `LaunchInstallerAsync` bind its file path to
  a prior verification result. Preserve the verified handoff in app-private storage; consider a verified-package handle and identity
  checks. Android still enforces its own installation/signature rules—this is **not** evidence of a signature bypass.
- **A3 / P2 — paths, locks and recovery:** [filenames are sanitized and joined with `Path.Combine`][review-downloader] (77–81, 427–447).
  There is no archive extraction in this implementation; no Zip Slip or metadata-driven traversal was established.
  [Storage uses exclusive write streams][review-storage], and the downloader closes its stream before renaming (149–175), so the old
  rename-while-open defect is not present. The operation gate is per bootstrap, not per directory/process: two bootstraps targeting the
  same filename can conflict or delete one another's files. Use a single coordinator or isolated staging paths.
  Partial files enable resume, but stale releases need bounded retention rather than unconditional deletion on cancellation.

### 4. Architecture design

**Good separation:** constructor-injected downloader, validator, installer, storage, comparer, dispatcher and logger make the
[orchestrator][review-bootstrap] testable without UI. This is MVVM-compatible service design, not an MVVM implementation:
ViewModels, commands, bindings and dispatch remain in the host. There is no source evidence of UI/Core coupling through GeneralUpdate.Core.

**Extensibility limits:** bootstrap constructs the concrete internal `HttpUpdatePackageClient` rather than accepting a discovery
interface; a nonstandard protocol needs an adapter endpoint or code changes. The default factory hard-wires storage/downloader/hash/installer;
advanced replacements require constructing `AndroidBootstrap` directly. `Sha256HashValidator` also opens physical files rather than using
`IFileStorage`, so a virtual storage implementation cannot be substituted throughout the pipeline.
Consider an injectable metadata provider and consistent storage abstraction only when those use cases are required.

**State/operational limits:** the semaphore serializes individual calls, not an entire multi-call update transaction.
Snapshots are in-memory and do not establish verified-package provenance or crash recovery. A host coordinator should own the sequence,
shutdown and post-install reconciliation. The default logger is no-op; production hosts need stage/failure telemetry without credentials.

**Dependency risks:** [AndroidX Core is the runtime package dependency; SourceLink is private build tooling][review-project].
No dependency advisory audit or transitive inventory was performed for this assessment, so version age alone is not a vulnerability finding.
Validate the resolved dependency graph, Android workload/toolchain compatibility and packaged artifact on supported devices before release.

### Original validation evidence and remaining production release gates

- **Observed:** the existing Release run passed all **3** `UpdateFlowEndToEndTests`, then all **48** core tests, without skips.
  [Main CI run 35453852521](https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/actions/runs/35453852521) also passed 48 tests and Android build/pack.
  The inspected earlier failed run 35446303238 exposed no jobs/logs; it is not evidence of a current source failure.
- **Limits:** [tests link platform-independent sources][review-tests], excluding the default factory and real Android installer.
  [Flow tests use fake HTTP, random bytes, real storage/hash and a recording installer][review-flow-tests]—not a signed APK on a device.
  No local Android workload/device was available; installer permissions, actual install/restart and Avalonia UI dispatch were not exercised.
  Those original results predate the remediation and its new regression tests.
- **Before production:** validate the remediation in the host application, including explicit authentication-origin configuration.
  Exercise interruption/resume (200/206/416, changed ETag, corrupt sidecar), disk-full/permission/locked-file errors,
  timeout versus cancellation, competing coordinators, subscriber exceptions and UI-thread dispatch.
- **On real Android devices:** test denied/granted install permission, bad FileProvider paths, user cancellation, wrong package/signature,
  process death, installer success, next-launch version confirmation and failed-release/data-migration recovery.
  Treat these as release acceptance criteria, not guarantees inferred from passing unit tests.

**Bottom line:** use as an Android update building block only after application-specific hardening and device validation.
Do not advertise unattended install success, automatic rollback/restart, or desktop support from this repository's current implementation.

[review-project]: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/blob/c3d87519de8fb97d3bebd1b1b0177b33cf0047e3/src/GeneralUpdate.Avalonia.Android/GeneralUpdate.Avalonia.Android.csproj#L1-L43
[review-bootstrap]: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/blob/c3d87519de8fb97d3bebd1b1b0177b33cf0047e3/src/GeneralUpdate.Avalonia.Android/Services/AndroidBootstrap.cs#L8-L403
[review-client]: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/blob/c3d87519de8fb97d3bebd1b1b0177b33cf0047e3/src/GeneralUpdate.Avalonia.Android/Services/HttpUpdatePackageClient.cs#L16-L208
[review-downloader]: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/blob/c3d87519de8fb97d3bebd1b1b0177b33cf0047e3/src/GeneralUpdate.Avalonia.Android/Services/HttpResumableApkDownloader.cs#L29-L455
[review-hash]: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/blob/c3d87519de8fb97d3bebd1b1b0177b33cf0047e3/src/GeneralUpdate.Avalonia.Android/Services/Sha256HashValidator.cs#L9-L76
[review-installer]: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/blob/c3d87519de8fb97d3bebd1b1b0177b33cf0047e3/src/GeneralUpdate.Avalonia.Android/Services/AndroidApkInstaller.cs#L28-L128
[review-guide]: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/blob/c3d87519de8fb97d3bebd1b1b0177b33cf0047e3/src/GeneralUpdate.Avalonia.Android/README.en.md#L17-L187
[review-versions]: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/blob/c3d87519de8fb97d3bebd1b1b0177b33cf0047e3/src/GeneralUpdate.Avalonia.Android/Services/SystemVersionComparer.cs#L7-L25
[review-props]: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/blob/c3d87519de8fb97d3bebd1b1b0177b33cf0047e3/Directory.Build.props#L1-L5
[review-license]: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/blob/c3d87519de8fb97d3bebd1b1b0177b33cf0047e3/LICENSE#L1-L3
[review-factory]: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/blob/c3d87519de8fb97d3bebd1b1b0177b33cf0047e3/src/GeneralUpdate.Avalonia.Android/GeneralUpdateBootstrap.cs#L10-L69
[review-dispatcher]: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/blob/c3d87519de8fb97d3bebd1b1b0177b33cf0047e3/src/GeneralUpdate.Avalonia.Android/Services/ImmediateEventDispatcher.cs#L5-L8
[review-tls]: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/blob/c3d87519de8fb97d3bebd1b1b0177b33cf0047e3/src/GeneralUpdate.Avalonia.Android/Services/SslValidationPolicies.cs#L20-L32
[review-storage]: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/blob/c3d87519de8fb97d3bebd1b1b0177b33cf0047e3/src/GeneralUpdate.Avalonia.Android/Services/PhysicalFileStorage.cs#L7-L46
[review-tests]: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/blob/c3d87519de8fb97d3bebd1b1b0177b33cf0047e3/tests/GeneralUpdate.Avalonia.Android.Tests/GeneralUpdate.Avalonia.Android.Tests.csproj#L1-L58
[review-flow-tests]: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/blob/c3d87519de8fb97d3bebd1b1b0177b33cf0047e3/tests/GeneralUpdate.Avalonia.Android.Tests/UpdateFlowEndToEndTests.cs#L23-L207

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
