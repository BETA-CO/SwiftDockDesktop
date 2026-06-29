using System;
using System.Threading;
using System.Windows;

namespace SwiftDock
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private static Mutex? _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
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

                // Shutdown this second instance immediately
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }
    }
}
