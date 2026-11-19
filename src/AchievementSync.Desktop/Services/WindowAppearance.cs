using System.Runtime.InteropServices;

namespace AchievementSync.Desktop.Services;

internal static class WindowAppearance
{
    private const int UseImmersiveDarkMode = 20;
    private const int WindowCornerPreference = 33;
    private const int SystemBackdropType = 38;
    private const int RoundCorners = 2;
    private const int MainWindowBackdrop = 2;

    public static void Apply(IntPtr windowHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == IntPtr.Zero) return;

        TrySet(windowHandle, UseImmersiveDarkMode, 1);
        var build = Environment.OSVersion.Version.Build;
        if (build >= 22000) TrySet(windowHandle, WindowCornerPreference, RoundCorners);
        if (build >= 22621) TrySet(windowHandle, SystemBackdropType, MainWindowBackdrop);
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
