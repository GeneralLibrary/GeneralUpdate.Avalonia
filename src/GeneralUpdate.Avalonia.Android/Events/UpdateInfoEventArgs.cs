using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Events;

/// <summary>
/// Carries the update information discovered by version validation.
/// <para>
/// Mirrors <c>GeneralUpdate.Core.Download.UpdateInfoEventArgs</c> (which wraps the server
/// response <c>VersionRespDTO</c>); here the equivalent payload is the
/// <see cref="UpdatePackageInfo"/> that was validated together with its <see cref="UpdateCheckResult"/>.
/// </para>
/// <para>
/// Hosts receive this payload from the <c>AddListenerUpdatePrecheck</c> callback, so business
/// logic (force-update policy, channel selection, UX decisions) can inspect the package metadata
/// before the APK is downloaded.
/// </para>
/// </summary>
public sealed class UpdateInfoEventArgs : EventArgs
{
    public UpdateInfoEventArgs(UpdatePackageInfo packageInfo, string currentVersion, UpdateCheckResult result)
    {
        PackageInfo = packageInfo;
        CurrentVersion = currentVersion;
        Result = result;
    }

    /// <summary>
    /// The package metadata that was validated (the update information to inspect).
    /// </summary>
    public UpdatePackageInfo PackageInfo { get; }

    /// <summary>
    /// The version the device is currently running.
    /// </summary>
    public string CurrentVersion { get; }

    /// <summary>
    /// The validation result, whose <see cref="UpdateCheckResult.UpdateFound"/> is <c>true</c>
    /// and <see cref="UpdateCheckResult.TargetVersion"/> equals <see cref="UpdatePackageInfo.Version"/>.
    /// </summary>
    public UpdateCheckResult Result { get; }

    /// <summary>
    /// Whether the publisher marked this update as forced. Forced updates cannot be skipped
    /// by the pre-check callback (same rule as <c>GeneralUpdate.Core</c>).
    /// </summary>
    public bool IsForced => PackageInfo.IsForced;
}
