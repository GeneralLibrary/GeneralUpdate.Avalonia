<p align="center">
  <img src="https://raw.githubusercontent.com/GeneralLibrary/GeneralUpdate.Avalonia/main/imgs/banner.png" alt="GeneralUpdate.Avalonia">
</p>

# GeneralUpdate.Avalonia.Android

面向 Avalonia 12+ 应用的 Android 自动更新核心库（无 UI，`net10.0-android`）。

## 功能特性

- **无内置 UI** — 弹窗、进度条、错误提示由宿主应用全权控制
- **更新流程编排** — 版本校验 → 断点续传下载 → SHA-256 校验 → 拉起安装器
- **断点续传下载** — sidecar 元数据 + 流式写入 + 平滑速度报告
- **可替换抽象接口** — 每个环节都可通过接口替换实现
- **操作串行化** — 并发调用自动门控，线程安全

## 快速开始

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

    // ValidateAsync 据此在组件内部请求服务端，调用方只需要提供当前版本
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

## API

### 静态工厂

`GeneralUpdateBootstrap.CreateDefault(options, contextProvider?, activityProvider?, httpClient?, versionComparer?, eventDispatcher?, logger?, httpOptions?)`

默认注入链：

| 抽象接口 | 默认实现 |
|---|---|
| `IAndroidContextProvider` | `DefaultAndroidContextProvider` |
| `IAndroidActivityProvider` | `NullAndroidActivityProvider` |
| `IUpdateLogger` | `NoOpUpdateLogger` |
| `IFileStorage` | `PhysicalFileStorage` |
| `IUpdateDownloader` | `HttpResumableApkDownloader` |
| `IHashValidator` | `Sha256HashValidator` |
| `IApkInstaller` | `AndroidApkInstaller` |
| `IVersionComparer` | `SystemVersionComparer` |

### IAndroidBootstrap 方法

| 方法 | 返回类型 |
|---|---|
| `ValidateAsync(currentVersion, ct)` | `UpdateCheckResult`，包含查询到的 `PackageInfo` |
| `DownloadAndVerifyAsync(packageInfo, ct)` | `UpdateOperationResult` |
| `LaunchInstallerAsync(packageInfo, apkFilePath, ct)` | `InstallResult` |
| `GetSnapshot()` | `UpdateStateSnapshot` |

### 事件

| 事件 | 参数 |
|---|---|
| `AddListenerValidate` | `ValidateEventArgs` |
| `AddListenerDownloadProgressChanged` | `DownloadProgressChangedEventArgs` |
| `AddListenerUpdateCompleted` | `UpdateCompletedEventArgs` |
| `AddListenerUpdateFailed` | `UpdateFailedEventArgs` |

### 服务端版本校验

`ValidateAsync(currentVersion, ct)` 只接收设备上已安装的版本号：组件按 `AndroidUpdateOptions.UpdateServer`
的配置请求服务端、选出最新完整 APK 并比较版本，调用方不再需要自行构造 `UpdatePackageInfo`，
之后的下载与安装直接使用 `check.PackageInfo`。

默认使用 GeneralUpdate 验证协议（`version/appKey/appType/platform/productId` →
`{"code":200,"body":[...]}`）；设置 `UpdateServer.UseJsonEndpoint = true` 则改为 GET 单个
`UpdatePackageInfo` JSON。HTTP 204 或空结果表示“无更新”；请求、协议与元数据错误通过
`UpdateCheckResult.Success`/`FailureReason` 与 `AddListenerUpdateFailed` 上报，不会触发 pre-check。
未配置 `UpdateServer` 时调用会以 `UpdateFailureReason.InvalidMetadata` 失败。校验请求复用 `CreateDefault`
的 `httpOptions`（`RequestTimeout`、代理、TLS、`AuthProvider`）。完整协议说明与 JSON 示例见仓库根 README。

### 更新前回调（Pre-check Hook）

