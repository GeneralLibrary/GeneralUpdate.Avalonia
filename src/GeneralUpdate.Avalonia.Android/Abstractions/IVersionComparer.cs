namespace GeneralUpdate.Avalonia.Android.Abstractions;

public interface IVersionComparer
{
    bool TryCompare(string currentVersion, string targetVersion, out int compareResult, out string? errorMessage);
}
