using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using Microsoft.Win32;

namespace SwiftDock
{
    public static class ActionExecutor
    {
        // P/Invoke for Keypress Simulation
        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("powrprof.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool LockWorkStation();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        private const byte VK_VOLUME_MUTE = 0xAD;
        private const byte VK_VOLUME_DOWN = 0xAE;
        private const byte VK_VOLUME_UP = 0xAF;
        private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
        private const byte VK_MEDIA_PREV_TRACK = 0xB1;
        private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;

        private const uint KEYEVENTF_KEYUP = 0x0002;

        public static void ExecuteButton(ShortcutButton button)
        {
            Task.Run(() =>
            {
                try
                {
                    ExecuteAction(button.ActionType, button.ActionData);
                    if (button.ActionType.Equals("Macro", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var step in button.MacroSteps)
                        {
                            ExecuteAction(step.Type, step.Data);
                            if (step.DelayMs > 0)
                            {
                                System.Threading.Thread.Sleep(step.DelayMs);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error executing button: {ex.Message}");
                }
            });
        }

        private static void ExecuteAction(string type, string data)
        {
            switch (type.ToLower())
            {
                case "app":
                    LaunchApp(data);
                    break;
                case "url":
                    OpenUrl(data);
                    break;
                case "system":
                    ExecuteSystemAction(data);
                    break;
            }
        }

        private static void LaunchApp(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Direct LaunchApp failed: {ex.Message}. Trying explorer.exe fallback...");
                if (path.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = path,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    Process.Start(psi);
                }
                else
                {
                    throw;
                }
            }
        }

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var parts = url.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var cleanUrl = part.Trim();
                if (string.IsNullOrWhiteSpace(cleanUrl)) continue;

                if (!cleanUrl.StartsWith("http://") && !cleanUrl.StartsWith("https://"))
                {
                    cleanUrl = "https://" + cleanUrl;
                }
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = cleanUrl,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error launching URL {cleanUrl}: {ex.Message}");
                }
            }
        }

