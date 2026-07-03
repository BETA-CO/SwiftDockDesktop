using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Windows.Automation;
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

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        private const int SW_RESTORE = 9;

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
                    ExecuteAction(button.ActionType, button.ActionData, enableSwitching: true, buttonTitle: button.Title);
                    if (button.ActionType.Equals("Macro", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var step in button.MacroSteps)
                        {
                            ExecuteAction(step.Type, step.Data, enableSwitching: false, buttonTitle: "");
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

        private static void ExecuteAction(string type, string data, bool enableSwitching, string buttonTitle)
        {
            switch (type.ToLower())
            {
                case "app":
                    if (enableSwitching && SwitchToProcess(data, buttonTitle))
                    {
                        break;
                    }
                    LaunchApp(data);
                    break;
                case "url":
                    if (enableSwitching && SwitchToBrowserTab(data, buttonTitle))
                    {
                        break;
                    }
                    OpenUrl(data);
                    break;
                case "system":
                    ExecuteSystemAction(data);
                    break;
                case "profile":
                    App.Current.Dispatcher.Invoke(() =>
                    {
                        if (App.Current.MainWindow is MainWindow mainWindow)
                        {
                            mainWindow.OnProfileChangeRequested(data);
                        }
                    });
                    break;
            }
        }

        private static bool SwitchToProcess(string path, string buttonTitle)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                string procName = "";
                try
                {
                    procName = System.IO.Path.GetFileNameWithoutExtension(path).ToLower();
                }
                catch
                {
                    if (path.Contains("\\"))
                    {
                        int lastSlash = path.LastIndexOf('\\');
                        procName = path.Substring(lastSlash + 1);
                    }
                    else
                    {
                        procName = path;
                    }
                    int dotIdx = procName.LastIndexOf('.');
                    if (dotIdx > 0) procName = procName.Substring(0, dotIdx);
                    procName = procName.ToLower();
                }

                if (string.IsNullOrEmpty(procName)) return false;

                if (procName == "microsoftedge") procName = "msedge";

                string keywordFromTitle = !string.IsNullOrWhiteSpace(buttonTitle) && 
                                          !buttonTitle.Equals("New Button", StringComparison.OrdinalIgnoreCase) && 
                                          !buttonTitle.Equals("Launch Application", StringComparison.OrdinalIgnoreCase)
                                          ? buttonTitle.Trim() 
                                          : "";

                IntPtr foundHWnd = IntPtr.Zero;

                EnumWindows((hWnd, lParam) =>
                {
                    if (IsWindowVisible(hWnd))
                    {
                        uint procId;
                        GetWindowThreadProcessId(hWnd, out procId);
                        try
                        {
                            using var proc = Process.GetProcessById((int)procId);
                            string currentProcName = proc.ProcessName.ToLower();

                            bool matches = false;

                            if (currentProcName == procName)
                            {
                                matches = true;
                            }

                            if (!matches && !string.IsNullOrEmpty(keywordFromTitle) && keywordFromTitle.Length >= 3)
                            {
                                var sb = new System.Text.StringBuilder(256);
                                GetWindowText(hWnd, sb, 256);
                                string title = sb.ToString();
                                if (!string.IsNullOrEmpty(title) && title.IndexOf(keywordFromTitle, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    matches = true;
                                }
                            }

                            if (matches)
                            {
                                foundHWnd = hWnd;
                                return false; // Stop enumeration
                            }
                        }
                        catch { }
                    }
                    return true;
                }, IntPtr.Zero);

                if (foundHWnd != IntPtr.Zero)
                {
                    ForceForegroundWindow(foundHWnd);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error switching to process for path {path}: {ex.Message}");
            }
            return false;
        }

        private static void ForceForegroundWindow(IntPtr hWnd)
        {
            try
            {
                IntPtr foreWnd = GetForegroundWindow();
                uint junk;
                uint foreThread = GetWindowThreadProcessId(foreWnd, out junk);
                uint appThread = GetCurrentThreadId();

                if (foreThread != appThread)
                {
                    AttachThreadInput(appThread, foreThread, true);

                    if (IsIconic(hWnd))
                    {
                        ShowWindow(hWnd, SW_RESTORE);
                    }
                    else
                    {
                        ShowWindow(hWnd, 5); // SW_SHOW = 5
                    }

                    SetForegroundWindow(hWnd);
                    AttachThreadInput(appThread, foreThread, false);
                }
                else
                {
                    if (IsIconic(hWnd))
                    {
                        ShowWindow(hWnd, SW_RESTORE);
                    }
                    else
                    {
                        ShowWindow(hWnd, 5);
                    }
                    SetForegroundWindow(hWnd);
                }

                // Alt key bypass logic if not in foreground
                if (GetForegroundWindow() != hWnd)
                {
                    keybd_event(0x12, 0, 0, UIntPtr.Zero); // Press Alt (VK_MENU = 0x12)
                    keybd_event(0x12, 0, 0x0002, UIntPtr.Zero); // Release Alt (KEYEVENTF_KEYUP = 0x0002)
                    SetForegroundWindow(hWnd);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error forcing foreground window: {ex.Message}");
                if (IsIconic(hWnd))
                {
                    ShowWindow(hWnd, SW_RESTORE);
                }
                SetForegroundWindow(hWnd);
            }
        }

        private static List<string> ExtractUrlKeywords(string url)
        {
            var keywords = new List<string>();
            if (string.IsNullOrWhiteSpace(url)) return keywords;
            try
            {
                string temp = url.ToLower();
                if (!temp.StartsWith("http://") && !temp.StartsWith("https://"))
                {
                    temp = "https://" + temp;
                }
                var uri = new Uri(temp);
                string host = uri.Host;
                if (host.StartsWith("www.")) host = host.Substring(4);

                string[] parts = host.Split('.');
                if (parts.Length > 1)
                {
                    keywords.Add(parts[0]);
                }
                else
                {
                    keywords.Add(host);
                }

                // Extract path segments
                var segments = uri.Segments;
                if (segments.Length > 0)
                {
                    string lastSegment = segments[segments.Length - 1].Trim('/');
                    if (!string.IsNullOrEmpty(lastSegment) && lastSegment.Length >= 3)
                    {
                        keywords.Add(Uri.UnescapeDataString(lastSegment).ToLower());
                    }
                }

                // Fallback for local development
                if (host.Contains("localhost") || host.Contains("127.0.0.1"))
                {
                    keywords.Add("swift dock");
                    keywords.Add("swiftdock");
                    keywords.Add("localhost");
                }
            }
            catch
            {
                keywords.Add(url.ToLower());
            }
            return keywords;
        }

        private static bool ActivateBrowserTabViaUIA(IntPtr hWnd, string keyword)
        {
            if (string.IsNullOrEmpty(keyword)) return false;
            try
            {
                var rootElement = AutomationElement.FromHandle(hWnd);
                if (rootElement == null) return false;

                var tabCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
                var tabs = rootElement.FindAll(TreeScope.Descendants, tabCondition);

                foreach (AutomationElement tab in tabs)
                {
                    string tabName = tab.Current.Name;
                    if (!string.IsNullOrEmpty(tabName) && tabName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object patternObj))
                        {
                            var selectPattern = patternObj as SelectionItemPattern;
                            if (selectPattern != null)
                            {
                                selectPattern.Select();
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UI Automation error: {ex.Message}");
            }
            return false;
        }

        private static bool SwitchToBrowserTab(string url, string buttonTitle)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            try
            {
                var keywords = ExtractUrlKeywords(url);
                string keywordFromTitle = !string.IsNullOrWhiteSpace(buttonTitle) && 
                                          !buttonTitle.Equals("New Button", StringComparison.OrdinalIgnoreCase) && 
                                          !buttonTitle.Equals("Open Website", StringComparison.OrdinalIgnoreCase)
                                          ? buttonTitle.Trim() 
                                          : "";

                IntPtr targetWindow = IntPtr.Zero;

                // 1. Search for active tabs matching keywords
                EnumWindows((hWnd, lParam) =>
                {
                    if (IsWindowVisible(hWnd))
                    {
                        var sb = new System.Text.StringBuilder(256);
                        GetWindowText(hWnd, sb, 256);
                        string title = sb.ToString();
                        if (!string.IsNullOrEmpty(title))
                        {
                            bool matches = false;

                            foreach (var kw in keywords)
                            {
                                if (!string.IsNullOrEmpty(kw) && kw.Length >= 3)
                                {
                                    if (title.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        matches = true;
                                        break;
                                    }
                                }
                            }

                            if (!matches && !string.IsNullOrEmpty(keywordFromTitle) && keywordFromTitle.Length >= 3)
                            {
                                if (title.IndexOf(keywordFromTitle, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    matches = true;
                                }
                            }

                            if (matches)
                            {
                                uint procId;
                                GetWindowThreadProcessId(hWnd, out procId);
                                try
                                {
                                    using var proc = Process.GetProcessById((int)procId);
                                    string procName = proc.ProcessName.ToLower();
                                    if (procName == "chrome" || procName == "msedge" || procName == "firefox" || 
                                        procName == "opera" || procName == "brave" || procName == "iexplore")
                                    {
                                        targetWindow = hWnd;
                                        return false; // Stop enumeration
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    return true;
                }, IntPtr.Zero);

                if (targetWindow != IntPtr.Zero)
                {
                    ForceForegroundWindow(targetWindow);
                    return true;
                }

                // 2. Search background tabs in Chrome/Edge via UI Automation
                EnumWindows((hWnd, lParam) =>
                {
                    if (IsWindowVisible(hWnd))
                    {
                        uint procId;
                        GetWindowThreadProcessId(hWnd, out procId);
                        try
                        {
                            using var proc = Process.GetProcessById((int)procId);
                            string procName = proc.ProcessName.ToLower();
                            if (procName == "chrome" || procName == "msedge")
                            {
                                foreach (var kw in keywords)
                                {
                                    if (!string.IsNullOrEmpty(kw) && kw.Length >= 3)
                                    {
                                        if (ActivateBrowserTabViaUIA(hWnd, kw))
                                        {
                                            targetWindow = hWnd;
                                            return false;
                                        }
                                    }
                                }

                                if (targetWindow == IntPtr.Zero && !string.IsNullOrEmpty(keywordFromTitle) && keywordFromTitle.Length >= 3)
                                {
                                    if (ActivateBrowserTabViaUIA(hWnd, keywordFromTitle))
                                    {
                                        targetWindow = hWnd;
                                        return false;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    return true;
                }, IntPtr.Zero);

                if (targetWindow != IntPtr.Zero)
                {
                    ForceForegroundWindow(targetWindow);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error switching browser tab: {ex.Message}");
            }
            return false;
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
