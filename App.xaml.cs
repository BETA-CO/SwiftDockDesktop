using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SwiftDock
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _mutex;
        private SplashScreen? _splashScreen;

        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);
            };

            DispatcherUnhandledException += (s, args) =>
            {
                LogCrash("DispatcherUnhandledException", args.Exception);
            };

            bool createdNew;
            _mutex = new Mutex(true, "Global\\SwiftDockUniqueMutex-7E3F2A", out createdNew);

            if (!createdNew)
            {
                try
                {
                    using (var showEvent = EventWaitHandle.OpenExisting("Global\\SwiftDockShowEvent-7E3F2A"))
                    {
                        showEvent.Set();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to signal existing instance: " + ex.Message);
                }

                Shutdown();
                return;
            }

            // Show splash screen immediately
            _splashScreen = new SplashScreen();
            _splashScreen.Show();

            // Load main window asynchronously
            Task.Run(() =>
            {
                // Small delay to ensure splash is visible
                Thread.Sleep(100);

                Dispatcher.Invoke(() =>
                {
                    _splashScreen?.UpdateStatus("LOADING CONFIGURATION...");
                });

                // Load main window on UI thread
                Dispatcher.Invoke(() =>
                {
                    var mainWindow = new MainWindow();
                    mainWindow.Loaded += (s, args) =>
                    {
                        // Close splash after main window is fully loaded
                        _splashScreen?.CloseSplash();
                        _splashScreen = null;
                    };

                    MainWindow = mainWindow;
                    mainWindow.Show();
                });
            });

            base.OnStartup(e);
        }

        private static void LogCrash(string source, Exception? ex)
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                string message = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Crash in {source}:\n{ex?.ToString()}\n\n";
                File.AppendAllText(logPath, message);
            }
            catch { }
        }
    }
}
