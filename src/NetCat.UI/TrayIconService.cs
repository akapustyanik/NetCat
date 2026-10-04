using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using NetCat.Core;

namespace NetCat.UI;

public sealed class TrayIconService : IDisposable
{
    private NotifyIcon data;
    private readonly HwndSource source;
    private readonly Action openWindow;
    private readonly Action exitApp;
    private readonly Func<IEnumerable<TrayCommand>> getCommands;
    private ContextMenu? menu;
    private bool suppressDoubleClickRelease;
    private readonly IntPtr ownedIcon;
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly Action<string>? diagnostic;
    private readonly Action<bool>? availabilityChanged;
    private readonly Func<uint, bool> notifyIcon;
    private readonly DispatcherTimer retryTimer;
    private readonly TimeSpan[] retryDelays;
    private int retryIndex;
    private bool disposed;
    public bool TrayInitialized { get; private set; }
    public bool TrayVisible { get; private set; }

    public TrayIconService(Action openWindow, Action exitApp, Func<IEnumerable<TrayCommand>> getCommands,
        Action<string>? diagnostic = null, Action<bool>? availabilityChanged = null,
        Func<uint, bool>? notifyIcon = null, TimeSpan[]? retryDelays = null)
    {
        this.openWindow = openWindow;
        this.exitApp = exitApp;
        this.getCommands = getCommands;
        this.diagnostic = diagnostic;
        this.availabilityChanged = availabilityChanged;
        this.notifyIcon = notifyIcon ?? (message => Shell_NotifyIcon(message, ref data));
        this.retryDelays = retryDelays?.ToArray() ??
            [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500),
             TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];
        if (this.retryDelays.Length > 8 || this.retryDelays.Any(delay => delay <= TimeSpan.Zero))
            throw new ArgumentOutOfRangeException(nameof(retryDelays));
        retryTimer = new DispatcherTimer(DispatcherPriority.Background);
        retryTimer.Tick += RetryRegistration;
        diagnostic?.Invoke("TRAY state=creating");