`AddListenerUpdatePrecheck` 对应 `GeneralUpdate.Core` 的 `AddListenerUpdatePrecheck` / `ClientStrategy.UseUpdatePrecheck`：
当 `ValidateAsync` 发现更高版本时、在下载 APK **之前**触发，把获取到的更新信息（`UpdateInfoEventArgs`：包元数据、
当前版本、即将返回的 `UpdateCheckResult`）交给业务层处理。

```csharp
bootstrap.AddListenerUpdatePrecheck(args =>
{
    // args.PackageInfo    — Version / DownloadUrl / Sha256 / FileSize / IsForced 等
    // args.CurrentVersion — 设备当前版本
    // args.Result         — ValidateAsync 即将返回的 UpdateCheckResult

    if (args.PackageInfo.Version == "1.2.0" && !IsWifiConnected())
    {
        return true; // 跳过：等 Wi-Fi 后再拉取 1.2.0
    }

    return false;
});
```

返回 `true` 表示跳过本次更新（与 `GeneralUpdate.Core` 的 `CanSkip` 语义一致），返回 `false` 表示继续。跳过后
`ValidateAsync` 返回 `UpdateFound == false`、状态为 `UpdateState.Completed`，且**不会**触发 `AddListenerValidate`，
因此常规的 `if (check.UpdateFound) { ... }` 流程不会进入下载。强制更新（`UpdatePackageInfo.IsForced`）不会调用该回调。

### 模型继承层次

```
UpdateOperationResult (基类)
├── Success, State, FailureReason, Message, PackageInfo, FilePath, Exception
├── UpdateCheckResult  →  + UpdateFound, CurrentVersion
├── DownloadResult
├── HashValidationResult  →  + ActualSha256, ExpectedSha256
└── InstallResult
```

### 枚举

`UpdateState`: `None`, `Checking`, `UpdateAvailable`, `Downloading`, `Verifying`, `ReadyToInstall`, `Installing`, `Completed`, `Failed`, `Canceled`

`UpdateFailureReason`: `None`, `NetworkError`, `Canceled`, `InvalidMetadata`, `FileIoError`, `HashMismatch`, `ServerDoesNotSupportRange`, `InstallPermissionDenied`, `InstallLaunchFailed`, `VersionComparisonFailed`, `Unknown`

## 持久化流程协调器（推荐）

`GeneralUpdateBootstrap.CreateCoordinator(options)` 返回 `IAndroidUpdateCoordinator`，原有低层 API 保持不变。
使用相同的服务器、FileProvider 和安装权限配置，并传入当前**实际安装版本**：

- `RunAsync`：查询 → 下载并验证 → 持久化意图 → 安装交接，整个尝试串行化；已有记录返回 `PendingUpdateExists`。
- `ReconcileAsync`：下次启动/从安装器返回时离线核对；实际版本达到或超过目标才返回 `Updated`。
- `RetryAsync`：显式恢复待确认尝试，先核对再重新查询、下载和验证同一目标，不安装持久化路径。
  服务端目标改变则返回 `RecoveryRequired` 并保留原记录，需核对/明确放弃；没有记录时用 `RunAsync` 开始新尝试。
- `AbandonAsync`：放弃跟踪（也可恢复损坏的状态文件），不删除 APK、不取消系统安装、不回滚。

`StateChanged` 区分阶段与最终 `Outcome`，`InstallerLaunched` 不等于安装完成。默认将最小化版本化记录
保存在 `<NoBackupFilesDir>/generalupdate/pending-update.json`，同目录写入并刷新临时文件后原子替换。
记录仅含尝试 ID、原始/目标版本、时间和阶段，不保存 URL、文件路径、凭据或异常。
可通过 `pendingStore` 注入私有持久化 `IPendingUpdateStore`，不应使用缓存或备份恢复目录。
默认存储持有跨协作实例/进程的完整流程 `.lock` 独占租约；使用中不要删除锁文件。
自定义存储可实现 `IPendingUpdateStoreLeaseProvider`，否则需单协调器；不同状态文件不能保护共享的 APK 目录。
状态损坏会明确失败，安装前持久化失败会阻止交接；不确定的交接保留待确认状态。

