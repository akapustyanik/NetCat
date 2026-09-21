using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetCat.Core;
namespace NetCat.UI;
public sealed record RunningApp(string Name, string Path, int Pid, bool HasWindow, ImageSource? Icon) { public string PidLabel => "PID " + Pid; }
public sealed record RouteChoice(string Label,RouteTarget Target) { public override string ToString()=>Label; }
public partial class ProcessPickerWindow : Window
{
    private List<RunningApp> rows = [];
    public IReadOnlyList<RunningApp> SelectedApps { get; private set; } = [];
    public Task Initialization { get; private set; } = Task.CompletedTask;
    public RouteTarget Target => (RouteTarget)(Route.SelectedValue ?? RouteTarget.Direct);
    public ProcessPickerWindow(Window owner)
    {
        InitializeComponent(); Owner = owner;
        Route.ItemsSource = new[] { new RouteChoice("Напрямую",RouteTarget.Direct), new RouteChoice("Через VPN",RouteTarget.Vpn), new RouteChoice("Через OpenVPN",RouteTarget.OpenVpn), new RouteChoice("Блокировать",RouteTarget.Block) }; Route.SelectedIndex = 0;
        WindowFrame.Apply(this); Loaded += (_,_) => Initialization=RefreshAsync();
    }
    private async Task RefreshAsync()
    {
        Count.Text = "Читаю приложения…";
        rows = await Task.Run(() =>
        {
            var result = new List<RunningApp>(); var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in Process.GetProcesses().OrderByDescending(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } }))
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName; if (path == null || !known.Add(path)) continue;
                    var name = FileVersionInfo.GetVersionInfo(path).FileDescription; if (string.IsNullOrWhiteSpace(name)) name = System.IO.Path.GetFileNameWithoutExtension(path);
                    result.Add(new(name, path, process.Id, process.MainWindowHandle != IntPtr.Zero, ReadIcon(path)));
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
            }
            return result.OrderByDescending(a => a.HasWindow).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        });
        Filter();
    }
    private void Filter()
    {
        if (Apps == null) return;
        var search = Search.Text.Trim(); var visible = rows.Where(r => (BackgroundApps.IsChecked == true || r.HasWindow) && (search.Length == 0 || (r.Name + " " + r.Path + " " + r.Pid).Contains(search, StringComparison.CurrentCultureIgnoreCase))).ToArray();
        Apps.ItemsSource = visible; Count.Text = $"{visible.Length} приложений";
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) => Filter();
    private void Filter_Click(object sender, RoutedEventArgs e) => Filter();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Apps_DoubleClick(object sender, MouseButtonEventArgs e) { if (Apps.SelectedItem != null) Accept_Click(sender,e); }
    private void Accept_Click(object sender, RoutedEventArgs e) { SelectedApps = Apps.SelectedItems.Cast<RunningApp>().ToArray(); if (SelectedApps.Count > 0) DialogResult = true; }
    private static ImageSource? ReadIcon(string path)
    {
        var info = new ShellInfo(); if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellInfo>(), 0x100) == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
        try { var source = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32,32)); source.Freeze(); return source; } finally { DestroyIcon(info.Icon); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ShellInfo { public IntPtr Icon; public int IconIndex; public uint Attributes; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string DisplayName; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=80)] public string TypeName; }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