        private static void ExecuteSystemAction(string action)
        {
            switch (action.ToLower())
            {
                // Volume
                case "volume_up":
                    SimulateKey(VK_VOLUME_UP);
                    break;
                case "volume_down":
                    SimulateKey(VK_VOLUME_DOWN);
                    break;
                case "volume_mute":
                    SimulateKey(VK_VOLUME_MUTE);
                    break;

                // Media
                case "media_play_pause":
                    SimulateKey(VK_MEDIA_PLAY_PAUSE);
                    break;
                case "media_next":
                    SimulateKey(VK_MEDIA_NEXT_TRACK);
                    break;
                case "media_prev":
                    SimulateKey(VK_MEDIA_PREV_TRACK);
                    break;
                case "media_forward_10":
                    SkipMedia(10);
                    break;
                case "media_backward_10":
                    SkipMedia(-10);
                    break;

                // Brightness
                case "brightness_up":
                    AdjustBrightness(10);
                    break;
                case "brightness_down":
                    AdjustBrightness(-10);
                    break;

                // Audio Capture / Camera Mute
                case "mic_toggle":
                    ToggleMicrophoneMute();
                    break;

                // Power Off / Sleep
                case "pc_shutdown":
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "shutdown",
                        Arguments = "/s /t 0",
                        CreateNoWindow = true,
                        UseShellExecute = true
                    });
                    break;
                case "pc_sleep":
                    SetSuspendState(true, false, false);
                    break;
                case "pc_lock":
                    LockWorkStation();
                    break;
                case "pc_restart":
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "shutdown",
                        Arguments = "/r /t 0",
                        CreateNoWindow = true,
                        UseShellExecute = true
                    });
                    break;
                case "wifi_toggle":
                    Task.Run(async () => await ToggleWifi());
                    break;
                case "bluetooth_toggle":
                    Task.Run(async () => await ToggleBluetooth());
                    break;
                case "screen_record":
                    SimulateKeyCombo(new byte[] { 0x5B, 0x12, 0x52 }); // Win + Alt + R
                    break;
                case "screenshot":
                    SimulateKeyCombo(new byte[] { 0x5B, 0x2C }); // Win + PrintScreen
                    break;
                case "home_screen":
                    SimulateKeyCombo(new byte[] { 0x5B, 0x44 }); // Win + D
                    break;
                case "close_all_apps":
                    CloseAllApplications();
                    break;
            }
        }

        private static void SimulateKey(byte key)
        {
            keybd_event(key, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private static string GetActiveWindowTitle()
        {
            const int nChars = 256;
            System.Text.StringBuilder buff = new System.Text.StringBuilder(nChars);
            IntPtr handle = GetForegroundWindow();

            if (GetWindowText(handle, buff, nChars) > 0)
            {
                return buff.ToString().ToLower();
            }
            return "";
        }

        private static void SimulateKeyCombo(byte[] virtualKeys)
        {
            // Press keys in order
            for (int i = 0; i < virtualKeys.Length; i++)
            {
                keybd_event(virtualKeys[i], 0, 0, UIntPtr.Zero);
            }
            // Release keys in reverse order
            for (int i = virtualKeys.Length - 1; i >= 0; i--)
            {
                keybd_event(virtualKeys[i], 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
        }

        private static void SkipMedia(int seconds)
        {
            try
            {
                string activeTitle = GetActiveWindowTitle();
                if (!string.IsNullOrEmpty(activeTitle))
                {
                    if (activeTitle.Contains("vlc"))
                    {
                        if (seconds > 0)
                        {
                            // Simulate Alt + Right Arrow (VLC skip 10s forward)
                            SimulateKeyCombo(new byte[] { 0x12, 0x27 }); // VK_MENU (Alt = 0x12), VK_RIGHT (0x27)
                        }
                        else
                        {
                            // Simulate Alt + Left Arrow (VLC skip 10s backward)
                            SimulateKeyCombo(new byte[] { 0x12, 0x25 }); // VK_MENU (Alt = 0x12), VK_LEFT (0x25)
                        }
                    }
                    else
                    {
                        // Default: Web browsers (Chrome, Edge, Firefox, Opera, YouTube, Netflix, etc.)
                        if (seconds > 0)
                        {
                            // Send 'L' key (skip 10s forward)
                            SimulateKey(0x4C); // 'L'
                        }
                        else
                        {
                            // Send 'J' key (skip 10s backward)
                            SimulateKey(0x4A); // 'J'
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error skipping media: {ex.Message}");
            }
        }

        private static void AdjustBrightness(int delta)
        {
            try
            {
                byte current = GetBrightness();
                int target = Math.Clamp(current + delta, 0, 100);
                SetBrightness((byte)target);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to adjust brightness: {ex.Message}");
            }
        }

        private static byte GetBrightness()
        {
            try
            {
                var scope = new System.Management.ManagementScope("root\\wmi");
                var query = new System.Management.SelectQuery("WmiMonitorBrightness");
                using var searcher = new System.Management.ManagementObjectSearcher(scope, query);
                using var collection = searcher.Get();
                foreach (System.Management.ManagementObject mObj in collection)
                {
                    return (byte)mObj["CurrentBrightness"];
                }
            }
            catch { }
            return 50; // Fallback
        }

        private static void SetBrightness(byte brightness)
        {
            try
            {
                var scope = new System.Management.ManagementScope("root\\wmi");
                var query = new System.Management.SelectQuery("WmiMonitorBrightnessMethods");
                using var searcher = new System.Management.ManagementObjectSearcher(scope, query);
                using var collection = searcher.Get();
                foreach (System.Management.ManagementObject mObj in collection)
                {
                    mObj.InvokeMethod("WmiSetBrightness", new object[] { uint.MaxValue, brightness });
                    break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set brightness: {ex.Message}");
            }
        }

        public static bool ToggleMicrophoneMute()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                bool state = !device.AudioEndpointVolume.Mute;
                device.AudioEndpointVolume.Mute = state;
                return state;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Microphone toggle error: {ex.Message}");
                return false;
            }
        }



        private static async Task ToggleWifi()
        {
            try
            {
                var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
                foreach (var radio in radios)
                {
                    if (radio.Kind == Windows.Devices.Radios.RadioKind.WiFi)
                    {
                        var targetState = radio.State == Windows.Devices.Radios.RadioState.On 
                            ? Windows.Devices.Radios.RadioState.Off 
                            : Windows.Devices.Radios.RadioState.On;
                        await radio.SetStateAsync(targetState);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to toggle wifi: {ex.Message}");
            }
        }

        private static async Task ToggleBluetooth()
        {
            try
            {
                var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
                foreach (var radio in radios)
                {
                    if (radio.Kind == Windows.Devices.Radios.RadioKind.Bluetooth)
                    {
                        var targetState = radio.State == Windows.Devices.Radios.RadioState.On 
                            ? Windows.Devices.Radios.RadioState.Off 
                            : Windows.Devices.Radios.RadioState.On;
                        await radio.SetStateAsync(targetState);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to toggle bluetooth: {ex.Message}");
            }
        }

        private static void CloseAllApplications()
        {
            try
            {
                var currentProcess = Process.GetCurrentProcess();
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        if (process.MainWindowHandle != IntPtr.Zero && 
                            process.Id != currentProcess.Id && 
                            !process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                        {
                            process.CloseMainWindow();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to close process {process.ProcessName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to get processes: {ex.Message}");
            }
        }
    }
}
