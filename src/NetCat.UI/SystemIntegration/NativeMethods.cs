using System;
using System.Runtime.InteropServices;

namespace NetCat.UI.SystemIntegration
{
    public static class NativeMethods
    {
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        public enum BackdropType
        {
            Auto = 0,
            None = 1,
            MainWindow = 2, // Mica
            TransientWindow = 3, // Acrylic
            TabbedWindow = 4 // Tabbed
        }

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HTCAPTION = 0x2;

        public static void EnableImmersiveDarkMode(IntPtr hwnd, bool enable = true)
        {
            if (Environment.OSVersion.Version.Build >= 17763)
            {
                int darkMode = enable ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
            }
        }

        public static void EnableMicaBackdrop(IntPtr hwnd)
        {
            if (Environment.OSVersion.Version.Build >= 22000)
            {
                int backdrop = (int)BackdropType.MainWindow;
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            }
        }
    }
}
