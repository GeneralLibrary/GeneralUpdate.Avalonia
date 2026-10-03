using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Abstractions;

public interface IInstallationStore
{
    Task<InstallationRecord?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(InstallationRecord record, CancellationToken cancellationToken = default);
    /// <summary>Explicitly forgets the last attempt, including a corrupt record. Does not uninstall or modify the app.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
