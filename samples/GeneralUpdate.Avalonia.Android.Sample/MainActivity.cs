using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace GeneralUpdate.Avalonia.Android.Sample;

[Activity(
    Label = "GeneralUpdate Android Sample",
    Theme = "@style/MyTheme.NoActionBar",
    MainLauncher = true,
    Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity
{
    public static MainActivity? Current { get; private set; }

    public static event EventHandler? Resumed;

    protected override void OnResume()
    {
        base.OnResume();
        Current = this;
        Resumed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPause()
    {
        if (Current == this)
        {
            Current = null;
        }

        base.OnPause();
    }
}
