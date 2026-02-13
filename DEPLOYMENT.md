# Deployment Guide / 部署指南

[中文](#中文文档) | [English](#english-documentation)

---

## 中文文档

### 部署到 Android

#### 前提条件

1. 安装 Android SDK
2. 安装 Java JDK 11 或更高版本
3. 安装 .NET Android 工作负载

```bash
dotnet workload install android
```

#### 构建 APK

##### Debug 版本

```bash
cd GeneralUpdate.Avalonia.Mobile.Android
dotnet build -c Debug
```

生成的 APK 位于: `bin/Debug/net10.0-android/com.CompanyName.GeneralUpdate.Avalonia.Mobile-Signed.apk`

##### Release 版本

1. 创建签名密钥（首次）:

```bash
keytool -genkey -v -keystore myapp.keystore -alias myapp -keyalg RSA -keysize 2048 -validity 10000
```

2. 配置签名（在 .csproj 文件中）:

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <AndroidKeyStore>true</AndroidKeyStore>
  <AndroidSigningKeyStore>path/to/myapp.keystore</AndroidSigningKeyStore>
  <AndroidSigningKeyAlias>myapp</AndroidSigningKeyAlias>
  <AndroidSigningKeyPass>your-password</AndroidSigningKeyPass>
  <AndroidSigningStorePass>your-password</AndroidSigningStorePass>
</PropertyGroup>
```

3. 构建 Release APK:

```bash
dotnet publish -c Release -f net10.0-android
```

#### 部署到设备

##### 通过 ADB

```bash
adb install path/to/your-app.apk
```

##### 通过 Google Play Console

1. 构建 AAB (Android App Bundle):

```bash
dotnet publish -c Release -f net10.0-android -p:AndroidPackageFormat=aab
```

2. 登录 Google Play Console
3. 创建新应用或选择现有应用
4. 上传 AAB 文件
5. 填写应用详情和截图
6. 提交审核

### 部署到 iOS

#### 前提条件

1. macOS 系统
2. Xcode 14 或更高版本
3. Apple Developer 账户
4. 安装 .NET iOS 工作负载

```bash
dotnet workload install ios
```

#### 配置签名

1. 在 Xcode 中打开项目
2. 选择您的 Team
3. 配置 Bundle Identifier
4. 配置 Provisioning Profile

#### 构建 IPA

##### Debug 版本（用于测试）

```bash
cd GeneralUpdate.Avalonia.Mobile.iOS
dotnet build -c Debug
```

##### Release 版本

```bash
dotnet publish -c Release -f net10.0-ios
```

#### 部署到设备

##### TestFlight（内部测试）

1. 构建 IPA:

```bash
dotnet publish -c Release -f net10.0-ios /p:ArchiveOnBuild=true
```

2. 使用 Xcode 或 Application Loader 上传到 App Store Connect
3. 在 App Store Connect 中配置 TestFlight
4. 邀请测试人员

##### App Store（正式发布）

1. 准备应用截图和描述
2. 在 App Store Connect 中创建应用
3. 上传构建的 IPA
4. 填写应用信息
5. 提交审核

### 部署到 Desktop

#### Windows

##### 自包含部署

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

生成的可执行文件位于: `bin/Release/net10.0/win-x64/publish/`

##### 框架依赖部署

```bash
dotnet publish -c Release -r win-x64 --self-contained false
```

##### 创建安装程序

使用 WiX Toolset 或 Inno Setup 创建 MSI/EXE 安装程序。

#### macOS

```bash
dotnet publish -c Release -r osx-x64 --self-contained true
```

创建 .app bundle 和 .dmg 文件：

```bash
# 创建 app bundle
mkdir -p YourApp.app/Contents/MacOS
cp -r bin/Release/net10.0/osx-x64/publish/* YourApp.app/Contents/MacOS/

# 创建 DMG
hdiutil create -volname "YourApp" -srcfolder YourApp.app -ov -format UDZO YourApp.dmg
```

#### Linux

```bash
dotnet publish -c Release -r linux-x64 --self-contained true
```

创建 .deb 包（Ubuntu/Debian）或 .rpm 包（Fedora/CentOS）。

### 设置更新服务器

#### 服务器要求

- Web 服务器（Nginx、Apache 或 IIS）
- HTTPS 支持（推荐）
- 足够的存储空间用于托管更新包

#### 目录结构

```
/updates/
├── version.json          # 版本信息
├── android/
│   └── app-1.0.1.apk
├── ios/
│   └── app-1.0.1.ipa
└── desktop/
    ├── win-x64/
    │   └── app-1.0.1.zip
    ├── osx-x64/
    │   └── app-1.0.1.zip
    └── linux-x64/
        └── app-1.0.1.zip
```

#### Nginx 配置示例

```nginx
server {
    listen 443 ssl;
    server_name updates.yourcompany.com;

    ssl_certificate /path/to/certificate.crt;
    ssl_certificate_key /path/to/private.key;

    root /var/www/updates;

    location / {
        add_header Access-Control-Allow-Origin *;
        add_header Access-Control-Allow-Methods "GET, OPTIONS";
        autoindex on;
    }

    location /version.json {
        add_header Content-Type application/json;
        add_header Cache-Control "no-cache, no-store, must-revalidate";
    }
}
```

### CI/CD 集成

#### GitHub Actions 示例

```yaml
name: Build and Deploy

on:
  push:
    tags:
      - 'v*'

jobs:
  build-android:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v2
      - name: Setup .NET
        uses: actions/setup-dotnet@v1
        with:
          dotnet-version: '10.0.x'
      - name: Build Android
        run: |
          dotnet workload install android
          dotnet publish -c Release -f net10.0-android
      - name: Upload APK
        uses: actions/upload-artifact@v2
        with:
          name: android-apk
          path: '**/*.apk'

  build-ios:
    runs-on: macos-latest
    steps:
      - uses: actions/checkout@v2
      - name: Setup .NET
        uses: actions/setup-dotnet@v1
        with:
          dotnet-version: '10.0.x'
      - name: Build iOS
        run: |
          dotnet workload install ios
          dotnet publish -c Release -f net10.0-ios
      - name: Upload IPA
        uses: actions/upload-artifact@v2
        with:
          name: ios-ipa
          path: '**/*.ipa'
```

---

## English Documentation

### Deploy to Android

#### Prerequisites

1. Install Android SDK
2. Install Java JDK 11 or higher
3. Install .NET Android workload

```bash
dotnet workload install android
```

#### Build APK

##### Debug Build

```bash
cd GeneralUpdate.Avalonia.Mobile.Android
dotnet build -c Debug
```

Generated APK location: `bin/Debug/net10.0-android/com.CompanyName.GeneralUpdate.Avalonia.Mobile-Signed.apk`

##### Release Build

1. Create signing key (first time):

```bash
keytool -genkey -v -keystore myapp.keystore -alias myapp -keyalg RSA -keysize 2048 -validity 10000
```

2. Configure signing (in .csproj file):

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <AndroidKeyStore>true</AndroidKeyStore>
  <AndroidSigningKeyStore>path/to/myapp.keystore</AndroidSigningKeyStore>
  <AndroidSigningKeyAlias>myapp</AndroidSigningKeyAlias>
  <AndroidSigningKeyPass>your-password</AndroidSigningKeyPass>
  <AndroidSigningStorePass>your-password</AndroidSigningStorePass>
</PropertyGroup>
```

3. Build Release APK:

```bash
dotnet publish -c Release -f net10.0-android
```

#### Deploy to Device

##### Via ADB

```bash
adb install path/to/your-app.apk
```

##### Via Google Play Console

1. Build AAB (Android App Bundle):

```bash
dotnet publish -c Release -f net10.0-android -p:AndroidPackageFormat=aab
```

2. Login to Google Play Console
3. Create new app or select existing
4. Upload AAB file
5. Fill in app details and screenshots
6. Submit for review

### Deploy to iOS

#### Prerequisites

1. macOS system
2. Xcode 14 or higher
3. Apple Developer account
4. Install .NET iOS workload

```bash
dotnet workload install ios
```

#### Configure Signing

1. Open project in Xcode
2. Select your Team
3. Configure Bundle Identifier
4. Configure Provisioning Profile

#### Build IPA

##### Debug Build (for testing)

```bash
cd GeneralUpdate.Avalonia.Mobile.iOS
dotnet build -c Debug
```

##### Release Build

```bash
dotnet publish -c Release -f net10.0-ios
```

#### Deploy to Device

##### TestFlight (Internal Testing)

1. Build IPA:

```bash
dotnet publish -c Release -f net10.0-ios /p:ArchiveOnBuild=true
```

2. Upload to App Store Connect using Xcode or Application Loader
3. Configure TestFlight in App Store Connect
4. Invite testers

##### App Store (Official Release)

1. Prepare app screenshots and description
2. Create app in App Store Connect
3. Upload built IPA
4. Fill in app information
5. Submit for review

### Deploy to Desktop

#### Windows

##### Self-contained Deployment

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Generated executable location: `bin/Release/net10.0/win-x64/publish/`

##### Framework-dependent Deployment

```bash
dotnet publish -c Release -r win-x64 --self-contained false
```

##### Create Installer

Use WiX Toolset or Inno Setup to create MSI/EXE installer.

#### macOS

```bash
dotnet publish -c Release -r osx-x64 --self-contained true
```

Create .app bundle and .dmg file:

```bash
# Create app bundle
mkdir -p YourApp.app/Contents/MacOS
cp -r bin/Release/net10.0/osx-x64/publish/* YourApp.app/Contents/MacOS/

# Create DMG
hdiutil create -volname "YourApp" -srcfolder YourApp.app -ov -format UDZO YourApp.dmg
```

#### Linux

```bash
dotnet publish -c Release -r linux-x64 --self-contained true
```

Create .deb package (Ubuntu/Debian) or .rpm package (Fedora/CentOS).

### Setup Update Server

#### Server Requirements

- Web server (Nginx, Apache, or IIS)
- HTTPS support (recommended)
- Sufficient storage for hosting update packages

#### Directory Structure

```
/updates/
├── version.json          # Version information
├── android/
│   └── app-1.0.1.apk
├── ios/
│   └── app-1.0.1.ipa
└── desktop/
    ├── win-x64/
    │   └── app-1.0.1.zip
    ├── osx-x64/
    │   └── app-1.0.1.zip
    └── linux-x64/
        └── app-1.0.1.zip
```

#### Nginx Configuration Example

```nginx
server {
    listen 443 ssl;
    server_name updates.yourcompany.com;

    ssl_certificate /path/to/certificate.crt;
    ssl_certificate_key /path/to/private.key;

    root /var/www/updates;

    location / {
        add_header Access-Control-Allow-Origin *;
        add_header Access-Control-Allow-Methods "GET, OPTIONS";
        autoindex on;
    }

    location /version.json {
        add_header Content-Type application/json;
        add_header Cache-Control "no-cache, no-store, must-revalidate";
    }
}
```

### CI/CD Integration

#### GitHub Actions Example

```yaml
name: Build and Deploy

on:
  push:
    tags:
      - 'v*'

jobs:
  build-android:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v2
      - name: Setup .NET
        uses: actions/setup-dotnet@v1
        with:
          dotnet-version: '10.0.x'
      - name: Build Android
        run: |
          dotnet workload install android
          dotnet publish -c Release -f net10.0-android
      - name: Upload APK
        uses: actions/upload-artifact@v2
        with:
          name: android-apk
          path: '**/*.apk'

  build-ios:
    runs-on: macos-latest
    steps:
      - uses: actions/checkout@v2
      - name: Setup .NET
        uses: actions/setup-dotnet@v1
        with:
          dotnet-version: '10.0.x'
      - name: Build iOS
        run: |
          dotnet workload install ios
          dotnet publish -c Release -f net10.0-ios
      - name: Upload IPA
        uses: actions/upload-artifact@v2
        with:
          name: ios-ipa
          path: '**/*.ipa'
```
