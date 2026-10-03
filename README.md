# GeneralUpdate.Avalonia

[![GitHub Stars](https://img.shields.io/github/stars/GeneralLibrary/GeneralUpdate.Avalonia?style=flat-square)](https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/stargazers)
[![GitHub Forks](https://img.shields.io/github/forks/GeneralLibrary/GeneralUpdate.Avalonia?style=flat-square)](https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/network/members)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg?style=flat-square)](./LICENSE)
[![NuGet](https://img.shields.io/nuget/v/GeneralUpdate.Avalonia.Android?style=flat-square)](https://www.nuget.org/packages/GeneralUpdate.Avalonia.Android/)

---

![](./imgs/banner.png)

## 项目简介

`GeneralUpdate.Avalonia` 是面向 Avalonia 应用的更新能力仓库。当前核心模块为 `GeneralUpdate.Avalonia.Android`，提供 Android 平台自动更新流程编排能力（无 UI），适配 `net10.0-android`，面向 Avalonia 12+ 应用。

项目将更新流程拆分为可组合的抽象接口，便于在不同业务场景下替换版本比较、下载、哈希校验、安装拉起、日志与事件分发实现。

## 核心特性

- **Android 更新核心（无 UI）**：宿主应用可完全控制弹窗、进度和错误展示。  
- **完整更新链路**：版本校验 → 断点续传下载 → SHA-256 校验 → 安装器拉起。  
- **可扩展架构**：`IVersionComparer`、`IUpdateDownloader`、`IHashValidator`、`IApkInstaller` 等均可替换。  
- **断点续传下载**：支持 sidecar 元数据与流式写入，提升弱网场景稳定性。  
- **统一事件通知**：提供验证、进度、完成、失败等事件用于 UI/日志集成。  

## 快速开始

### 环境准备

- .NET SDK：`10.0+`
- 平台：`Android (net10.0-android)`
- Avalonia：`12+`
- Git：`2.30+`

### 安装步骤

1. 克隆仓库

```bash
git clone https://github.com/GeneralLibrary/GeneralUpdate.Avalonia.git
cd GeneralUpdate.Avalonia
```

2. 安装依赖（以 NuGet 包方式使用）

```bash
dotnet add package GeneralUpdate.Avalonia.Android
```

3. 本地构建与测试（仓库开发）

```bash
dotnet test tests/GeneralUpdate.Avalonia.Android.Tests/GeneralUpdate.Avalonia.Android.Tests.csproj
```

### 基本使用示例

示例中的 `androidPlatformId` 由宿主配置为服务端实际使用的 Android 平台编号。

```csharp
using GeneralUpdate.Avalonia.Android;
using GeneralUpdate.Avalonia.Android.Models;

var cacheDirPath = Android.App.Application.Context.CacheDir?.AbsolutePath
    ?? Path.GetTempPath();

var options = new AndroidUpdateOptions
{
    DownloadDirectoryPath = Path.Combine(cacheDirPath, "update"),
    FileProviderAuthority = "com.example.app.generalupdate.fileprovider",

    // ValidateAsync 据此在组件内部请求服务端，调用方只需要提供当前版本
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
    ?? throw new InvalidOperationException("无法读取本机版本。");

// 启动时先离线核对上次安装；记录损坏时明确报错，不自动清空。
var installation = await bootstrap.CheckInstallationAsync(currentVersion);
if (!installation.Success) return;

var prepared = await bootstrap.PrepareUpdateAsync(currentVersion, CancellationToken.None);
if (prepared.IsReadyToInstall && prepared.PackageInfo is { } package && prepared.FilePath is { } path)
{
    await bootstrap.LaunchInstallerAsync(package, path, CancellationToken.None);
}
```

`PrepareUpdateAsync` 在一个操作锁内完成查询、版本比较、pre-check、下载和哈希校验；没有更新或被跳过时
`Success = true`、`IsReadyToInstall = false`。它不会打开安装器或权限页面。需要只检查版本或分阶段控制时，
仍可使用 `ValidateAsync` / `DownloadAndVerifyAsync`。UI 线程切换使用 `IUpdateEventDispatcher`，权限处理见下文。

### 服务端版本校验

`ValidateAsync(currentVersion, cancellationToken)` 只需要当前应用的版本号：组件按
`AndroidUpdateOptions.UpdateServer` 的配置请求服务端，选出最新的完整 APK，与 `currentVersion`
比较，并把发现的包信息放在结果的 `PackageInfo` 中，供下载与安装继续使用。

默认使用 GeneralUpdate 示例服务端的 `POST /Upgrade/Verification` 协议：请求体发送
`version/appKey/appType/platform/productId`，响应为 `{"code":200,"body":[...]}`；客户端映射
`version/url/hash/size/name/updateLog/releaseDate/isForcibly/authScheme/authToken`，并按版本选择最新的
非冻结完整 APK（`packageType` 为 2、0 或省略；`format` 为 `apk`/`.apk`，省略时 URL 路径需以 `.apk` 结尾）。
ZIP、差分包、驱动包不会交给 Android 安装器；`body` 为空数组或没有符合条件的包时视为“无更新”。
**请核对实际部署的地址、响应格式与 Android 平台编号，不要假定固定编号**；协议不同时，可让服务端提供下面的标准 JSON 端点。

静态 JSON 服务端：设置 `UpdateServer.UseJsonEndpoint = true` 并把 `RequestUrl` 指向 JSON 地址，
组件自动 GET 一个 `UpdatePackageInfo`（字段名不区分大小写），例如：

```json
{
  "version": "2.3.0",
  "downloadUrl": "https://example.com/app-release.apk",
  "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "description": "更新说明",
  "isForced": false
}
```

- `sha256` 必须是 APK 实际的 64 位十六进制 SHA-256，不能使用 MD5；`fileSize` 可省略或为 0（表示未知），已知时单位为字节。
- HTTP 204 或 GET JSON `null` 表示无包；请求、协议与元数据错误通过 `UpdateCheckResult.Success = false`、
  `FailureReason` 和 `AddListenerUpdateFailed` 上报，并且不会触发 pre-check。
- 请求期间取消返回 `UpdateState.Canceled`；等待操作锁时取消会抛出 `OperationCanceledException`。
- 查询与下载共用 `CreateDefault` 的一个 HTTP 客户端。外部 `httpClient` 始终保留并由宿主管理，
  可同时传入超时、重试和认证策略；不会改写该客户端的 `Timeout`，实际生效的是较早到期的限制。
  外部客户端的 TLS/代理必须配置在其 handler 上；同时通过 `httpOptions` 指定 TLS/代理会明确抛出
  `ArgumentException`，不会静默替换客户端。未传客户端时，组件创建并释放自己的客户端。
- 工厂默认查询/HEAD 超时 30 秒，整个下载（含重试等待）超时 10 分钟，最多 3 次下载尝试。
  HEAD/GET/响应流的瞬态网络失败会重试并续传，HEAD 返回 405/501 时改用 GET；401 等永久错误及主动取消不重试。
  `MaxRetryAttempts` 包含首次尝试，不作用于元数据查询。超时报告 `Failed/NetworkError`，主动取消报告 `Canceled`。
- 未配置 `UpdateServer` 且未注入 `IUpdatePackageSource` 时，版本查询以 `InvalidMetadata` 失败。
- 仅应查询可信服务器，生产环境请使用 HTTPS。

发现新版本后，`AddListenerUpdatePrecheck` 回调会拿到最新包信息，返回 `true` 跳过、`false` 继续
（强制更新不调用该回调），语义与 `GeneralUpdate.Core` 一致。

## Android 接入前提

库不提供 UI，也不代替宿主申请权限。要真正走完一次更新，宿主必须配置好下面四项，缺一项就会停在半路：

1. **安装权限（Android 8.0+）**：在 `AndroidManifest.xml` 中声明

   ```xml
   <uses-permission android:name="android.permission.REQUEST_INSTALL_PACKAGES" />
   ```

   用户可能仍未授予，此时 `LaunchInstallerAsync` 返回 `FailureReason = InstallPermissionDenied`。
   用下面的 intent 引导用户开启“允许安装未知应用”，授权后重试即可：

   ```csharp
   var context = Android.App.Application.Context;
   context.StartActivity(new Android.Content.Intent(
           Android.Provider.Settings.ActionManageUnknownAppSources,
           Android.Net.Uri.Parse("package:" + context.PackageName))
       .AddFlags(Android.Content.ActivityFlags.NewTask));
   ```

2. **FileProvider**：`AndroidManifest.xml` 的 `android:authorities` 必须与
   `AndroidUpdateOptions.FileProviderAuthority` 完全一致，且 `generalupdate_file_paths.xml` 要覆盖
   `DownloadDirectoryPath`（默认是 `<CacheDir>/update`）。不一致时返回 `InstallLaunchFailed`。

3. **当前 Activity**：`CreateDefault` 默认使用 `NullAndroidActivityProvider`，此时安装器通过
   `Application.Context` + `FLAG_ACTIVITY_NEW_TASK` 拉起。传入实现 `IAndroidActivityProvider` 的
   provider（返回当前 `Activity`）更稳妥：

   ```csharp
   using var bootstrap = GeneralUpdateBootstrap.CreateDefault(options, activityProvider: myActivityProvider);
   ```

4. **服务端**：配置 `AndroidUpdateOptions.UpdateServer`（或静态 JSON / 自定义 `IUpdatePackageSource`），
   且 `sha256` 为 64 位十六进制 SHA-256。

`LaunchInstallerAsync` 返回 `Success = true` 只表示安装器已拉起，**不代表用户已完成安装**：安装完成后进程会被
系统结束，下次启动时调用 `CheckInstallationAsync` 核对本机实际版本，以确认这次更新是否真正生效。

### 升级结果闭环

`CreateDefault` 默认在 `<FilesDir>/update/installation.json` 保存最近一次安装目标，而不是保存在可被清理的
APK 缓存中。可以通过 `InstallationStateFilePath` 指定其他持久化位置。同一文件只使用一个 bootstrap 实例。
安装目标在拉起安装器**之前**原子写入；写入失败会返回 `FileIoError`，不会继续拉起安装器。记录不包含下载令牌。

宿主在启动或从安装器返回时，从 Android `PackageManager` 读取当前版本，再调用：

```csharp
bootstrap.AddListenerInstallationConfirmed += (_, args) =>
{
    // args.Result.Record.TargetVersion：上次安装目标
    // args.Result.CurrentVersion：本次从设备读取的实际版本
};
var installation = await bootstrap.CheckInstallationAsync(currentVersion, CancellationToken.None);
```

| 结果 | 含义 |
|---|---|
| `Success && State == None` | 没有安装记录，不表示安装成功 |
| `HasPendingInstallation` | 当前版本低于目标，安装尚未确认；可能取消、失败或仍在进行，可重新检查并重试升级 |
| `IsInstalled` | 本机版本已达到或超过目标，状态为 `Installed`，确认结果已持久化 |
| `!Success` | 本机版本无效或记录读取/写入失败，通过 `AddListenerUpdateFailed` 报错，不伪装成“无记录” |

该核对不依赖网络；随后可调用 `ValidateAsync` 查询是否还有更新。`AddListenerInstallationConfirmed`
只在首次持久化确认时触发，重复核对或进程重启不会重复发送；它不是可靠消息队列，界面恢复应以返回的持久化结果为准。
原有 `AddListenerUpdateCompleted` 保持兼容，仍表示 `ReadyToInstall` / `Installing` 阶段完成，**不能用作安装成功通知**。
兼容构造函数不再隐式禁用跟踪：未指定 `installationStateFilePath` 时使用应用
`LocalApplicationData/update/installation.json`。新依赖注入构造函数要求显式提供 `IInstallationStore`。

记录损坏时，由宿主明确提示用户后调用 `await bootstrap.ResetInstallationAsync()`，并检查返回的 `Success`。
重置只删除升级记录，不修改已安装应用、下载文件或服务端配置；不要在遇到任何错误时自动重置。

### 扩展与生命周期

自有协议实现 `IUpdatePackageSource.GetLatestAsync(currentVersion, ct)`，数据库等存储实现
`IInstallationStore.LoadAsync/SaveAsync/ClearAsync`，通过工厂的命名参数注入即可，不必复制流程代码：

```csharp
using var bootstrap = GeneralUpdateBootstrap.CreateDefault(options,
    packageSource: myPackageSource, installationStore: myInstallationStore);
```

需要替换全部环节时，使用 `AndroidBootstrap(versionComparer, downloader, hashValidator, apkInstaller,
fileStorage, packageSource, installationStore, eventDispatcher?, logger?)`。HTTP 协议和 JSON 持久化分别由
`HttpUpdatePackageClient`、`JsonFileInstallationStore` 实现，流程类不再绑定这些具体实现。
自定义源返回 `null` 表示无更新，网络/协议错误应抛出对应异常；存储须持久化原子写入，损坏数据应报错，不能返回空记录。

一个升级器对应一个安装记录。Bootstrap 负责释放实现 `IDisposable` 的 downloader/package source；
其余注入依赖（包括 store）由宿主管理，不要跨升级器共享由 Bootstrap 拥有的服务实例。
关闭页面时先取消并等待当前操作，再 `Dispose`。提前 Dispose 会拒绝新调用和排队调用，并延迟到当前操作退出后释放资源，
不会自动取消当前操作，也不会在其 `finally` 释放信号量时抛异常。

示例会保存服务端配置，启动时核对上次升级并自动检查服务端，单独显示升级结果；它不会自动重复弹出安装器。
用户仍需确认系统安装并重新打开应用。生产 APK 必须保持相同包名、兼容签名和递增的 `versionCode`；
本机版本核对不是 APK 签名校验或应用健康检查，也不提供静默安装、自动重启或回滚。

## 目录结构

```text
GeneralUpdate.Avalonia/
├── samples/
│   └── GeneralUpdate.Avalonia.Android.Sample/ # GeneralSpacestation Android 自动升级示例
├── src/
│   └── GeneralUpdate.Avalonia.Android/   # Android 自动更新核心库
├── tests/
│   └── GeneralUpdate.Avalonia.Android.Tests/ # 单元测试
├── README.md
├── README-EN.md
└── LICENSE
```

## 完整移动端示例

[`samples/GeneralUpdate.Avalonia.Android.Sample`](./samples/GeneralUpdate.Avalonia.Android.Sample)
提供可运行的 Avalonia Android 示例，覆盖 GeneralSpacestation 版本校验、断点续传、SHA-256
校验、未知来源安装授权和 APK 安装器拉起。示例界面可直接填写验证接口、`AppKey`、Android
平台编号和 `ProductId`。连接 Android 设备后可运行
`samples\GeneralUpdate.Avalonia.Android.Sample\run-demo.cmd`，自动构建 `1.0.0`/`2.0.0`
两个 APK、启动兼容验证协议的本地服务并安装演示初始版本。

## 贡献指南

欢迎通过 GitHub 协作流程参与贡献：

1. Fork 本仓库并从 `main` 创建分支：`feature/{{short-description}}`。  
2. 保持变更聚焦，并遵循现有代码风格与命名规范。  
3. 提交前运行现有测试：
   ```bash
   dotnet test tests/GeneralUpdate.Avalonia.Android.Tests/GeneralUpdate.Avalonia.Android.Tests.csproj
   ```
4. 提交 Pull Request，说明动机、实现方案和兼容性影响。  
5. 根据评审反馈迭代，合并后删除分支。  

## 许可证

本项目采用 **Apache License 2.0**。详情请见 [LICENSE](./LICENSE)。

## 联系方式

- 仓库地址：https://github.com/GeneralLibrary/GeneralUpdate.Avalonia
- 问题反馈：https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/issues
- 讨论交流：https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/discussions
