using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;

namespace Chiramoji.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private TaskbarIcon? _trayIcon;
        private bool _isExitRequested;
        private ViewModels.MainViewModel? _viewModel;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            StateChanged += MainWindow_StateChanged;
            Closing += MainWindow_Closing;
        }

        private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (DataContext is ViewModels.MainViewModel vm && sender is TextBox tb)
            {
                if (!vm.IsSyncingFromMonitor)
                {
                    vm.SyncTextFromUi(tb.Text, tb.SelectionStart);
                }
            }
        }

        private void InputTextBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.MainViewModel vm && sender is TextBox tb)
            {
                if (!vm.IsSyncingFromMonitor)
                {
                    vm.SyncTextFromUi(tb.Text, tb.SelectionStart);
                }
            }
        }


        private void UpdateCheckButton_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsToggle != null)
            {
                SettingsToggle.IsChecked = false;
            }
        }
        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            MinimizeToTray();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (ShouldMinimizeOnClose())
            {
                MinimizeToTray();
                return;
            }

            ExitApplication();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureTrayIcon();
        }


        private void HookViewModelEvents()
        {
            UnhookViewModelEvents();
            if (DataContext is ViewModels.MainViewModel vm)
            {
                _viewModel = vm;
                _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            }
        }

        private void UnhookViewModelEvents()
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                _viewModel = null;
            }
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not ViewModels.MainViewModel vm)
            {
                return;
            }

            if (e.PropertyName == nameof(ViewModels.MainViewModel.IsUpdateDialogOpen) && vm.IsUpdateDialogOpen)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (SettingsToggle != null)
                    {
                        SettingsToggle.IsChecked = false;
                    }
                });
            }
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                MinimizeToTray();
            }
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (_isExitRequested)
            {
                CleanupTrayIcon();
                return;
            }

            if (!ShouldMinimizeOnClose())
            {
                CleanupTrayIcon();
                return;
            }

            e.Cancel = true;
            MinimizeToTray();
        }

        private void EnsureTrayIcon()
        {
            if (_trayIcon != null)
            {
                return;
            }

            var contextMenu = new ContextMenu();
            var openItem = new MenuItem { Header = "開く" };
            openItem.Click += (_, _) => RestoreFromTray();

            var exitItem = new MenuItem { Header = "終了" };
            exitItem.Click += (_, _) => ExitApplication();

            contextMenu.Items.Add(openItem);
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(exitItem);

            _trayIcon = new TaskbarIcon
            {
                ToolTipText = "ちらもじ",
                IconSource = CreateTrayIconSource(),
                ContextMenu = contextMenu
            };
            _trayIcon.TrayLeftMouseDown += (_, _) => RestoreFromTray();
        }

        private static ImageSource CreateTrayIconSource()
        {
            try
            {
                var icon = new BitmapImage(new Uri("pack://application:,,,/Assets/ChiraMoji.ico"));
                icon.Freeze();
                return icon;
            }
            catch
            {
                return Imaging.CreateBitmapSourceFromHIcon(
                    System.Drawing.SystemIcons.Application.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(16, 16));
            }
        }

        private void MinimizeToTray()
        {
            EnsureTrayIcon();
            ShowInTaskbar = false;
            Hide();
        }

        private void RestoreFromTray()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = WindowState.Normal;
            Activate();
        }

        private void CleanupTrayIcon()
        {
            if (_trayIcon == null)
            {
                return;
            }

            _trayIcon.Dispose();
            _trayIcon = null;
        }

        public void ExitApplication()
        {
            _isExitRequested = true;
            Close();
            Application.Current.Shutdown();
        }

        private bool ShouldMinimizeOnClose()
        {
            if (DataContext is ViewModels.MainViewModel vm)
            {
                return vm.CloseButtonMinimizesToTray;
            }

            return true;
        }
    }
}








