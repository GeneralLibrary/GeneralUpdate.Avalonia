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
