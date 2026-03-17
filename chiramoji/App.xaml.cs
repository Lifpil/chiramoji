using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Chiramoji
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private const string AutoStartRegistryName = "chiramoji";
        private const string SingleInstanceMutexName = @"Local\chiramoji_SingleInstance";
        private static readonly object CrashLogLock = new();
        private Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            _singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, createdNew: out var createdNew);
            if (!createdNew)
            {
                MessageBox.Show("アプリはすでに起動しています。", "ちらもじ", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            var settingsService = new Services.SettingsService();
            var settings = settingsService.Load();
            ApplyAutoStartSetting(settings.StartWithWindows);
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            DispatcherUnhandledException -= App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;

            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            base.OnExit(e);
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            WriteCrashLog("DispatcherUnhandledException", e.Exception);
            e.Handled = true;
            MessageBox.Show("予期しないエラーが発生しました。ログを保存しました。", "ちらもじ", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            WriteCrashLog("AppDomainUnhandledException", e.ExceptionObject as Exception);
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            WriteCrashLog("UnobservedTaskException", e.Exception);
            e.SetObserved();
        }

        private static void WriteCrashLog(string source, Exception? exception)
        {
            try
            {
                lock (CrashLogLock)
                {
                    var baseDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "chiramoji",
                        "logs");
                    Directory.CreateDirectory(baseDir);
                    var path = Path.Combine(baseDir, "crash.log");
                    var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
                    File.AppendAllText(path, text);
                }
            }
            catch
            {
                // Last-resort logger: ignore logging failures.
            }
        }

        public static void ApplyAutoStartSetting(bool enabled)
        {
            try
            {
                using var runKey = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run",
                    writable: true);

                if (runKey == null)
                {
                    return;
                }

                if (!enabled)
                {
                    runKey.DeleteValue(AutoStartRegistryName, false);
                    return;
                }

                var startupCommand = BuildStartupCommand();
                if (string.IsNullOrWhiteSpace(startupCommand))
                {
                    return;
                }
                
                var current = runKey.GetValue(AutoStartRegistryName) as string;
                if (!string.Equals(current, startupCommand, StringComparison.Ordinal))
                {
                    runKey.SetValue(AutoStartRegistryName, startupCommand, RegistryValueKind.String);
                }
            }
            catch
            {
                // Do not block app launch even if auto-start registration fails.
            }
        }

        private static string BuildStartupCommand()
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return string.Empty;
            }

            var processFileName = Path.GetFileName(processPath);
            if (string.Equals(processFileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                var dllPath = Path.Combine(AppContext.BaseDirectory, "chiramoji.dll");
                if (File.Exists(dllPath))
                {
                    return $"\"{processPath}\" \"{dllPath}\"";
                }
            }

            return $"\"{processPath}\"";
        }
    }
}
