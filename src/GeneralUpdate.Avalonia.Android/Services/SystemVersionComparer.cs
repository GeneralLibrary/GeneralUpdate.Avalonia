using GeneralUpdate.Avalonia.Android.Abstractions;

namespace GeneralUpdate.Avalonia.Android.Services;

public sealed class SystemVersionComparer : IVersionComparer
{
    public bool TryCompare(string currentVersion, string targetVersion, out int compareResult, out string? errorMessage)
    {
        compareResult = 0;
        errorMessage = null;

        if (!Version.TryParse(currentVersion, out var current))
        {
            errorMessage = $"Current version '{currentVersion}' is not a valid System.Version string.";
            return false;
        }

        if (!Version.TryParse(targetVersion, out var target))
        {
            errorMessage = $"Target version '{targetVersion}' is not a valid System.Version string.";
            return false;
        }

        compareResult = target.CompareTo(current);
        return true;
    }
}
