using System;
using System.Diagnostics;
using System.IO;

namespace PrusaConnect.Widget.Diagnostics;

/// <summary>
/// File logger for diagnosing a headless COM-activated process with no console.
/// Writes to %TEMP%\prusa-widget.log - temp is always writable regardless of
/// packaging, and easy to find from a shell.
/// </summary>
internal static class Log
{
    private static readonly object _gate = new();
    private static readonly string _path;

    static Log()
    {
        string dir = Path.GetTempPath();
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "prusa-widget.log");
    }

    public static string LogPath => _path;

    public static void Write(string message)
    {
        try
        {
            string line = string.Format(
                "{0:yyyy-MM-dd HH:mm:ss.fff} [pid {1} t{2}] {3}{4}",
                DateTime.Now,
                Environment.ProcessId,
                Environment.CurrentManagedThreadId,
                message,
                Environment.NewLine);
            lock (_gate)
            {
                File.AppendAllText(_path, line);
            }
        }
        catch (Exception ex)
        {
            // Last-ditch: at least write something to the Debug stream.
            Debug.WriteLine($"Log.Write failed: {ex.Message}");
        }
    }

    public static void Error(string message, Exception ex)
    {
        Write($"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
    }
}
