using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace NetCat.UI;
public sealed class TrayIntegration : IDisposable
{
    private NotifyIcon data;
    private readonly HwndSource source;
    private readonly Window window;
    private readonly Action exit;
    private readonly Func<IEnumerable<TrayCommand>> commands;
    private ContextMenu? menu;
    private readonly IntPtr ownedIcon;
    public TrayIntegration(Window window, Action exit, Func<IEnumerable<TrayCommand>> commands)
    {
        this.window = window; this.exit = exit; this.commands = commands;
        var handle = new WindowInteropHelper(window).Handle; source = HwndSource.FromHwnd(handle); source.AddHook(Hook);
        using var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/NetCat.ico")).Stream;
        using var iconBuffer = new MemoryStream(); iconStream.CopyTo(iconBuffer); var iconBytes=iconBuffer.ToArray();
        var iconImage=NetCat.Core.IconDirectory.Image(iconBytes);
        ownedIcon=CreateIconFromResourceEx(iconImage,(uint)iconImage.Length,true,0x30000,32,32,0);
        data = new NotifyIcon { Size = Marshal.SizeOf<NotifyIcon>(), Window = handle, Id = 1, Flags = 1 | 2 | 4, Callback = 0x8001, Icon = ownedIcon != IntPtr.Zero ? ownedIcon : LoadIcon(IntPtr.Zero, (IntPtr)32512), Tip = "NetCat — открыть двойным щелчком", Info = "", InfoTitle = "" };
        Shell_NotifyIcon(0, ref data);
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x8001) return IntPtr.Zero;
        if ((int)lParam == 0x203) Show();
        if ((int)lParam == 0x205)
        {
            SetForegroundWindow(hwnd); menu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
            menu = CreateMenu(Show, exit, commands()); menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.PlacementTarget = window; menu.IsOpen = true;
        }
        handled = true; return IntPtr.Zero;
    }
    public static ContextMenu CreateMenu(Action show, Action exit, IEnumerable<TrayCommand> commands)
    {
        var menu = new ContextMenu(); menu.SetResourceReference(FrameworkElement.StyleProperty,"TrayMenu");
        void Add(string label, Action action, bool? state = null, bool enabled = true)
        {
            var item = new MenuItem { Header=label, IsCheckable=state.HasValue, IsChecked=state==true, IsEnabled=enabled };
            item.SetResourceReference(FrameworkElement.StyleProperty,"TrayMenuItem"); item.Click += (_,_) => action(); menu.Items.Add(item);
        }
        Add("Открыть NetCat",show); menu.Items.Add(new Separator());
        foreach(var command in commands) Add(command.Label,command.Toggle,command.Checked,command.Enabled);
        menu.Items.Add(new Separator()); Add("Полностью выйти",exit); return menu;
    }
    private void Show() { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); }
    public void Dispose() { if(menu!=null) menu.IsOpen=false; Shell_NotifyIcon(2, ref data); source.RemoveHook(Hook); if(ownedIcon!=IntPtr.Zero) DestroyIcon(ownedIcon); }
    [DllImport("user32.dll")] private static extern IntPtr CreateIconFromResourceEx(byte[] bits,uint size,bool icon,uint version,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIcon
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
}

public sealed record TrayCommand(string Label, bool Checked, bool Enabled, Action Toggle);
