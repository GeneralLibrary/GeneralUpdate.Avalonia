using Android.Util;
using GeneralUpdate.Avalonia.Android.Abstractions;

namespace GeneralUpdate.Avalonia.Android.Sample.Infrastructure;

internal sealed class AndroidUpdateLogger : IUpdateLogger
{
    private const string Tag = "GeneralUpdate.Sample";

    public void LogDebug(string message) => Log.Debug(Tag, message);

    public void LogInformation(string message) => Log.Info(Tag, message);

    public void LogWarning(string message) => Log.Warn(Tag, message);

    public void LogError(string message, Exception? exception = null)
    {
        Log.Error(Tag, exception is null ? message : $"{message}{Environment.NewLine}{exception}");
    }
}