        var parameters = new HwndSourceParameters("NetCatTrayListener")
        {
            WindowStyle = 0,
            Width = 0,
            Height = 0
        };
        source = new HwndSource(parameters);
        source.AddHook(Hook);
        var handle = source.Handle;
        // Autostart runs elevated; accept only this shell broadcast across UIPI.
        // This is an unowned top-level HWND, so it survives lazy MainWindow creation.
        if (taskbarCreated != 0 && !ChangeWindowMessageFilterEx(handle, taskbarCreated, 1, IntPtr.Zero))
            diagnostic?.Invoke("TRAY taskbar-message-filter=failed");

        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/NetCat;component/Assets/NetCat.ico"))
                   ?? Application.GetResourceStream(new Uri("pack://application:,,,/Assets/NetCat.ico"));
            if (res != null)
            {
                using var iconStream = res.Stream;
                using var iconBuffer = new MemoryStream();
                iconStream.CopyTo(iconBuffer);
                var iconBytes = iconBuffer.ToArray();
                var iconImage = IconDirectory.Image(iconBytes);
                ownedIcon = CreateIconFromResourceEx(iconImage, (uint)iconImage.Length, true, 0x30000, 32, 32, 0);
            }
        }
        catch { }

        data = new NotifyIcon
        {
            Size = Marshal.SizeOf<NotifyIcon>(),
            Window = handle,
            Id = 1,
            Flags = 1 | 2 | 4,
            Callback = 0x8001,
            Icon = ownedIcon != IntPtr.Zero ? ownedIcon : LoadIcon(IntPtr.Zero, (IntPtr)32512),
            Tip = "NetCat — открыть щелчком",
            Info = "",
            InfoTitle = ""
        };
        try
        {
            TrayInitialized = true;
            RegisterIcon();
        }
        catch
        {
            retryTimer.Stop();
            retryTimer.Tick -= RetryRegistration;
            source.RemoveHook(Hook);
            source.Dispose();
            if (ownedIcon != IntPtr.Zero) DestroyIcon(ownedIcon);
            throw;
        }
    }

    private void RegisterIcon()
    {
        if (disposed) return;
        source.Dispatcher.VerifyAccess();
        retryTimer.Stop();
        // MODIFY handles duplicate TaskbarCreated broadcasts without adding another icon.
        // After Explorer has actually restarted it fails, and ADD restores the same HWND/ID.
        var registered = notifyIcon(1) || notifyIcon(0);
        var changed = TrayVisible != registered;
        TrayVisible = registered;
        diagnostic?.Invoke(registered ? "TRAY state=visible" :
            $"TRAY state=unavailable reason=shell-notify-icon attempt={retryIndex + 1}");
        if (changed) availabilityChanged?.Invoke(registered);
        if (registered) { retryIndex = 0; return; }
        if (retryIndex < retryDelays.Length)
        {
            retryTimer.Interval = retryDelays[retryIndex++];
            retryTimer.Start();
        }
        else diagnostic?.Invoke("TRAY retries=exhausted awaiting=TaskbarCreated");
    }

    private void RetryRegistration(object? sender, EventArgs e) => RegisterIcon();

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (taskbarCreated != 0 && (uint)message == taskbarCreated)
        {
            if (!disposed)
            {
                diagnostic?.Invoke("TRAY event=TaskbarCreated");
                retryTimer.Stop();
                retryIndex = 0;
                RegisterIcon();
            }
            handled = true;
            return IntPtr.Zero;
        }
        if (message != 0x8001) return IntPtr.Zero;
        if ((int)lParam == 0x201) // WM_LBUTTONDOWN: a new click sequence
        {
            suppressDoubleClickRelease = false;
        }
        else if ((int)lParam == 0x203) // WM_LBUTTONDBLCLK
        {
            // The first WM_LBUTTONUP already opened the window. The double-click
            // replaces the second DOWN; ignore its following UP as well.
            suppressDoubleClickRelease = true;
        }
        else if ((int)lParam == 0x202) // WM_LBUTTONUP
        {
            if (!suppressDoubleClickRelease) openWindow();
            suppressDoubleClickRelease = false;
        }
        else if ((int)lParam == 0x205) // WM_RBUTTONUP
        {
            SetForegroundWindow(hwnd);
            menu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
            menu = CreateMenu(openWindow, exitApp, getCommands());
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }
        handled = true;
        return IntPtr.Zero;
    }

    public static ContextMenu CreateMenu(Action show, Action exit, IEnumerable<TrayCommand> commands)
    {
        var menu = new ContextMenu();
        menu.SetResourceReference(FrameworkElement.StyleProperty, "TrayMenu");
        void Add(string label, Action action, bool? state = null, bool enabled = true)
        {
            var item = new MenuItem { Header = label, IsCheckable = state.HasValue, IsChecked = state == true, IsEnabled = enabled };
            item.SetResourceReference(FrameworkElement.StyleProperty, "TrayMenuItem");
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Открыть NetCat", show);
        menu.Items.Add(new Separator());
        foreach (var command in commands) Add(command.Label, command.Toggle, command.Checked, command.Enabled);
        menu.Items.Add(new Separator());
        Add("Полностью выйти", exit);
        return menu;
    }

    public void Dispose()
    {
        if (disposed) return;
        source.Dispatcher.VerifyAccess();
        disposed = true;
        retryTimer.Stop();
        retryTimer.Tick -= RetryRegistration;
        if (menu != null) menu.IsOpen = false;
        notifyIcon(2);
        TrayVisible = false;
        diagnostic?.Invoke("TRAY state=disposed reason=shutdown");
        source.RemoveHook(Hook);
        source.Dispose();
        if (ownedIcon != IntPtr.Zero) DestroyIcon(ownedIcon);
    }

    [DllImport("user32.dll")] private static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint size, bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIcon
    {
        public int Size; public IntPtr Window; public uint Id, Flags, Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIcon data);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr id);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr changeInfo);
}
