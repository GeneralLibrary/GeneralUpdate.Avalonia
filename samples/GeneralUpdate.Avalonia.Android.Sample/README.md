# GeneralUpdate Avalonia Android 示例

该示例连接 GeneralSpacestation 的 `POST /Upgrade/Verification` 接口，演示完整的 Android
自动升级流程：

1. 读取当前 APK 的 `VersionName`。
2. 使用 `version/appKey/appType/platform/productId` 查询新版本。
3. 断点续传下载完整 APK，并校验服务端提供的 SHA-256。
4. 请求“允许安装未知应用”权限。
5. 返回应用后自动拉起 Android 系统安装器。
6. 在应用持久化目录保存升级目标，重新打开应用后自动核对实际版本并显示“升级已确认”或“升级尚未确认”。
7. 保存服务端配置，启动时自动检查服务端是否还有新版本（不会自动重复弹出安装器）。

## 集成结构

- `Views/MainView`：编译绑定、按钮事件和页面生命周期，不承担升级业务或文件读写。
- `ViewModels/UpdateViewModel`：通过 `PrepareUpdateAsync` 准备升级包，管理权限返回、结果展示与操作取消。
- `Infrastructure/IUpdateHost` / `AndroidUpdateHost`：隔离 PackageManager、SharedPreferences、权限 Intent 和组件组装。
- 组件内 `IUpdatePackageSource` / `IInstallationStore`：分别负责包信息查询和安装记录持久化，可替换为自有服务。

ViewModel 不依赖 Android/Avalonia API，可独立测试。启动时先离线核对安装结果，配置或网络失败不会遮蔽已确认结果。
页面卸载时取消并等待当前操作后释放升级器；从授权页返回仅重试尚在等待授权的安装，不重复下载或反复弹安装器。

## GeneralSpacestation 配置

在 GeneralSpacestation 中创建客户端产品和 Android 完整包，发布包至少需要满足：

- `AppType = 1`（客户端应用）。
- `Platform` 与部署中的 Android 平台编号一致；当前管理端代码使用 `3`，请以实际部署为准。
- `PackageType = 2`（完整包）或兼容的未指定值。
- `Format = apk`，下载地址指向可访问的 `.apk` 文件。
- `Hash` 是 APK 文件的 64 位十六进制 SHA-256。
- `Version` 高于示例工程的 `ApplicationDisplayVersion`。

启动应用后可在页面顶部选择中文或英文（选择会保存），然后填写验证接口、`AppKey`、`Platform` 和 `ProductId`，
点击“检查并自动升级”。界面提示、表单校验和 Android 平台错误会按所选语言显示。

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
“当前版本”应为 `2.0.0`，升级结果应显示“升级已确认：目标 2.0.0，当前 2.0.0”，再次检查应显示已是最新版本。
首次启动的自动检查只发现目标，仍需点击按钮开始下载。

### 闭环验收

- 在系统安装器中取消安装，返回或重新打开应用：仍为 `1.0.0`，显示目标 `2.0.0` 尚未确认；点击按钮可以重试。
- 在未知来源授权页面结束应用进程，授权后重新打开：服务端配置和目标仍保留，点击按钮重新下载校验并安装。
- 完成安装后断网再打开：本机核对仍可确认 `2.0.0`；服务端查询失败单独显示，不会把已确认结果改为升级失败。
- 再次打开 `2.0.0`：仍显示上次确认结果，但不会重复发送首次安装确认事件。
- 安装记录损坏：显示错误并停止升级。由用户点击“重置升级记录”清除损坏记录后，再点击升级按钮恢复。
  重置不会修改已安装应用、下载缓存或服务端设置，也不代表安装成功。

记录默认位于 `<FilesDir>/update/installation.json`，不随下载缓存清理；卸载应用或清除数据会删除记录和服务端配置。
“尚未确认”不区分用户取消、系统拒绝或安装仍在进行，不能当作安装失败回执。更新包必须同包名、兼容签名且
`versionCode` 更高。这里不提供静默安装、安装后自动拉起应用、健康检查或自动回滚。

下载过程中点击“取消”只会中止当前请求：`<CacheDir>/update` 下的 `.part` 片段和续传元数据仍然保留，
再次点击“检查并自动升级”会从断点继续。演示服务的下载地址支持 Range 请求，因此续传时不会重新
下载整个 APK；把演示服务换成正式部署的 GeneralSpacestation 时，同样要求下载地址支持断点续传。
瞬态下载错误最多尝试 3 次（包含首次请求），重试等待包含在总下载超时内；超时和主动取消分别展示为失败与取消。

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
