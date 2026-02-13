# Quick Start Guide / 快速入门指南

[中文](#中文指南) | [English](#english-guide)

---

## 中文指南

### 🚀 5 分钟快速开始

#### 第 1 步：克隆项目

```bash
git clone https://github.com/GeneralLibrary/GeneralUpdate.Avalonia.git
cd GeneralUpdate.Avalonia
```

#### 第 2 步：安装依赖

```bash
# 安装 .NET 10.0 SDK（如果尚未安装）
# 下载地址: https://dotnet.microsoft.com/download/dotnet/10.0

# 安装工作负载
dotnet workload restore
```

#### 第 3 步：运行项目

**Desktop 版本（Windows、macOS、Linux）:**

```bash
cd GeneralUpdate.Avalonia.Mobile.Desktop
dotnet run
```

**Browser 版本（WebAssembly）:**

```bash
cd GeneralUpdate.Avalonia.Mobile.Browser
dotnet run
```

然后在浏览器中打开 `https://localhost:5001`

**Android 版本（需要 Android SDK）:**

```bash
# 确保已安装 Android SDK 和工作负载
dotnet workload install android

# 运行（需要连接设备或启动模拟器）
cd GeneralUpdate.Avalonia.Mobile.Android
dotnet run
```

#### 第 4 步：自定义更新服务器

1. 打开 `GeneralUpdate.Avalonia.Mobile/ViewModels/MainViewModel.cs`
2. 找到 `CheckForUpdatesAsync` 方法
3. 修改 `updateUrl` 为您的服务器地址：

```csharp
_updateService.Initialize(
    updateUrl: "https://your-update-server.com/updates",  // 👈 修改这里
    appName: "GeneralUpdate.Avalonia.Mobile",
    currentVersion: CurrentVersion
);
```

#### 第 5 步：设置更新服务器

1. 创建 `version.json` 文件（参考 `version.example.json`）
2. 将文件上传到您的服务器
3. 确保 URL 可访问：`https://your-server.com/updates/version.json`

### 📱 平台特定说明

#### Android

**前置要求:**
- Android SDK
- Java JDK 11+

**首次运行:**
```bash
# 检查 Android SDK 路径
echo $ANDROID_HOME

# 如果未设置，添加到环境变量
export ANDROID_HOME=$HOME/Android/Sdk
export PATH=$PATH:$ANDROID_HOME/tools:$ANDROID_HOME/platform-tools

# 列出可用设备
adb devices

# 运行应用
dotnet run
```

#### iOS

**前置要求:**
- macOS
- Xcode 14+
- Apple Developer 账户

**首次运行:**
```bash
# 列出可用模拟器
xcrun simctl list devices

# 启动模拟器（可选）
open -a Simulator

# 运行应用
cd GeneralUpdate.Avalonia.Mobile.iOS
dotnet run
```

### 🎯 常见任务

#### 修改应用图标

**Android:**
```bash
# 替换图标文件
cp your-icon.png GeneralUpdate.Avalonia.Mobile.Android/Icon.png
```

**iOS:**
```bash
# 在 Xcode 中打开项目并替换 AppIcon
```

#### 修改应用 ID

编辑 `GeneralUpdate.Avalonia.Mobile.Android/GeneralUpdate.Avalonia.Mobile.Android.csproj`:

```xml
<ApplicationId>com.yourcompany.yourapp</ApplicationId>
```

#### 修改应用版本

编辑相应的 `.csproj` 文件：

```xml
<ApplicationVersion>2</ApplicationVersion>
<ApplicationDisplayVersion>1.1.0</ApplicationDisplayVersion>
```

### 🔧 故障排除

#### 问题: 工作负载安装失败

```bash
# 清理并重新安装
dotnet workload clean
dotnet workload restore
```

#### 问题: Android 设备未识别

```bash
# 重启 ADB
adb kill-server
adb start-server
adb devices
```

#### 问题: iOS 签名错误

1. 在 Xcode 中打开项目
2. 选择正确的 Team
3. 确保 Bundle Identifier 是唯一的

### 📚 下一步

- 阅读 [README.md](README.md) 了解详细信息
- 查看 [API_REFERENCE.md](API_REFERENCE.md) 了解 API 用法
- 参考 [DEPLOYMENT.md](DEPLOYMENT.md) 学习如何部署
- 阅读 [CONTRIBUTING.md](CONTRIBUTING.md) 了解如何贡献

---

## English Guide

### 🚀 Get Started in 5 Minutes

#### Step 1: Clone the Project

```bash
git clone https://github.com/GeneralLibrary/GeneralUpdate.Avalonia.git
cd GeneralUpdate.Avalonia
```

#### Step 2: Install Dependencies

```bash
# Install .NET 10.0 SDK (if not already installed)
# Download from: https://dotnet.microsoft.com/download/dotnet/10.0

# Install workloads
dotnet workload restore
```

#### Step 3: Run the Project

**Desktop Version (Windows, macOS, Linux):**

```bash
cd GeneralUpdate.Avalonia.Mobile.Desktop
dotnet run
```

**Browser Version (WebAssembly):**

```bash
cd GeneralUpdate.Avalonia.Mobile.Browser
dotnet run
```

Then open `https://localhost:5001` in your browser

**Android Version (requires Android SDK):**

```bash
# Ensure Android SDK and workload are installed
dotnet workload install android

# Run (requires connected device or emulator)
cd GeneralUpdate.Avalonia.Mobile.Android
dotnet run
```

#### Step 4: Customize Update Server

1. Open `GeneralUpdate.Avalonia.Mobile/ViewModels/MainViewModel.cs`
2. Find the `CheckForUpdatesAsync` method
3. Change `updateUrl` to your server address:

```csharp
_updateService.Initialize(
    updateUrl: "https://your-update-server.com/updates",  // 👈 Change this
    appName: "GeneralUpdate.Avalonia.Mobile",
    currentVersion: CurrentVersion
);
```

#### Step 5: Setup Update Server

1. Create `version.json` file (see `version.example.json`)
2. Upload file to your server
3. Ensure URL is accessible: `https://your-server.com/updates/version.json`

### 📱 Platform-Specific Instructions

#### Android

**Prerequisites:**
- Android SDK
- Java JDK 11+

**First Run:**
```bash
# Check Android SDK path
echo $ANDROID_HOME

# If not set, add to environment variables
export ANDROID_HOME=$HOME/Android/Sdk
export PATH=$PATH:$ANDROID_HOME/tools:$ANDROID_HOME/platform-tools

# List available devices
adb devices

# Run app
dotnet run
```

#### iOS

**Prerequisites:**
- macOS
- Xcode 14+
- Apple Developer account

**First Run:**
```bash
# List available simulators
xcrun simctl list devices

# Launch simulator (optional)
open -a Simulator

# Run app
cd GeneralUpdate.Avalonia.Mobile.iOS
dotnet run
```

### 🎯 Common Tasks

#### Change App Icon

**Android:**
```bash
# Replace icon file
cp your-icon.png GeneralUpdate.Avalonia.Mobile.Android/Icon.png
```

**iOS:**
```bash
# Open project in Xcode and replace AppIcon
```

#### Change Application ID

Edit `GeneralUpdate.Avalonia.Mobile.Android/GeneralUpdate.Avalonia.Mobile.Android.csproj`:

```xml
<ApplicationId>com.yourcompany.yourapp</ApplicationId>
```

#### Change App Version

Edit respective `.csproj` file:

```xml
<ApplicationVersion>2</ApplicationVersion>
<ApplicationDisplayVersion>1.1.0</ApplicationDisplayVersion>
```

### 🔧 Troubleshooting

#### Issue: Workload Installation Failed

```bash
# Clean and reinstall
dotnet workload clean
dotnet workload restore
```

#### Issue: Android Device Not Recognized

```bash
# Restart ADB
adb kill-server
adb start-server
adb devices
```

#### Issue: iOS Signing Error

1. Open project in Xcode
2. Select correct Team
3. Ensure Bundle Identifier is unique

### 📚 Next Steps

- Read [README.md](README.md) for detailed information
- Check [API_REFERENCE.md](API_REFERENCE.md) for API usage
- See [DEPLOYMENT.md](DEPLOYMENT.md) to learn deployment
- Read [CONTRIBUTING.md](CONTRIBUTING.md) to contribute

---

## 🎉 You're Ready!

Now you have a working Avalonia mobile auto-update application. Customize it to fit your needs!

现在您已经拥有一个可工作的 Avalonia 移动端自动更新应用程序。根据您的需求进行自定义吧！

### 🆘 Need Help? / 需要帮助？

- 📖 Check the documentation / 查看文档
- 🐛 Report issues / 报告问题: [GitHub Issues](https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/issues)
- 💬 Ask questions / 提问讨论: [GitHub Discussions](https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/discussions)
