using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using NetCat.Core.Enums;
using NetCat.Core.Models;
using Application = System.Windows.Application;

namespace NetCat.UI.SystemIntegration
{
    public class TrayIconManager : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly Window _mainWindow;
        private readonly Action _toggleVpnAction;
        private readonly Action<RoutingMode> _changeModeAction;
        private readonly Action _exitAction;

        private Icon? _iconActive;
        private Icon? _iconIdle;
        private Icon? _iconError;

        public TrayIconManager(
            Window mainWindow,
            Action toggleVpnAction,
            Action<RoutingMode> changeModeAction,
            Action exitAction)
        {
            _mainWindow = mainWindow;
            _toggleVpnAction = toggleVpnAction;
            _changeModeAction = changeModeAction;
            _exitAction = exitAction;

            LoadIcons();

            _notifyIcon = new NotifyIcon
            {
                Icon = _iconIdle ?? SystemIcons.Application,
                Text = "NetCat VPN & Zapret",
                Visible = true
            };

            _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();
            RebuildContextMenu(false, null, RoutingMode.RuleBased);
        }

        private void LoadIcons()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string iconDir = Path.Combine(baseDir, "assets", "icons");

                string activePath = Path.Combine(iconDir, "tray_active.ico");
                string idlePath = Path.Combine(iconDir, "tray_idle.ico");
                string errorPath = Path.Combine(iconDir, "tray_error.ico");

                if (File.Exists(activePath)) _iconActive = new Icon(activePath);
                if (File.Exists(idlePath)) _iconIdle = new Icon(idlePath);
                if (File.Exists(errorPath)) _iconError = new Icon(errorPath);
            }
            catch
            {
                // Fallback to system icons if needed
            }
        }

        public void UpdateState(bool isConnected, ProxyProfile? activeProfile, RoutingMode currentMode)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (isConnected)
                {
                    _notifyIcon.Icon = _iconActive ?? SystemIcons.Application;
                    int ping = activeProfile?.LastMetrics.DisplayDelay ?? -1;
                    string pingStr = ping >= 0 ? $"{ping} ms" : "OK";
                    _notifyIcon.Text = $"NetCat: Подключено ({activeProfile?.Name ?? "VPN"}) [{pingStr}]";
                }
                else
                {
                    _notifyIcon.Icon = _iconIdle ?? SystemIcons.Application;
                    _notifyIcon.Text = "NetCat: Отключено";
                }

                RebuildContextMenu(isConnected, activeProfile, currentMode);
            });
        }

        private void RebuildContextMenu(bool isConnected, ProxyProfile? activeProfile, RoutingMode currentMode)
        {
            var menu = new ContextMenuStrip();

            // Status header
            string statusText = isConnected
                ? $"● Активен: {activeProfile?.Name ?? "VPN"} ({activeProfile?.LastMetrics.DisplayDelay ?? 0} ms)"
                : "○ VPN Отключен";
            var statusItem = new ToolStripMenuItem(statusText) { Enabled = false };
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());

            // Connect/Disconnect toggle
            var toggleItem = new ToolStripMenuItem(isConnected ? "Отключить VPN" : "Включить VPN", null, (s, e) => _toggleVpnAction());
            toggleItem.Font = new Font(toggleItem.Font, System.Drawing.FontStyle.Bold);
            menu.Items.Add(toggleItem);
            menu.Items.Add(new ToolStripSeparator());

            // Mode Selector
            var modesMenu = new ToolStripMenuItem("Режим маршрутизации");
            foreach (RoutingMode mode in Enum.GetValues(typeof(RoutingMode)))
            {
                var modeItem = new ToolStripMenuItem(GetModeDisplayName(mode), null, (s, e) => _changeModeAction(mode))
                {
                    Checked = (mode == currentMode)
                };
                modesMenu.DropDownItems.Add(modeItem);
            }
            menu.Items.Add(modesMenu);
            menu.Items.Add(new ToolStripSeparator());

            // Show window & Exit
            menu.Items.Add(new ToolStripMenuItem("Открыть NetCat", null, (s, e) => ShowMainWindow()));
            menu.Items.Add(new ToolStripMenuItem("Выход", null, (s, e) => _exitAction()));

            _notifyIcon.ContextMenuStrip = menu;
        }

        public void ShowMainWindow()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                _mainWindow.Show();
                if (_mainWindow.WindowState == WindowState.Minimized)
                {
                    _mainWindow.WindowState = WindowState.Normal;
                }
                _mainWindow.Activate();
            });
        }

        private static string GetModeDisplayName(RoutingMode mode) => mode switch
        {
            RoutingMode.Global => "Глобальный (весь трафик)",
            RoutingMode.RuleBased => "По правилам (Geosite/GeoIP)",
            RoutingMode.SelectiveVpn => "Выборочный (Только VPN)",
            RoutingMode.SelectiveDirect => "Исключения (Все кроме direct)",
            _ => mode.ToString()
        };

        public void Dispose()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _iconActive?.Dispose();
            _iconIdle?.Dispose();
            _iconError?.Dispose();
        }
    }
}
