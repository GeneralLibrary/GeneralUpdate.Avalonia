using System.Globalization;
using GeneralUpdate.Avalonia.Android;

namespace GeneralUpdate.Avalonia.Android.Sample.Infrastructure;

internal static class UpdateViewText
{
    private static readonly IReadOnlyDictionary<string, string> Chinese = new Dictionary<string, string>
    {
        ["Language"] = "语言",
        ["GeneralUpdate mobile updater"] = "GeneralUpdate 移动端自动升级",
        ["Connect to GeneralSpacestation to check versions, download APKs, verify SHA-256, and install."] = "连接 GeneralSpacestation，完成版本校验、APK 下载、SHA-256 校验和安装。",
        ["Current version:"] = "当前版本：",
        ["Target version:"] = "目标版本：",
        ["GeneralSpacestation settings"] = "GeneralSpacestation 配置",
        ["Verification endpoint"] = "验证接口",
        ["AppKey configured in admin console"] = "管理端配置的 AppKey",
        ["Android platform number"] = "Android 平台编号",
        ["Product ID"] = "产品编号",
        ["Release notes"] = "更新说明",
        ["Not checked"] = "尚未检查",
        ["Shown when an update is found"] = "检查到新版本后显示",
        ["Waiting for update check"] = "等待检查更新",
        ["Reconciling previous update..."] = "正在核对上次升级结果...",
        ["Checking server version automatically..."] = "正在自动检查服务端版本...",
        ["Latest version"] = "已是最新版本",
        ["The server did not provide release notes."] = "服务端未提供更新说明。",
        ["New version found. Select “Check and update” to download and install."] = "发现新版本，点击“检查并自动升级”开始下载和安装。",
        ["The app is up to date."] = "当前已经是最新版本。",
        ["Checking and preparing update..."] = "正在检查并准备升级包...",
        ["Update record reset. The installed app was not changed. You can check for updates again."] = "升级记录已重置，未修改已安装应用。可以重新检查升级。",
        ["Update operation canceled."] = "更新操作已取消。",
        ["Update operation failed: {0}"] = "更新操作失败：{0}",
        ["System installer opened. Confirm installation; the app will check the result when reopened."] = "系统安装器已打开，请确认安装。重新打开应用后会核对升级结果。",
        ["Allow installation from unknown sources, then return to retry."] = "请开启“允许安装未知应用”，返回后会重试安装。",
        ["Update confirmed: target {0}, current {1}."] = "升级已确认：目标 {0}，当前 {1}。",
        ["Update not confirmed: target {0}, current {1}. You can check and retry."] = "升级尚未确认：目标 {0}，当前 {1}。可以重新检查并重试。",
        ["No update record."] = "暂无升级记录。",
        ["If the record is corrupt, explicitly reset the update record."] = "如记录损坏，可显式重置升级记录。",
        ["Enter a valid HTTP or HTTPS verification URL."] = "请输入有效的 HTTP 或 HTTPS 验证接口地址。",
        ["Platform must be a positive integer configured on the server."] = "Platform 必须是服务端配置的正整数平台编号。",
        ["Enter a ProductId."] = "请输入 ProductId。",
        ["Update failed ({0}): {1}"] = "更新失败：{0}。{1}",
        ["Update error: {0}. If the record is corrupt, explicitly reset the update record."] = "更新错误：{0}。如记录损坏，可显式重置升级记录。",
        ["Check and update"] = "检查并自动升级",
        ["Cancel"] = "取消",
        ["Reset update record (does not modify installed app)"] = "重置升级记录（不修改已安装应用）",
        ["Unable to read installed app version."] = "无法读取本机应用版本。",
        ["The installed app does not provide a version, so update confirmation is unavailable."] = "本机应用未提供版本号，不能确认升级结果。",
        ["Unable to read app package name."] = "无法读取应用包名。",
        ["Unable to open the unknown sources installation permission page."] = "无法打开未知来源安装授权页面。",
        ["Unable to read update server settings."] = "无法读取更新服务配置。",
        ["Saved platform number is invalid. Re-enter and save it."] = "保存的平台编号无效，请重新填写并保存。",
        ["Unable to save update server settings."] = "无法保存更新服务配置。",
        ["Saving update server settings failed; update was not started."] = "保存更新服务配置失败，未开始更新。",
        ["Unable to save language preference."] = "无法保存语言设置。"
    };

    public static string Get(UpdateLanguage language, string key) =>
        language == UpdateLanguage.Chinese && Chinese.TryGetValue(key, out var translated)
            ? translated
            : key;

    public static string Format(UpdateLanguage language, string format, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(language, format), arguments);
}
