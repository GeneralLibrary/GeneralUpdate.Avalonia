# GeneralUpdate.Avalonia.Mobile

[中文](#中文文档) | [English](#english-documentation)

---

## 中文文档

### 📱 Avalonia 移动端自动更新项目

这是一个基于 Avalonia 框架的跨平台移动端自动更新解决方案，支持 Android 和 iOS 平台。

### ✨ 特性

- 🎯 **跨平台支持**: 支持 Android、iOS、Desktop 和 Browser
- 🔄 **自动更新**: 内置完整的自动更新功能
- 🎨 **现代化 UI**: 使用 Avalonia Fluent 主题
- 📦 **MVVM 架构**: 采用 CommunityToolkit.Mvvm 实现
- 🌍 **双语支持**: 中文/英文双语界面

### 🏗️ 项目结构

```
GeneralUpdate.Avalonia.Mobile/
├── GeneralUpdate.Avalonia.Mobile/          # 共享核心项目
│   ├── Services/                           # 服务层
│   │   └── UpdateService.cs               # 自动更新服务
│   ├── ViewModels/                        # 视图模型
│   │   ├── MainViewModel.cs               # 主视图模型
│   │   └── ViewModelBase.cs              # 基础视图模型
│   └── Views/                             # 视图
│       ├── MainView.axaml                # 主视图
│       └── MainWindow.axaml              # 主窗口
├── GeneralUpdate.Avalonia.Mobile.Android/  # Android 项目
├── GeneralUpdate.Avalonia.Mobile.iOS/      # iOS 项目
├── GeneralUpdate.Avalonia.Mobile.Desktop/  # Desktop 项目
└── GeneralUpdate.Avalonia.Mobile.Browser/  # Browser 项目
```

### 🚀 快速开始

#### 前置要求

- .NET 10.0 SDK 或更高版本
- Visual Studio 2022 或 JetBrains Rider
- 对于 Android: Android SDK 和工作负载
- 对于 iOS: macOS 和 Xcode

#### 安装工作负载

```bash
# 安装必要的工作负载
dotnet workload restore
```

#### 构建项目

```bash
# 构建共享项目
dotnet build GeneralUpdate.Avalonia.Mobile/GeneralUpdate.Avalonia.Mobile.csproj

# 构建 Desktop 项目
dotnet build GeneralUpdate.Avalonia.Mobile.Desktop/GeneralUpdate.Avalonia.Mobile.Desktop.csproj

# 构建 Android 项目 (需要 Android SDK)
dotnet build GeneralUpdate.Avalonia.Mobile.Android/GeneralUpdate.Avalonia.Mobile.Android.csproj
```

#### 运行项目

```bash
# 运行 Desktop 版本
dotnet run --project GeneralUpdate.Avalonia.Mobile.Desktop/GeneralUpdate.Avalonia.Mobile.Desktop.csproj

# 运行 Android 版本 (需要模拟器或设备)
dotnet run --project GeneralUpdate.Avalonia.Mobile.Android/GeneralUpdate.Avalonia.Mobile.Android.csproj
```

### 📖 使用说明

#### 1. 配置更新服务器

在 `MainViewModel.cs` 中配置您的更新服务器地址：

```csharp
_updateService.Initialize(
    updateUrl: "https://your-update-server.com/updates",  // 您的更新服务器地址
    appName: "GeneralUpdate.Avalonia.Mobile",             // 应用名称
    currentVersion: CurrentVersion                         // 当前版本号
);
```

#### 2. 实现真实的更新逻辑

当前的 `UpdateService.cs` 包含模拟实现。要实现真实的更新功能，您需要：

1. **添加 GeneralUpdate 包的实际集成**
2. **实现版本检查逻辑** - 从服务器获取最新版本信息
3. **实现下载逻辑** - 下载更新包
4. **实现安装逻辑** - 安装更新并重启应用

示例代码框架：

```csharp
public async Task<bool> CheckForUpdatesAsync()
{
    // 1. 向服务器请求最新版本信息
    var latestVersion = await _httpClient.GetFromJsonAsync<VersionInfo>($"{_updateUrl}/version.json");
    
    // 2. 比较版本号
    if (CompareVersions(latestVersion.Version, _currentVersion) > 0)
    {
        StatusChanged?.Invoke(this, $"发现新版本: {latestVersion.Version}");
        return true;
    }
    
    return false;
}
```

#### 3. 服务器端设置

您需要设置一个更新服务器，提供以下端点：

- `GET /version.json` - 返回最新版本信息
  ```json
  {
    "version": "1.0.1",
    "downloadUrl": "https://your-server.com/updates/app-1.0.1.zip",
    "releaseNotes": "Bug fixes and improvements",
    "minVersion": "1.0.0"
  }
  ```

- `GET /updates/{filename}` - 提供更新包下载

### 🎯 功能说明

#### UpdateService

自动更新服务提供以下功能：

- `Initialize()` - 初始化更新服务
- `CheckForUpdatesAsync()` - 检查是否有可用更新
- `DownloadAndInstallAsync()` - 下载并安装更新

#### 事件

- `StatusChanged` - 更新状态变化时触发
- `ProgressChanged` - 下载进度变化时触发

### 📦 依赖包

- Avalonia 11.3.12
- CommunityToolkit.Mvvm 8.4.0
- GeneralUpdate.Core 10.2.1
- GeneralUpdate.ClientCore 10.2.1

### 🔧 自定义配置

#### 修改应用标识

在 `GeneralUpdate.Avalonia.Mobile.Android.csproj` 中：

```xml
<ApplicationId>com.yourcompany.yourapp</ApplicationId>
```

#### 修改应用图标

替换以下文件：
- Android: `GeneralUpdate.Avalonia.Mobile.Android/Icon.png`
- iOS: `GeneralUpdate.Avalonia.Mobile.iOS/Resources/AppIcon.appiconset/`

### 🐛 故障排除

#### Android 构建失败

```bash
# 确保安装了 Android 工作负载
dotnet workload install android

# 检查 Android SDK 路径
echo $ANDROID_HOME
```

#### iOS 构建失败

```bash
# 确保在 macOS 上并安装了 Xcode
xcode-select --install

# 安装 iOS 工作负载
dotnet workload install ios
```

### 📝 许可证

本项目采用 MIT 许可证。详见 [LICENSE](LICENSE) 文件。

### 🤝 贡献

欢迎贡献！请随时提交 Pull Request。

### 📧 联系方式

如有问题或建议，请提交 Issue。

---

## English Documentation

### 📱 Avalonia Mobile Auto-Update Project

A cross-platform mobile auto-update solution based on the Avalonia framework, supporting Android and iOS platforms.

### ✨ Features

- 🎯 **Cross-platform**: Supports Android, iOS, Desktop, and Browser
- 🔄 **Auto-update**: Built-in complete auto-update functionality
- 🎨 **Modern UI**: Using Avalonia Fluent theme
- 📦 **MVVM Architecture**: Implemented with CommunityToolkit.Mvvm
- 🌍 **Bilingual**: Chinese/English bilingual interface

### 🏗️ Project Structure

```
GeneralUpdate.Avalonia.Mobile/
├── GeneralUpdate.Avalonia.Mobile/          # Shared core project
│   ├── Services/                           # Service layer
│   │   └── UpdateService.cs               # Auto-update service
│   ├── ViewModels/                        # View models
│   │   ├── MainViewModel.cs               # Main view model
│   │   └── ViewModelBase.cs              # Base view model
│   └── Views/                             # Views
│       ├── MainView.axaml                # Main view
│       └── MainWindow.axaml              # Main window
├── GeneralUpdate.Avalonia.Mobile.Android/  # Android project
├── GeneralUpdate.Avalonia.Mobile.iOS/      # iOS project
├── GeneralUpdate.Avalonia.Mobile.Desktop/  # Desktop project
└── GeneralUpdate.Avalonia.Mobile.Browser/  # Browser project
```

### 🚀 Quick Start

#### Prerequisites

- .NET 10.0 SDK or higher
- Visual Studio 2022 or JetBrains Rider
- For Android: Android SDK and workload
- For iOS: macOS and Xcode

#### Install Workloads

```bash
# Install required workloads
dotnet workload restore
```

#### Build Project

```bash
# Build shared project
dotnet build GeneralUpdate.Avalonia.Mobile/GeneralUpdate.Avalonia.Mobile.csproj

# Build Desktop project
dotnet build GeneralUpdate.Avalonia.Mobile.Desktop/GeneralUpdate.Avalonia.Mobile.Desktop.csproj

# Build Android project (requires Android SDK)
dotnet build GeneralUpdate.Avalonia.Mobile.Android/GeneralUpdate.Avalonia.Mobile.Android.csproj
```

#### Run Project

```bash
# Run Desktop version
dotnet run --project GeneralUpdate.Avalonia.Mobile.Desktop/GeneralUpdate.Avalonia.Mobile.Desktop.csproj

# Run Android version (requires emulator or device)
dotnet run --project GeneralUpdate.Avalonia.Mobile.Android/GeneralUpdate.Avalonia.Mobile.Android.csproj
```

### 📖 Usage

#### 1. Configure Update Server

Configure your update server address in `MainViewModel.cs`:

```csharp
_updateService.Initialize(
    updateUrl: "https://your-update-server.com/updates",  // Your update server URL
    appName: "GeneralUpdate.Avalonia.Mobile",             // App name
    currentVersion: CurrentVersion                         // Current version
);
```

#### 2. Implement Real Update Logic

The current `UpdateService.cs` contains a simulated implementation. To implement real update functionality, you need to:

1. **Add actual integration with GeneralUpdate packages**
2. **Implement version check logic** - Get latest version info from server
3. **Implement download logic** - Download update packages
4. **Implement installation logic** - Install updates and restart app

Example code framework:

```csharp
public async Task<bool> CheckForUpdatesAsync()
{
    // 1. Request latest version info from server
    var latestVersion = await _httpClient.GetFromJsonAsync<VersionInfo>($"{_updateUrl}/version.json");
    
    // 2. Compare versions
    if (CompareVersions(latestVersion.Version, _currentVersion) > 0)
    {
        StatusChanged?.Invoke(this, $"New version found: {latestVersion.Version}");
        return true;
    }
    
    return false;
}
```

#### 3. Server-side Setup

You need to set up an update server that provides the following endpoints:

- `GET /version.json` - Returns latest version information
  ```json
  {
    "version": "1.0.1",
    "downloadUrl": "https://your-server.com/updates/app-1.0.1.zip",
    "releaseNotes": "Bug fixes and improvements",
    "minVersion": "1.0.0"
  }
  ```

- `GET /updates/{filename}` - Provides update package downloads

### 🎯 Features

#### UpdateService

The auto-update service provides the following features:

- `Initialize()` - Initialize update service
- `CheckForUpdatesAsync()` - Check for available updates
- `DownloadAndInstallAsync()` - Download and install updates

#### Events

- `StatusChanged` - Triggered when update status changes
- `ProgressChanged` - Triggered when download progress changes

### 📦 Dependencies

- Avalonia 11.3.12
- CommunityToolkit.Mvvm 8.4.0
- GeneralUpdate.Core 10.2.1
- GeneralUpdate.ClientCore 10.2.1

### 🔧 Custom Configuration

#### Modify Application ID

In `GeneralUpdate.Avalonia.Mobile.Android.csproj`:

```xml
<ApplicationId>com.yourcompany.yourapp</ApplicationId>
```

#### Modify App Icon

Replace the following files:
- Android: `GeneralUpdate.Avalonia.Mobile.Android/Icon.png`
- iOS: `GeneralUpdate.Avalonia.Mobile.iOS/Resources/AppIcon.appiconset/`

### 🐛 Troubleshooting

#### Android Build Failed

```bash
# Ensure Android workload is installed
dotnet workload install android

# Check Android SDK path
echo $ANDROID_HOME
```

#### iOS Build Failed

```bash
# Ensure you're on macOS with Xcode installed
xcode-select --install

# Install iOS workload
dotnet workload install ios
```

### 📝 License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.

### 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

### 📧 Contact

For questions or suggestions, please submit an Issue.
