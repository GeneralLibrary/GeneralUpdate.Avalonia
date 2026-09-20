using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Events;

public sealed class UpdateCoordinatorEventArgs(UpdateCoordinatorResult result) : EventArgs
{
    public UpdateCoordinatorResult Result { get; } = result;
}
