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
    FileProviderAuthority = "com.example.app.generalupdate.fileprovider"
};

using IAndroidBootstrap bootstrap = GeneralUpdateBootstrap.CreateDefault(options);

var packageInfo = new UpdatePackageInfo
{
    Version     = "2.3.0",
    DownloadUrl = "https://example.com/app-release.apk",
    Sha256      = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
    FileSize    = 52_428_800,
    FileName    = "app-release.apk"
};

var check = await bootstrap.ValidateAsync(packageInfo, "2.2.1", CancellationToken.None);
if (check.UpdateFound)
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

### 在 ValidateAsync 前查询包信息

`HttpUpdatePackageClient.GetPackageInfoAsync` 支持 GET 标准 JSON 元数据，以及 POST GeneralUpdate 验证协议，
无需手动填写每次发布的 `UpdatePackageInfo`。查询与版本比较相互独立，支持取消和现有认证接口。
参见[中文使用示例与 GeneralSpacestation 兼容性说明](../../README.md#在-validateasync-前获取服务器包信息)。

### IAndroidBootstrap 方法

| 方法 | 返回类型 |
|---|---|
| `ValidateAsync(packageInfo, currentVersion, ct)` | `UpdateCheckResult` |
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

## Android FileProvider 配置

添加到 `AndroidManifest.xml`：

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
