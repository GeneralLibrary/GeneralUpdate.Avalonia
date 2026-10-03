namespace GeneralUpdate.Avalonia.Android.Models;

public sealed record AndroidUpdateOptions
{
    /// <summary>
    /// Update server queried by <see cref="Abstractions.IAndroidBootstrap.ValidateAsync"/>.
    /// When null and no custom IUpdatePackageSource is provided, validation reports
    /// <see cref="UpdateFailureReason.InvalidMetadata"/> because no package can be discovered.
    /// </summary>
    public UpdateServerOptions? UpdateServer { get; init; }

    public string DownloadDirectoryPath { get; init; } = string.Empty;
    /// <summary>
    /// Durable installation journal. CreateDefault uses FilesDir/update/installation.json when empty.
    /// Do not place it in the download cache, which Android may clear during an update.
    /// </summary>
    public string InstallationStateFilePath { get; init; } = string.Empty;
    public string TemporaryFileExtension { get; init; } = ".part";
    public string SidecarExtension { get; init; } = ".json";
    public string FileProviderAuthority { get; init; } = string.Empty;
    /// <summary>Language used for built-in user-facing update messages. Defaults to English.</summary>
    public UpdateLanguage Language { get; init; } = UpdateLanguage.English;
    public int DownloadBufferSize { get; init; } = 64 * 1024;
    public int SpeedSmoothingWindowSeconds { get; init; } = 4;
}

internal static class UpdateMessages
{
    public static string? Get(UpdateLanguage language, string? message)
    {
        if (language != UpdateLanguage.Chinese || message is null)
            return message;

        const string currentVersionPrefix = "Current version '";
        const string targetVersionPrefix = "Target version '";
        const string invalidVersionSuffix = "' is not a valid System.Version string.";
        if (message.StartsWith(currentVersionPrefix, StringComparison.Ordinal) &&
            message.EndsWith(invalidVersionSuffix, StringComparison.Ordinal))
            return $"当前版本“{message[currentVersionPrefix.Length..^invalidVersionSuffix.Length]}”不是有效的 System.Version 版本字符串。";
        if (message.StartsWith(targetVersionPrefix, StringComparison.Ordinal) &&
            message.EndsWith(invalidVersionSuffix, StringComparison.Ordinal))
            return $"目标版本“{message[targetVersionPrefix.Length..^invalidVersionSuffix.Length]}”不是有效的 System.Version 版本字符串。";

        return message switch
        {
            "Checking for updates." => "正在检查更新。",
            "Update check canceled." => "更新检查已取消。",
            "Failed to query the update server." => "查询更新服务器失败。",
            "No update available." => "没有可用更新。",
            "Update available." => "发现可用更新。",
            "Failed to compare versions." => "比较版本失败。",
            "Update skipped by pre-check callback." => "更新已被预检查回调跳过。",
            "Downloading package." => "正在下载更新包。",
            "The downloader returned success without a file path." => "下载器报告成功，但未提供文件路径。",
            "Validating package hash." => "正在校验更新包哈希值。",
            "SHA256 validation failed." => "SHA256 校验失败。",
            "Package downloaded and verified." => "更新包已下载并校验通过。",
            "Package preparation canceled." => "更新包准备已取消。",
            "Package preparation failed." => "更新包准备失败。",
            "Could not persist the installation target. The installer was not launched." => "无法保存安装目标，未启动安装器。",
            "Launching installer." => "正在启动安装器。",
            "Installer launched." => "安装器已启动。",
            "Installer launch canceled." => "启动安装器已取消。",
            "Installer launch did not complete." => "安装器启动未完成。",
            "Invalid installed version." => "已安装版本无效。",
            "Cannot compare installation versions." => "无法比较安装版本。",
            "No installation attempt recorded." => "没有安装记录。",
            "The installed version has reached the recorded update target." => "已安装版本已达到记录的更新目标。",
            "The recorded update target is not installed yet. Installation may be pending or canceled." => "记录的更新目标尚未安装，安装可能仍在等待或已取消。",
            "Could not reconcile the installation record." => "无法核对安装记录。",
            "Installation record cleared. The installed application was not modified." => "安装记录已清除，未修改已安装的应用。",
            "Could not clear the installation record." => "无法清除安装记录。",
            "Package metadata is missing DownloadUrl or Sha256." => "更新包信息缺少 DownloadUrl 或 Sha256。",
            "Download canceled." => "下载已取消。",
            "Download timed out." => "下载超时。",
            "Network error occurred while downloading package." => "下载更新包时发生网络错误。",
            "File I/O error occurred while downloading package." => "下载更新包时发生文件读写错误。",
            "Download finished." => "下载完成。",
            "Expected SHA256 is empty." => "预期 SHA256 值为空。",
            "Downloaded file not found." => "未找到已下载的文件。",
            "SHA256 validation succeeded." => "SHA256 校验成功。",
            "Failed to validate SHA256." => "SHA256 校验失败。",
            "FileProvider authority is not configured." => "未配置 FileProvider authority。",
            "APK file not found." => "未找到 APK 文件。",
            "Android context is unavailable." => "Android Context 不可用。",
            "App is not allowed to request package installs." => "应用没有请求安装软件包的权限。",
            "Installer intent launched." => "安装器 Intent 已启动。",
            "Failed to launch installer intent." => "启动安装器 Intent 失败。",
            "Downloading" => "正在下载",
            "Resuming" => "正在续传",
            "Download completed" => "下载完成",
            _ => message
        };
    }

    public static string FileSizeMismatch(UpdateLanguage language, long expected, long actual) =>
        language == UpdateLanguage.Chinese
            ? $"下载文件大小不匹配。预期 {expected}，实际 {actual}。"
            : $"Downloaded file size mismatch. Expected {expected}, actual {actual}.";
}
