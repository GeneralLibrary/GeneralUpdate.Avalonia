using Android.App;
using GeneralUpdate.Avalonia.Android.Abstractions;

namespace GeneralUpdate.Avalonia.Android.Sample.Infrastructure;

internal sealed class CurrentActivityProvider : IAndroidActivityProvider
{
    public Activity? GetCurrentActivity() => MainActivity.Current;
}
