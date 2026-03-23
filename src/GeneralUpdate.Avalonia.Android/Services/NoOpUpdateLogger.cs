using GeneralUpdate.Avalonia.Android.Abstractions;

namespace GeneralUpdate.Avalonia.Android.Services;

public sealed class NoOpUpdateLogger : IUpdateLogger
{
    public void LogDebug(string message) { }
    public void LogInformation(string message) { }
    public void LogWarning(string message) { }
    public void LogError(string message, Exception? exception = null) { }
}
