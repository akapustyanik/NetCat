using System;
using System.Threading;
using System.Windows;
using NetCat.Network.Tun;

namespace NetCat.UI
{
    public partial class App : Application
    {
        private static Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            const string mutexName = "NetCat_SingleInstance_Mutex";
            _singleInstanceMutex = new Mutex(true, mutexName, out bool isNewInstance);

            if (!isNewInstance)
            {
                MessageBox.Show("NetCat уже запущен в системе!", "NetCat", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            Exit += OnAppExit;

            base.OnStartup(e);
        }

        private void OnAppExit(object sender, ExitEventArgs e)
        {
            CleanupSystemState();
        }

        private void OnProcessExit(object? sender, EventArgs e)
        {
            CleanupSystemState();
        }

        private static void CleanupSystemState()
        {
            try
            {
                RouteTableManager.ClearTrackedRoutes();
                RouteTableManager.FlushDns();
                _singleInstanceMutex?.ReleaseMutex();
                _singleInstanceMutex?.Dispose();
            }
            catch { }
        }
    }
}
