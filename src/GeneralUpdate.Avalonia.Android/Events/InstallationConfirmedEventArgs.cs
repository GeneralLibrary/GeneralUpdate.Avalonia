using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Events;

public sealed class InstallationConfirmedEventArgs(InstallationCheckResult result) : EventArgs
{
    public InstallationCheckResult Result { get; } = result;
}
