# API Reference / API 参考文档

[中文](#中文文档) | [English](#english-documentation)

---

## 中文文档

### UpdateService 类

自动更新服务的核心类。

#### 构造函数

```csharp
public UpdateService()
```

创建 UpdateService 的新实例。

#### 方法

##### Initialize

```csharp
public void Initialize(string updateUrl, string appName, string currentVersion)
```

初始化更新服务。

**参数:**
- `updateUrl` (string): 更新服务器的 URL
- `appName` (string): 应用程序名称
- `currentVersion` (string): 当前应用程序版本

**示例:**
```csharp
_updateService.Initialize(
    updateUrl: "https://your-server.com/updates",
    appName: "MyApp",
    currentVersion: "1.0.0"
);
```

##### CheckForUpdatesAsync

```csharp
public async Task<bool> CheckForUpdatesAsync()
```

异步检查是否有可用的更新。

**返回值:**
- `Task<bool>`: 如果有可用更新返回 `true`，否则返回 `false`

**异常:**
- 如果服务未初始化，会触发 `StatusChanged` 事件

**示例:**
```csharp
bool hasUpdate = await _updateService.CheckForUpdatesAsync();
if (hasUpdate)
{
    Console.WriteLine("有新版本可用！");
}
```

##### DownloadAndInstallAsync

```csharp
public async Task<bool> DownloadAndInstallAsync()
```

异步下载并安装更新。

**返回值:**
- `Task<bool>`: 如果更新成功返回 `true`，否则返回 `false`

**示例:**
```csharp
bool success = await _updateService.DownloadAndInstallAsync();
if (success)
{
    Console.WriteLine("更新成功！");
}
```

#### 事件

##### StatusChanged

```csharp
public event EventHandler<string>? StatusChanged
```

当更新状态改变时触发。

**参数:**
- `sender` (object): 事件源
- `e` (string): 状态消息

**示例:**
```csharp
_updateService.StatusChanged += (sender, status) =>
{
    Console.WriteLine($"状态: {status}");
};
```

##### ProgressChanged

```csharp
public event EventHandler<ProgressEventArgs>? ProgressChanged
```

当下载进度改变时触发。

**参数:**
- `sender` (object): 事件源
- `e` (ProgressEventArgs): 进度信息

**示例:**
```csharp
_updateService.ProgressChanged += (sender, args) =>
{
    Console.WriteLine($"进度: {args.ProgressPercentage}%");
    Console.WriteLine($"当前文件: {args.CurrentFile}");
};
```

### ProgressEventArgs 类

进度事件的参数类。

#### 属性

##### ProgressPercentage

```csharp
public int ProgressPercentage { get; set; }
```

下载进度百分比（0-100）。

##### CurrentFile

```csharp
public string? CurrentFile { get; set; }
```

当前正在处理的文件名。

---

## English Documentation

### UpdateService Class

The core class for auto-update service.

#### Constructor

```csharp
public UpdateService()
```

Creates a new instance of UpdateService.

#### Methods

##### Initialize

```csharp
public void Initialize(string updateUrl, string appName, string currentVersion)
```

Initializes the update service.

**Parameters:**
- `updateUrl` (string): URL of the update server
- `appName` (string): Application name
- `currentVersion` (string): Current application version

**Example:**
```csharp
_updateService.Initialize(
    updateUrl: "https://your-server.com/updates",
    appName: "MyApp",
    currentVersion: "1.0.0"
);
```

##### CheckForUpdatesAsync

```csharp
public async Task<bool> CheckForUpdatesAsync()
```

Asynchronously checks if updates are available.

**Returns:**
- `Task<bool>`: Returns `true` if updates are available, otherwise `false`

**Exceptions:**
- Triggers `StatusChanged` event if service is not initialized

**Example:**
```csharp
bool hasUpdate = await _updateService.CheckForUpdatesAsync();
if (hasUpdate)
{
    Console.WriteLine("New version available!");
}
```

##### DownloadAndInstallAsync

```csharp
public async Task<bool> DownloadAndInstallAsync()
```

Asynchronously downloads and installs updates.

**Returns:**
- `Task<bool>`: Returns `true` if update succeeds, otherwise `false`

**Example:**
```csharp
bool success = await _updateService.DownloadAndInstallAsync();
if (success)
{
    Console.WriteLine("Update successful!");
}
```

#### Events

##### StatusChanged

```csharp
public event EventHandler<string>? StatusChanged
```

Triggered when update status changes.

**Parameters:**
- `sender` (object): Event source
- `e` (string): Status message

**Example:**
```csharp
_updateService.StatusChanged += (sender, status) =>
{
    Console.WriteLine($"Status: {status}");
};
```

##### ProgressChanged

```csharp
public event EventHandler<ProgressEventArgs>? ProgressChanged
```

Triggered when download progress changes.

**Parameters:**
- `sender` (object): Event source
- `e` (ProgressEventArgs): Progress information

**Example:**
```csharp
_updateService.ProgressChanged += (sender, args) =>
{
    Console.WriteLine($"Progress: {args.ProgressPercentage}%");
    Console.WriteLine($"Current file: {args.CurrentFile}");
};
```

### ProgressEventArgs Class

Argument class for progress events.

#### Properties

##### ProgressPercentage

```csharp
public int ProgressPercentage { get; set; }
```

Download progress percentage (0-100).

##### CurrentFile

```csharp
public string? CurrentFile { get; set; }
```

Name of the file currently being processed.

---

## Usage Examples / 使用示例

### Complete Update Workflow / 完整更新流程

```csharp
using GeneralUpdate.Avalonia.Mobile.Services;

// Create service instance / 创建服务实例
var updateService = new UpdateService();

// Subscribe to events / 订阅事件
updateService.StatusChanged += (sender, status) =>
{
    Console.WriteLine($"Status / 状态: {status}");
};

updateService.ProgressChanged += (sender, args) =>
{
    Console.WriteLine($"Progress / 进度: {args.ProgressPercentage}%");
};

// Initialize / 初始化
updateService.Initialize(
    updateUrl: "https://your-server.com/updates",
    appName: "MyApp",
    currentVersion: "1.0.0"
);

// Check for updates / 检查更新
bool hasUpdate = await updateService.CheckForUpdatesAsync();

if (hasUpdate)
{
    // Download and install / 下载并安装
    bool success = await updateService.DownloadAndInstallAsync();
    
    if (success)
    {
        // Restart application / 重启应用
        Console.WriteLine("Please restart the application / 请重启应用");
    }
}
```

### MVVM Integration / MVVM 集成

```csharp
public class MainViewModel : ViewModelBase
{
    private readonly UpdateService _updateService;

    public MainViewModel()
    {
        _updateService = new UpdateService();
        _updateService.StatusChanged += OnStatusChanged;
        _updateService.ProgressChanged += OnProgressChanged;
        
        CheckForUpdatesCommand = new RelayCommand(async () => await CheckForUpdatesAsync());
        DownloadAndInstallCommand = new RelayCommand(async () => await DownloadAndInstallAsync());
    }

    private void OnStatusChanged(object? sender, string status)
    {
        UpdateStatus = status;
    }

    private void OnProgressChanged(object? sender, ProgressEventArgs args)
    {
        DownloadProgress = args.ProgressPercentage;
    }
}
```