通过 `eventDispatcher` 接入 UI 线程，不要混用直接 bootstrap 调用。协调器包括等待锁在内的取消均返回 `Canceled`；
下载进度与 pre-check 通过 `AddListenerDownloadProgressChanged` / `AddListenerUpdatePrecheck` 转发。
操作前注册策略，`true` 仍表示跳过可选更新，强制更新绕过该回调。
工厂协调器拥有其 bootstrap，直接构造默认不拥有。支持 `await using`，异步释放应在回调之外等待。
旧版本可能仍处于安装等待中，不应自动重试；宿主明确决定重试/放弃。
此闭环确认已观察到的安装版本，不保证应用健康、数据迁移成功、静默安装、自动重启或系统回滚。

## UI 调度与恢复责任

全局下载认证默认仅发送到版本查询端点的源（协议、主机、有效端口，不按路径限制）。
只有明确可信且允许接收相同凭据的 CDN 才应加入 `HttpDownloadOptions.AllowedDownloadAuthenticationOrigins`；
`TrustedAuthenticationOrigin` 可覆盖默认查询源。认证默认要求 HTTPS，`AllowInsecureAuthentication`
仅作为开发环境的显式不安全选项。内部 HTTP 客户端不跟随重定向，需配置最终地址；
注入自有 `HttpClient` 时，宿主必须自行禁用重定向并避免无范围限制的默认认证头。

默认事件分发器直接调用回调，不保证 Avalonia UI 线程。在宿主实现 `IUpdateEventDispatcher`，
通过 `Avalonia.Threading.Dispatcher.UIThread.Post(callback)` 分发事件，并传入 `CreateDefault`。
pre-check 是同步策略判断，不经过 UI 分发器；应读取已捕获的策略数据，而不是操作控件。
ViewModel 释放时使用 `-=` 解绑事件，对高频进度做合并；不要在回调中通过 `.Wait()` / `.Result`
同步等待其他更新操作或异步释放。`async void` 事件处理器须自行捕获 `await` 之后的异常。

每个应用私有下载目录仅使用一个流程协调器，安装器可能仍在读取 APK 时不要修改或删除它。
使用协调器时由其持久化目标版本，宿主在下次启动时调用 `ReconcileAsync` 核对实际安装版本；
直接使用低层 API 时则自行实现此跟踪。安装权限引导、过期缓存保留策略、
应用重启、失败版本恢复及数据迁移回退均由宿主负责，安装器拉起成功不代表更新已经完成。

`Dispose()` 非阻塞地请求取消，待操作和等待者退出后再释放资源。具体类 `AndroidBootstrap` 还实现
`IAsyncDisposable`，需要等待清理时可在回调之外使用。等待操作锁时取消仍抛出异常，校验期间取消则返回取消结果。
通知回调异常被隔离；pre-check 异常会让验证失败而不是绕过策略。配置的下载重试覆盖 HEAD、GET 和中断的正文读取，
不包含元数据查询或本地文件错误。

## Android 接入配置

申报安装权限（Android 8.0+）：缺少时 `LaunchInstallerAsync` 返回 `InstallPermissionDenied`，
需引导用户前往 `Settings.ActionManageUnknownAppSources` 后重试。

```xml
<uses-permission android:name="android.permission.REQUEST_INSTALL_PACKAGES" />
```

在 `AndroidManifest.xml` 中添加 FileProvider。其 `authorities` 必须与
`AndroidUpdateOptions.FileProviderAuthority` 一致，下面的 paths 必须覆盖 `DownloadDirectoryPath`
（默认 `<CacheDir>/update`）；不一致时返回 `InstallLaunchFailed`。建议向 `CreateDefault` 传入
`IAndroidActivityProvider`，从当前 Activity 拉起安装器。`Success = true` 只表示安装器已拉起，
安装完成后进程会被系统结束，请在下次启动时重新比较版本以确认结果。

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

`Resources/xml/generalupdate_file_paths.xml`：

```xml
<?xml version="1.0" encoding="utf-8"?>
<paths>
    <cache-path name="update_cache" path="update/" />
    <files-path name="update_files" path="update/" />
</paths>
```
