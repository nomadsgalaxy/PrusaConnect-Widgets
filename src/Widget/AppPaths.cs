using System.IO;
using Windows.Storage;

namespace PrusaConnect.Widget;

/// <summary>
/// Filesystem paths shared by the widget process and settings UI. The package's
/// LocalState survives MSIX upgrades and is only wiped on a clean uninstall -
/// just what we want for printer config + secrets.
/// </summary>
internal static class AppPaths
{
    public static string LocalFolder => ApplicationData.Current.LocalFolder.Path;

    public static string PrintersDirectory => LocalFolder;

    public static string SecretsDirectory => Path.Combine(LocalFolder, "secrets");

    /// <summary>Where the COM server writes the pinned-widget index so the
    /// settings UI (a separate process) can show what's on the board.</summary>
    public static string PinnedWidgetsDirectory => LocalFolder;
}
