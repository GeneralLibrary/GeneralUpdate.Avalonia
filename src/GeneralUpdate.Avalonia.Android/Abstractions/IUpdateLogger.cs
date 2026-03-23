namespace GeneralUpdate.Avalonia.Android.Abstractions;

public interface IUpdateLogger
{
    void LogDebug(string message);
    void LogInformation(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? exception = null);
}
