using Microsoft.UI.Xaml;
using PrusaConnect.Widget.Settings;

namespace PrusaConnect.Widget;

public sealed partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Process-wide handle on the settings window so the OAuth-redirect
    /// activation can route results back into it from the dispatcher thread.
    /// </summary>
    internal static SettingsWindow? SettingsWindow { get; set; }
}
