using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Events;

public sealed class UpdateCompletedEventArgs : EventArgs
{
    public UpdateCompletedEventArgs(UpdateOperationResult result)
    {
        Result = result;
    }

    public UpdateOperationResult Result { get; }
}
