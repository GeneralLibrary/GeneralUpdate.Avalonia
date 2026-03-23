using GeneralUpdate.Avalonia.Android.Abstractions;

namespace GeneralUpdate.Avalonia.Android.Services;

public sealed class ImmediateEventDispatcher : IUpdateEventDispatcher
{
    public void Dispatch(Action callback) => callback();
}
