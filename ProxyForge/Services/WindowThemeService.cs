using ProxyForge.Interfaces;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ProxyForge.Services;

internal sealed class WindowThemeService : IWindowThemeService
{
    public void Bind(Window window)
    {
        if (window is null) return;
        window.SourceInitialized += (_, _) => ApplyCurrentTheme(window);
        window.Loaded += (_, _) => ApplyCurrentTheme(window);
    }

    private static void ApplyCurrentTheme(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero) return;

        var captionBrush = (window.TryFindResource(SystemColors.ControlBrushKey) as SolidColorBrush) ?? Brushes.White;
        var textBrush = (window.TryFindResource(SystemColors.WindowTextBrushKey) as SolidColorBrush) ?? Brushes.Black;

        SetDwmColor(hwnd, NativeMethods.DwmwaCaption, captionBrush.Color);
        SetDwmColor(hwnd, NativeMethods.DwmwaBorder, captionBrush.Color);
        SetDwmColor(hwnd, NativeMethods.DwmwaText, textBrush.Color);
    }

    private static void SetDwmColor(nint hwnd, uint attribute, Color color)
    {
        uint colorRef = color.R | ((uint)color.G << 8) | ((uint)color.B << 16);
        NativeMethods.DwmSetWindowAttribute(hwnd, attribute, ref colorRef, sizeof(uint));
    }

    private static class NativeMethods
    {
        internal const uint DwmwaCaption = 35;
        internal const uint DwmwaBorder = 34;
        internal const uint DwmwaText = 36;

        [DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, ref uint pvAttribute, uint cbAttribute);
    }
}