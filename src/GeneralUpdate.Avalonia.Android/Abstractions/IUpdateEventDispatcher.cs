namespace GeneralUpdate.Avalonia.Android.Abstractions;

public interface IUpdateEventDispatcher
{
    void Dispatch(Action callback);
}
