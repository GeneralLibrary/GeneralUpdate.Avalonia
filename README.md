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
- 查询与下载共用 `CreateDefault` 的 `httpOptions`（`RequestTimeout`、代理、TLS 与 `AuthProvider`）；
  未提供 `httpOptions` 时复用传入的 `httpClient`，其生命周期仍由宿主管理。
- 未配置 `UpdateServer` 时调用 `ValidateAsync` 会以 `UpdateFailureReason.InvalidMetadata` 失败。
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

4. **服务端**：必须配置 `AndroidUpdateOptions.UpdateServer`（或改用 `UseJsonEndpoint` 的静态 JSON），
   且 `sha256` 为 64 位十六进制 SHA-256。

`LaunchInstallerAsync` 返回 `Success = true` 只表示安装器已拉起，**不代表用户已完成安装**：安装完成后进程会被
系统结束，下次启动时请自行比较本机版本与服务端版本，以确认这次更新是否真正生效。

## 目录结构

```text
GeneralUpdate.Avalonia/
├── src/
│   └── GeneralUpdate.Avalonia.Android/   # Android 自动更新核心库
├── tests/
│   └── GeneralUpdate.Avalonia.Android.Tests/ # 单元测试
├── README.md
├── README-EN.md
└── LICENSE
```

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
