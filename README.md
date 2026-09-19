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
    FileProviderAuthority = "com.example.app.generalupdate.fileprovider"
};

using var bootstrap = GeneralUpdateBootstrap.CreateDefault(options);
var packageInfo = new UpdatePackageInfo
{
    Version = "2.3.0",
    DownloadUrl = "https://example.com/app-release.apk",
    Sha256 = "REPLACE_WITH_ACTUAL_SHA256_HASH",
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

### 在 ValidateAsync 前获取服务器包信息

`ValidateAsync` 只比较传入的版本，`AddListenerUpdatePrecheck` 也不会查询服务器。
使用独立的 `HttpUpdatePackageClient` 即可先获取、查看 `UpdatePackageInfo`，无需手动填写每次发布的包信息：

```csharp
using GeneralUpdate.Avalonia.Android.Services;

// HttpClient 由宿主管理，可复用并配置超时、代理和 TLS。
using var httpClient = new HttpClient();
var packageClient = new HttpUpdatePackageClient(httpClient);
var packageInfo = await packageClient.GetPackageInfoAsync(
    "https://example.com/android/latest.json", CancellationToken.None);
if (packageInfo is null) return; // 没有可用包，不要传 null 给 ValidateAsync

// 此处可读取版本说明等信息，尚未比较版本或触发更新事件。
var check = await bootstrap.ValidateAsync(packageInfo, "2.2.1", CancellationToken.None);
if (check.Success && check.UpdateFound)
{
    var prepared = await bootstrap.DownloadAndVerifyAsync(packageInfo, CancellationToken.None);
    if (prepared.Success && prepared.FilePath is not null)
    {
        var install = await bootstrap.LaunchInstallerAsync(packageInfo, prepared.FilePath, CancellationToken.None);
        // install.Success 表示已拉起系统安装器，不代表用户已完成安装。
    }
}
```

GET 地址返回 `UpdatePackageInfo` JSON（字段名不区分大小写），例如：

```json
{
  "version": "2.3.0",
  "downloadUrl": "https://example.com/app-release.apk",
  "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "description": "更新说明",
  "isForced": false
}
```

`sha256` 必须替换为实际 APK 的 SHA-256，不能使用 MD5。`fileSize` 可省略或为 0（未知），已知时单位为字节。
HTTP 204 或 JSON `null` 表示无包；HTTP 错误、错误 JSON、缺失/无效元数据及取消操作会抛出异常，宿主应处理，不能视为“无更新”。
仅应查询可信服务器，生产环境使用 HTTPS。

#### GeneralSpacestation / GeneralUpdate 验证协议

对于采用 [GeneralUpdate 示例服务器](https://github.com/GeneralLibrary/GeneralUpdate-Samples/tree/main/src/Server)
`POST /Upgrade/Verification`（或 `/Update/Verification`）协议的部署，使用另一重载：

```csharp
var request = new UpdatePackageRequest
{
    Version = currentVersion,
    AppKey = appKey,
    AppType = 1,
    Platform = serverPlatformId,
    ProductId = productId
};
var packageInfo = await packageClient.GetPackageInfoAsync(verificationUrl, request, cancellationToken);
```

请求发送 `version/appKey/appType/platform/productId`，响应为 `{"code":200,"body":[...]}`。
客户端映射 `version/url/hash/size/name/updateLog/releaseDate/isForcibly/authScheme/authToken`，
按版本选取最新的非冻结完整 APK（`packageType` 为 2、0 或省略；`format` 为 `apk`/`.apk`，省略时 URL 路径须以 `.apk` 结尾）。
ZIP、差分包、驱动包不会交给 Android 安装器。`body: []` 或没有符合条件的包返回 `null`；
非 200 业务码、缺失或 `null` 的 `body` 会报错。默认使用 `System.Version` 比较版本，也可在构造客户端时传入 `IVersionComparer`。

GeneralSpacestation 商业服务的接口并未公开，**请核对实际部署的地址、响应格式和 Android 平台编号，不要假定固定编号**；
协议不同时可由服务器提供上面的标准 JSON 端点。查询认证通过构造参数 `authProvider` 配置，可复用现有
`BearerTokenAuthProvider`、`ApiKeyAuthProvider`、`BasicAuthProvider` 或 `HmacAuthProvider`。
`AppKey` 仅是请求字段，不会自动启用 HMAC；`HttpDownloadOptions` 不会自动作用于独立查询客户端。

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
