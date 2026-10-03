# GeneralUpdate Avalonia Android 示例

该示例连接 GeneralSpacestation 的 `POST /Upgrade/Verification` 接口，演示完整的 Android
自动升级流程：

1. 读取当前 APK 的 `VersionName`。
2. 使用 `version/appKey/appType/platform/productId` 查询新版本。
3. 断点续传下载完整 APK，并校验服务端提供的 SHA-256。
4. 请求“允许安装未知应用”权限。
5. 返回应用后自动拉起 Android 系统安装器。

## GeneralSpacestation 配置

在 GeneralSpacestation 中创建客户端产品和 Android 完整包，发布包至少需要满足：

- `AppType = 1`（客户端应用）。
- `Platform` 与部署中的 Android 平台编号一致；当前管理端代码使用 `3`，请以实际部署为准。
- `PackageType = 2`（完整包）或兼容的未指定值。
- `Format = apk`，下载地址指向可访问的 `.apk` 文件。
- `Hash` 是 APK 文件的 64 位十六进制 SHA-256。
- `Version` 高于示例工程的 `ApplicationDisplayVersion`。

启动应用后填写验证接口、`AppKey`、`Platform` 和 `ProductId`，点击“检查并自动升级”。

## 运行

### 一键端到端演示

连接一台已授权 USB 调试的 Android 设备或启动模拟器，然后在 Windows 命令提示符中运行：

```cmd
samples\GeneralUpdate.Avalonia.Android.Sample\run-demo.cmd
```

如果没有连接设备，脚本会尝试启动 Android SDK 中创建的第一个 AVD。尚未创建 AVD 时，请先在
Android Studio 的 Device Manager 中创建一个 API 26 或更高版本的虚拟设备。

脚本将检查 .NET 10、安装缺失的 Android workload、检查 ADB 设备，然后：

1. 构建待安装的 `1.0.0` APK。
2. 构建作为升级目标的 `2.0.0` APK。
3. 启动一个实现 GeneralSpacestation 验证协议的本地演示服务。
4. 使用 `adb reverse` 将设备的 `127.0.0.1:5080` 映射到电脑。
5. 安装并启动 `1.0.0`。

脚本会先卸载设备上已有的示例应用，以便重复演示从版本 `1` 升级到版本 `2`；示例应用数据也会随之清除。
再次运行脚本前，请关闭上一次打开的 `GeneralUpdate Demo Server` 命令窗口。
`start-demo-now.cmd` 是同一个脚本的入口，便于从任意工作目录直接运行。

在设备中点击“检查并自动升级”，按系统提示允许未知来源安装并确认安装。重新打开应用后，
“当前版本”应为 `2.0.0`。

下载过程中点击“取消”只会中止当前请求：`<CacheDir>/update` 下的 `.part` 片段和续传元数据仍然保留，
再次点击“检查并自动升级”会从断点继续。演示服务的下载地址支持 Range 请求，因此续传时不会重新
下载整个 APK；把演示服务换成正式部署的 GeneralSpacestation 时，同样要求下载地址支持断点续传。

演示服务的固定参数为：

| 参数 | 值 |
|---|---|
| RequestUrl | `http://127.0.0.1:5080/Upgrade/Verification` |
| AppKey | `demo-client` |
| ProductId | `demo-product` |
| Platform | `3` |
| AppType | `1` |

`android:usesCleartextTraffic="true"` 仅用于本机 HTTP 演示。连接正式 GeneralSpacestation 时应改用
HTTPS，并删除该配置。

### 单独构建

```bash
dotnet build samples/GeneralUpdate.Avalonia.Android.Sample/GeneralUpdate.Avalonia.Android.Sample.csproj
```

随后使用 IDE 或 `dotnet build -t:Run` 部署到 Android 设备。真机必须能够访问
GeneralSpacestation 和 APK 下载地址；生产环境应使用 HTTPS。

`AndroidManifest.xml` 中 FileProvider 的 authority 与代码中的
`{PackageName}.generalupdate.fileprovider` 保持一致，下载目录则由
`Resources/xml/generalupdate_file_paths.xml` 暴露。
