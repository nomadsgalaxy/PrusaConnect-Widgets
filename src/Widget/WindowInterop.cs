using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using PrusaConnect.Widget.Diagnostics;
using Windows.Graphics;

namespace PrusaConnect.Widget;

/// <summary>
/// Forces the settings window onto the primary monitor at a sane size and to
/// the foreground. WinUI 3's Window.Activate() doesn't reliably position, size,
/// focus, or front it - a fresh launch can open off-screen, zero-size, or behind
/// other windows, which all read as "the app didn't open".
/// </summary>
internal static class WindowInterop
{
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    public static void CenterAndActivate(Window window)
    {
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);

            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow is not null)
            {
                var work = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
                int w = Math.Min(900, work.Width);
                int h = Math.Min(1040, work.Height);
                int x = work.X + Math.Max(0, (work.Width - w) / 2);
                int y = work.Y + Math.Max(0, (work.Height - h) / 2);
                appWindow.MoveAndResize(new RectInt32(x, y, w, h));
                Log.Write($"Settings window placed at {x},{y} {w}x{h} (work {work.X},{work.Y} {work.Width}x{work.Height})");
            }

            ShowWindow(hwnd, IsIconic(hwnd) ? SW_RESTORE : SW_SHOW);
            SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            Log.Error("CenterAndActivate failed", ex);
        }
    }
}
