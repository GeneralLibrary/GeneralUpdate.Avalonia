using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Events;

/// <summary>Reports download/verification or installer handoff completion, not successful APK installation.</summary>
public sealed class UpdateCompletedEventArgs : EventArgs
{
    public UpdateCompletedEventArgs(UpdateOperationResult result)
    {
        Result = result;
    }

    public UpdateOperationResult Result { get; }
}
