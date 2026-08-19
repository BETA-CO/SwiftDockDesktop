using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace SwiftDock
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _mutex;

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
