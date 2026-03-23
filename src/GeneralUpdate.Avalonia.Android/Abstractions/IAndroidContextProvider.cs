using Android.Content;

namespace GeneralUpdate.Avalonia.Android.Abstractions;

public interface IAndroidContextProvider
{
    Context? GetContext();
}
