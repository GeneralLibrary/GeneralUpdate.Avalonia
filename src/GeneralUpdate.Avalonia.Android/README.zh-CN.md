# GeneralUpdate.Avalonia.Android

面向 Avalonia 12+ 应用的 Android 自动更新核心库（无 UI，`net8.0-android`）。

## 功能特性

- 仅针对 Android（`net8.0-android`，设计上兼容 `net9.0-android`）
- 不内置任何界面（弹窗、进度条、错误提示由宿主应用负责）
- 完整流程编排：校验版本 → 下载/断点续传 → 哈希校验 → 拉起安装器
- 基于 `HttpClient` 的流式下载，支持 sidecar 元数据的续传一致性校验
- 关键能力均可替换：版本比较、下载器、哈希校验、安装器、日志、事件分发

## 核心 API

- `GeneralUpdateBootstrap.CreateDefault(...)`
- `IAndroidBootstrap.ValidateAsync(...)`
- `IAndroidBootstrap.DownloadAndVerifyAsync(...)`
- `IAndroidBootstrap.LaunchInstallerAsync(...)`

事件：

- `AddListenerValidate`
- `AddListenerDownloadProgressChanged`
- `AddListenerUpdateCompleted`
- `AddListenerUpdateFailed`

## 快速开始

```csharp
using GeneralUpdate.Avalonia.Android;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Models;

public sealed class AvaloniaUiDispatcher : IUpdateEventDispatcher
{
    public void Dispatch(Action callback)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(callback);
    }
}

var options = new AndroidUpdateOptions
{
    DownloadDirectoryPath = Path.Combine(Android.App.Application.Context.CacheDir!.AbsolutePath!, "update"),
    FileProviderAuthority = "com.example.app.generalupdate.fileprovider"
};

var bootstrap = GeneralUpdateBootstrap.CreateDefault(
    options,
    eventDispatcher: new AvaloniaUiDispatcher());

var packageInfo = new UpdatePackageInfo
{
    Version = "2.3.0",
    DownloadUrl = "https://example.com/app-release.apk",
    Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
    FileSize = 52_428_800,
    FileName = "app-release.apk"
};

var check = await bootstrap.ValidateAsync(packageInfo, "2.2.1", CancellationToken.None);
if (check.UpdateFound)
{
    var prepared = await bootstrap.DownloadAndVerifyAsync(packageInfo, CancellationToken.None);
    if (prepared.Success && prepared.FilePath is not null)
    {
        await bootstrap.LaunchInstallerAsync(packageInfo, prepared.FilePath, CancellationToken.None);
    }
}
```

## 事件通知内容

- `ValidateEventArgs`：`PackageInfo`、`CurrentVersion`
- `DownloadProgressChangedEventArgs`：下载速度、已下载字节、剩余字节、总字节、百分比、包信息、状态描述
- `UpdateCompletedEventArgs` / `UpdateFailedEventArgs`：`Result`（`UpdateOperationResult`）

## Android FileProvider 配置示例

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
