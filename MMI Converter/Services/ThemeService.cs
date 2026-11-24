using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MmiConverter.Services;

public static class ThemeService
{
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MmiConverter",
        "theme.txt");

    public static bool IsDark { get; private set; } = true;

    public static void LoadAndApply()
    {
        bool dark = true;
        try
        {
            dark = !File.Exists(PreferencePath) ||
                   !File.ReadAllText(PreferencePath).Trim().Equals("Light", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
        }

        Apply(dark, false);
    }

    public static void Apply(bool dark, bool persist = true)
    {
        IsDark = dark;
        ResourceDictionary resources = Application.Current.Resources;
        SetBrush(resources, "WindowBrush", dark ? "#191C20" : "#F4F6F8");
        SetBrush(resources, "PanelBrush", dark ? "#22262C" : "#FFFFFF");
        SetBrush(resources, "RaisedBrush", dark ? "#2A2F36" : "#E8EDF2");
        SetBrush(resources, "LineBrush", dark ? "#383E46" : "#CDD4DC");
        SetBrush(resources, "QuietButtonBrush", dark ? "#2A2F36" : "#EEF2F7");
        SetBrush(resources, "QuietButtonHoverBrush", dark ? "#323840" : "#E2E8F0");
        SetBrush(resources, "QuietButtonBorderBrush", dark ? "#383E46" : "#D6DEE9");
        SetBrush(resources, "InkBrush", dark ? "#EDF0F3" : "#18212B");
        SetBrush(resources, "MutedBrush", dark ? "#A7AFB9" : "#66717F");
        SetBrush(resources, "AccentBrush", "#4D6F9D");
        SetBrush(resources, "AccentHoverBrush", "#405F88");
        SetBrush(resources, "SuccessBrush", dark ? "#7FB59D" : "#2F7D5B");
        SetBrush(resources, "DangerBrush", dark ? "#F08B94" : "#C94C59");
        SetBrush(resources, "OnAccentBrush", "#FFFFFF");

        foreach (Window window in Application.Current.Windows)
        {
            ApplyWindowChrome(window);
        }

        if (persist)
        {
            try
            {
                string? directory = Path.GetDirectoryName(PreferencePath);
                if (directory is not null)
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(PreferencePath, dark ? "Dark" : "Light");
            }
            catch
            {
            }
        }
    }

    public static void ApplyWindowChrome(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        int captionColor = IsDark ? ColorRef(25, 28, 32) : ColorRef(244, 246, 248);
        DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(int));
        int textColor = IsDark ? ColorRef(237, 240, 243) : ColorRef(24, 33, 43);
        DwmSetWindowAttribute(handle, 36, ref textColor, sizeof(int));
        RefreshTitleBar(handle);
    }

    private static void SetBrush(ResourceDictionary resources, string key, string value)
    {
        resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    }

    private static int ColorRef(byte red, byte green, byte blue)
    {
        return red | green << 8 | blue << 16;
    }

    private static void RefreshTitleBar(IntPtr handle)
    {
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0037);
        if (GetForegroundWindow() == handle)
        {
            SendMessage(handle, 0x0086, IntPtr.Zero, IntPtr.Zero);
            SendMessage(handle, 0x0086, new IntPtr(1), IntPtr.Zero);
        }
        RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, 0x0501);
        DwmFlush();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr updateRectangle, IntPtr updateRegion, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
