using Android.App;
using Android.Content;
using GeneralUpdate.Avalonia.Android.Abstractions;

namespace GeneralUpdate.Avalonia.Android.Services;

public sealed class DefaultAndroidContextProvider : IAndroidContextProvider
{
    public Context? GetContext() => Application.Context;
}
