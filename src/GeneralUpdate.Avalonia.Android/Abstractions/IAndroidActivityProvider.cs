using Android.App;

namespace GeneralUpdate.Avalonia.Android.Abstractions;

public interface IAndroidActivityProvider
{
    Activity? GetCurrentActivity();
}
