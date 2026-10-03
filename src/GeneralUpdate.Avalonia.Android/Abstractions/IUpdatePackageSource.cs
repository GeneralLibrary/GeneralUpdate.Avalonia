using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Abstractions;

public interface IUpdatePackageSource
{
    /// <summary>Returns the newest eligible APK, or null for no package. Transport and metadata errors must not return null.</summary>
    Task<UpdatePackageInfo?> GetLatestAsync(string currentVersion, CancellationToken cancellationToken = default);
}
