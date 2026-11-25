using System.Runtime.InteropServices;

namespace AchievementSync.Desktop.Services;

internal static class WindowAppearance
{
    private const int UseImmersiveDarkMode = 20;

    public static void Apply(IntPtr windowHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == IntPtr.Zero) return;

        // Only apply immersive dark mode — this is safe on Windows 10 and 11 and does not
        // affect the window chrome (title bar, borders, drag-to-move).
        TrySet(windowHandle, UseImmersiveDarkMode, 1);
    }

    private static void TrySet(IntPtr windowHandle, int attribute, int value)
    {
        try
        {
            _ = DwmSetWindowAttribute(windowHandle, attribute, ref value, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
