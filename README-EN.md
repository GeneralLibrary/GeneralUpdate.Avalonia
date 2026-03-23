# GeneralUpdate.Avalonia

[![GitHub Stars](https://img.shields.io/github/stars/GeneralLibrary/GeneralUpdate.Avalonia?style=flat-square)](https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/stargazers)
[![GitHub Forks](https://img.shields.io/github/forks/GeneralLibrary/GeneralUpdate.Avalonia?style=flat-square)](https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/network/members)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg?style=flat-square)](./LICENSE)
[![NuGet](https://img.shields.io/nuget/v/GeneralUpdate.Avalonia.Android?style=flat-square)](https://www.nuget.org/packages/GeneralUpdate.Avalonia.Android/)

---

## Introduction

`GeneralUpdate.Avalonia` is a repository focused on update capabilities for Avalonia applications. Its current core module, `GeneralUpdate.Avalonia.Android`, provides a UI-free Android auto-update pipeline targeting `net8.0-android` for Avalonia 12+ apps.

The project uses composable abstractions so you can replace version comparison, downloading, hash validation, installer launching, logging, and event dispatching based on your application architecture.

## Core Features

- **UI-free Android update core**: Host applications fully control dialogs, progress, and error presentation.  
- **End-to-end update flow**: validation → resumable download → SHA-256 verification → installer launch.  
- **Extensible architecture**: `IVersionComparer`, `IUpdateDownloader`, `IHashValidator`, `IApkInstaller`, and more are replaceable.  
- **Resumable downloading**: sidecar metadata + streaming writes for better reliability on unstable networks.  
- **Unified event model**: built-in validation, progress, completion, and failure events for UI/log integration.  

## Quick Start

### Prerequisites

- .NET SDK: `8.0+`
- Platform: `Android (net8.0-android)`
- Avalonia: `12+`
- Git: `2.30+`

### Installation

1. Clone the repository

```bash
git clone https://github.com/GeneralLibrary/GeneralUpdate.Avalonia.git
cd GeneralUpdate.Avalonia
```

2. Install dependencies (NuGet package consumption)

```bash
dotnet add package GeneralUpdate.Avalonia.Android
```

3. Build and test locally (repository development)

```bash
dotnet test tests/GeneralUpdate.Avalonia.Android.Tests/GeneralUpdate.Avalonia.Android.Tests.csproj
```

### Basic Usage

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

## Directory Structure

```text
GeneralUpdate.Avalonia/
├── src/
│   └── GeneralUpdate.Avalonia.Android/   # Android auto-update core library
├── tests/
│   └── GeneralUpdate.Avalonia.Android.Tests/ # Unit tests
├── README.md
├── README-EN.md
└── LICENSE
```

## Contributing

Contributions are welcome through the standard GitHub workflow:

1. Fork this repository and create a branch from `main`: `feature/{{short-description}}`.  
2. Keep changes focused and follow existing style and naming conventions.  
3. Run the existing tests before submitting:
   ```bash
   dotnet test tests/GeneralUpdate.Avalonia.Android.Tests/GeneralUpdate.Avalonia.Android.Tests.csproj
   ```
4. Open a Pull Request describing motivation, implementation details, and compatibility impact.  
5. Iterate based on review feedback, then merge and delete the branch.  

## License

This project is licensed under the **Apache License 2.0**. See [LICENSE](./LICENSE) for details.

## Contact

- Repository: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia
- Issue Tracker: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/issues
- Discussions: https://github.com/GeneralLibrary/GeneralUpdate.Avalonia/discussions
