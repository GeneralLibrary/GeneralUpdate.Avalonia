using Avalonia.Threading;
using GeneralUpdate.Avalonia.Android.Abstractions;

namespace GeneralUpdate.Avalonia.Android.Sample.Infrastructure;

internal sealed class AvaloniaUpdateEventDispatcher : IUpdateEventDispatcher
{
    public void Dispatch(Action callback)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            callback();
            return;
        }

        Dispatcher.UIThread.Post(callback);
    }
}
