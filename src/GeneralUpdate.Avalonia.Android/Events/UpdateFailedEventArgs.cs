using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Events;

public sealed class UpdateFailedEventArgs : EventArgs
{
    public UpdateFailedEventArgs(UpdateOperationResult result)
    {
        Result = result;
    }

    public UpdateOperationResult Result { get; }
}
