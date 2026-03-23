using Android.App;
using GeneralUpdate.Avalonia.Android.Abstractions;

namespace GeneralUpdate.Avalonia.Android.Services;

public sealed class NullAndroidActivityProvider : IAndroidActivityProvider
{
    public Activity? GetCurrentActivity() => null;
}
