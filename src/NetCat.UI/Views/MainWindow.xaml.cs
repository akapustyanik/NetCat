using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using NetCat.UI.SystemIntegration;
using NetCat.UI.ViewModels;

namespace NetCat.UI.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private TrayIconManager? _trayIconManager;
        private bool _isExplicitExit = false;

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            Loaded += OnWindowLoaded;
            Closing += OnWindowClosing;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.EnableImmersiveDarkMode(hwnd, true);
            NativeMethods.EnableMicaBackdrop(hwnd);

            _trayIconManager = new TrayIconManager(
                this,
                () => _viewModel.ToggleConnectionCommand.Execute(null),
                mode => _viewModel.CurrentRoutingMode = mode,
                () =>
                {
                    _isExplicitExit = true;
                    Close();
                });

            _viewModel.PropertyChanged += (s, ev) =>
            {
                if (ev.PropertyName == nameof(_viewModel.IsConnected) ||
                    ev.PropertyName == nameof(_viewModel.ActiveProfile) ||
                    ev.PropertyName == nameof(_viewModel.CurrentRoutingMode))
                {
                    _trayIconManager?.UpdateState(_viewModel.IsConnected, _viewModel.ActiveProfile, _viewModel.CurrentRoutingMode);
                }
            };
        }

        private void OnWindowClosing(object? sender, CancelEventArgs e)
        {
            if (!_isExplicitExit && _viewModel.Settings.General.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            _trayIconManager?.Dispose();
            _viewModel.Cleanup();
        }

        private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void OnMinimizeClick(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnClearLogsClick(object sender, RoutedEventArgs e)
        {
            _viewModel.LogsText = "";
        }

        private void OnAutoStartToggle(object sender, RoutedEventArgs e)
        {
            if (_viewModel.Settings.General.AutoStartWithWindows)
            {
                TaskSchedulerManager.RegisterAutoStartTask();
            }
            else
            {
                TaskSchedulerManager.UnregisterAutoStartTask();
            }
        }

        private void OnSaveSettingsClick(object sender, RoutedEventArgs e)
        {
            _viewModel.SaveSettingsAndData();
            MessageBox.Show("Настройки сохранены!", "NetCat", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
