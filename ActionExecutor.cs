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
                case "hotkey":
                    ExecuteHotkeyAction(data);
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

        private static readonly HashSet<string> CommonTlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "com", "org", "net", "gov", "edu", "io", "co", "uk", "in", "us", "ca", "de", 
            "jp", "fr", "au", "ru", "ch", "it", "nl", "se", "no", "es", "mil", "app", 
            "dev", "ai", "xyz", "info", "biz", "site", "online", "store", "tech", "me"
        };

        private static List<string> ExtractUrlKeywords(string url)
        {
            var keywords = new List<string>();
            if (string.IsNullOrWhiteSpace(url)) return keywords;

            try
            {
                string temp = url.Trim();
                if (temp.Contains("|"))
                {
                    temp = temp.Split('|')[0].Trim();
                }

                if (!temp.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !temp.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    temp = "https://" + temp;
                }

                var uri = new Uri(temp);
                string host = uri.Host.ToLower();
                if (host.StartsWith("www.")) host = host.Substring(4);

                string[] hostParts = host.Split('.');
                foreach (var part in hostParts)
                {
                    if (!string.IsNullOrWhiteSpace(part) && !CommonTlds.Contains(part) && part.Length >= 2)
                    {
                        keywords.Add(part);
                    }
                }

                var segments = uri.Segments;
                if (segments != null && segments.Length > 0)
                {
                    foreach (var seg in segments)
                    {
                        string cleanSeg = seg.Trim('/');
                        if (!string.IsNullOrEmpty(cleanSeg) && cleanSeg.Length >= 3)
                        {
                            string unescaped = Uri.UnescapeDataString(cleanSeg).ToLower();
                            if (!keywords.Contains(unescaped) && !CommonTlds.Contains(unescaped))
                            {
                                keywords.Add(unescaped);
                            }
                        }
                    }
                }

                if (host.Contains("localhost") || host.Contains("127.0.0.1"))
                {
                    keywords.Add("swift dock");
                    keywords.Add("swiftdock");
                    keywords.Add("localhost");
                }
            }
            catch
            {
                string fallback = url.ToLower();
                if (fallback.Contains("|")) fallback = fallback.Split('|')[0].Trim();
                keywords.Add(fallback);
            }

            return System.Linq.Enumerable.ToList(System.Linq.Enumerable.Distinct(keywords, StringComparer.OrdinalIgnoreCase));
        }

        private static bool ActivateBrowserTabViaUIA(IntPtr hWnd, List<string> keywords)
        {
            if (keywords == null || keywords.Count == 0) return false;
            try
            {
                var rootElement = AutomationElement.FromHandle(hWnd);
                if (rootElement == null) return false;

                var tabCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
                var tabs = rootElement.FindAll(TreeScope.Descendants, tabCondition);

                if ((tabs == null || tabs.Count == 0) && IsIconic(hWnd))
                {
                    ShowWindow(hWnd, SW_RESTORE);
                    rootElement = AutomationElement.FromHandle(hWnd);
                    if (rootElement != null)
                    {
                        tabs = rootElement.FindAll(TreeScope.Descendants, tabCondition);
                    }
                }

                if (tabs != null)
                {
                    foreach (AutomationElement tab in tabs)
                    {
                        string tabName = tab.Current.Name;
                        if (string.IsNullOrEmpty(tabName)) continue;

                        foreach (var kw in keywords)
                        {
                            if (!string.IsNullOrEmpty(kw) && kw.Length >= 3)
                            {
                                if (tabName.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    bool activated = false;

                                    if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object selPatternObj))
                                    {
                                        var selectPattern = selPatternObj as SelectionItemPattern;
                                        selectPattern?.Select();
                                        activated = true;
                                    }

                                    if (!activated && tab.TryGetCurrentPattern(InvokePattern.Pattern, out object invPatternObj))
                                    {
                                        var invokePattern = invPatternObj as InvokePattern;
                                        invokePattern?.Invoke();
                                        activated = true;
                                    }

                                    try
                                    {
                                        tab.SetFocus();
                                    }
                                    catch { }

                                    return true;
                                }
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

        private static bool SwitchToBrowserTab(string rawData, string buttonTitle)
        {
            if (string.IsNullOrWhiteSpace(rawData)) return false;
            try
            {
                string url = rawData;
                if (rawData.Contains("|"))
                {
                    var parts = rawData.Split('|');
                    url = parts[0].Trim();
                }
                if (string.IsNullOrWhiteSpace(url)) return false;

                var urlKeywords = ExtractUrlKeywords(url);
                var keywords = new List<string>();

                string cleanTitle = !string.IsNullOrWhiteSpace(buttonTitle) &&
                                    !buttonTitle.Equals("New Button", StringComparison.OrdinalIgnoreCase) &&
                                    !buttonTitle.Equals("Open Website", StringComparison.OrdinalIgnoreCase) &&
                                    !buttonTitle.Equals("Launch Application", StringComparison.OrdinalIgnoreCase) &&
                                    !buttonTitle.Equals("URL", StringComparison.OrdinalIgnoreCase) &&
                                    !buttonTitle.Equals("Website", StringComparison.OrdinalIgnoreCase)
                                    ? buttonTitle.Trim()
                                    : "";

                if (!string.IsNullOrEmpty(cleanTitle))
                {
                    keywords.Add(cleanTitle);
                }

                foreach (var kw in urlKeywords)
                {
                    if (!keywords.Contains(kw, StringComparer.OrdinalIgnoreCase))
                    {
                        keywords.Add(kw);
                    }
                }

                if (keywords.Count == 0) return false;

                IntPtr targetWindow = IntPtr.Zero;

                var browserProcNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "arc", "iexplore", "waterfox", "librewolf", "sidekick"
                };

                // 1. Search active tabs of top-level browser windows
                EnumWindows((hWnd, lParam) =>
                {
                    if (IsWindowVisible(hWnd))
                    {
                        uint procId;
                        GetWindowThreadProcessId(hWnd, out procId);
                        try
                        {
                            using var proc = Process.GetProcessById((int)procId);
                            if (browserProcNames.Contains(proc.ProcessName))
                            {
                                var sb = new System.Text.StringBuilder(512);
                                GetWindowText(hWnd, sb, 512);
                                string title = sb.ToString();

                                if (!string.IsNullOrEmpty(title))
                                {
                                    foreach (var kw in keywords)
                                    {
                                        if (!string.IsNullOrEmpty(kw) && kw.Length >= 3)
                                        {
                                            if (title.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                                            {
                                                targetWindow = hWnd;
                                                return false; // Stop enumeration
                                            }
                                        }
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

                // 2. Search background tabs in browser windows via UI Automation
                EnumWindows((hWnd, lParam) =>
                {
                    if (IsWindowVisible(hWnd))
                    {
                        uint procId;
                        GetWindowThreadProcessId(hWnd, out procId);
                        try
                        {
                            using var proc = Process.GetProcessById((int)procId);
                            if (browserProcNames.Contains(proc.ProcessName))
                            {
                                if (ActivateBrowserTabViaUIA(hWnd, keywords))
                                {
                                    targetWindow = hWnd;
                                    return false; // Stop enumeration
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
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = path,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        Process.Start(psi);
                        return;
                    }
                    catch (Exception exFallback)
                    {
                        System.Diagnostics.Debug.WriteLine($"Fallback launch failed: {exFallback.Message}");
                    }
                }

                // If path doesn't exist on this PC, notify user cleanly
                App.Current?.Dispatcher?.Invoke(() =>
                {
                    System.Windows.MessageBox.Show(
                        $"The application configured for this button is not installed or path was not found on this PC:\n\nPath: {path}\n\nYou can edit this button to link it to an installed app.",
                        "SwiftDock - App Not Found",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information
                    );
                });
            }
        }

        private static void OpenUrl(string rawData)
        {
            if (string.IsNullOrWhiteSpace(rawData)) return;

            var parts = rawData.Split('|');
            string url = parts[0].Trim();
            string exePath = parts.Length >= 2 ? parts[1].Trim() : "";
            string profileArg = parts.Length >= 3 ? parts[2].Trim() : "";

            if (string.IsNullOrWhiteSpace(url)) return;

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(exePath) && System.IO.File.Exists(exePath))
                {
                    string args = string.IsNullOrWhiteSpace(profileArg) ? $"\"{url}\"" : $"{profileArg} \"{url}\"";
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = args,
                        UseShellExecute = false
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error launching URL {url}: {ex.Message}");
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                catch { }
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

                // Power Off / Sleep / Hibernate / Restart
                case "pc_shutdown":
                    Server.RequestMobileConfirmation("pc_shutdown", "Confirm Shutdown", "Are you sure you want to shut down your PC?");
                    break;
                case "pc_restart":
                    Server.RequestMobileConfirmation("pc_restart", "Confirm Restart", "Are you sure you want to restart your PC?");
                    break;
                case "pc_sleep":
                    Server.RequestMobileConfirmation("pc_sleep", "Confirm Sleep", "Are you sure you want to put your PC to sleep?");
                    break;
                case "pc_hibernate":
                    Server.RequestMobileConfirmation("pc_hibernate", "Confirm Hibernate", "Are you sure you want to hibernate your PC?");
                    break;
                case "pc_lock":
                    LockWorkStation();
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

        public static void ExecuteSystemActionDirect(string data)
        {
            switch (data.ToLower())
            {
                case "pc_shutdown":
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "shutdown",
                        Arguments = "/s /f /t 0",
                        CreateNoWindow = true,
                        UseShellExecute = true
                    });
                    break;
                case "pc_restart":
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "shutdown",
                        Arguments = "/r /f /t 0",
                        CreateNoWindow = true,
                        UseShellExecute = true
                    });
                    break;
                case "pc_hibernate":
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "shutdown",
                            Arguments = "/h",
                            CreateNoWindow = true,
                            UseShellExecute = true
                        });
                    }
                    catch
                    {
                        SetSuspendState(true, false, false);
                    }
                    break;
                case "pc_sleep":
                    SetSuspendState(false, true, true);
                    break;
                default:
                    ExecuteSystemAction(data);
                    break;
            }
        }

        public static void SendNextSlide()
        {
            SimulateKey(0x27); // VK_RIGHT
        }

        public static void SendPrevSlide()
        {
            SimulateKey(0x25); // VK_LEFT
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

        public static void ExecuteHotkeyAction(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return;

            try
            {
                var keys = ParseHotkeyString(data);
                if (keys.Length > 0)
                {
                    SimulateKeyCombo(keys);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error executing hotkey: {ex.Message}");
            }
        }

        private static byte[] ParseHotkeyString(string data)
        {
            var list = new List<byte>();
            string normalized = data.Trim().ToLowerInvariant();
            string[] parts = normalized.Split(new char[] { '+', '|', ',' }, StringSplitOptions.RemoveEmptyEntries);

            var modifiers = new List<byte>();
            var mainKeys = new List<byte>();

            foreach (var part in parts)
            {
                string token = part.Trim();
                switch (token)
                {
                    case "ctrl":
                    case "control":
                        modifiers.Add(0x11); // VK_CONTROL
                        break;
                    case "alt":
                    case "menu":
                        modifiers.Add(0x12); // VK_MENU
                        break;
                    case "shift":
                        modifiers.Add(0x10); // VK_SHIFT
                        break;
                    case "win":
                    case "windows":
                    case "cmd":
                        modifiers.Add(0x5B); // VK_LWIN
                        break;
                    default:
                        byte vk = GetVirtualKeyCode(token);
                        if (vk != 0) mainKeys.Add(vk);
                        break;
                }
            }

            list.AddRange(modifiers);
            list.AddRange(mainKeys);
            return list.ToArray();
        }

        private static byte GetVirtualKeyCode(string token)
        {
            if (token.Length == 1 && token[0] >= 'a' && token[0] <= 'z')
            {
                return (byte)('A' + (token[0] - 'a'));
            }
            if (token.Length == 1 && token[0] >= '0' && token[0] <= '9')
            {
                return (byte)('0' + (token[0] - '0'));
            }
            if (token.StartsWith("f") && int.TryParse(token.Substring(1), out int fNum) && fNum >= 1 && fNum <= 24)
            {
                return (byte)(0x70 + (fNum - 1)); // VK_F1 to VK_F24
            }

            switch (token)
            {
                case "tab": return 0x09;
                case "enter": case "return": return 0x0D;
                case "esc": case "escape": return 0x1B;
                case "space": return 0x20;
                case "backspace": case "back": return 0x08;
                case "delete": case "del": return 0x2E;
                case "insert": return 0x2D;
                case "home": return 0x24;
                case "end": return 0x23;
                case "pageup": case "pgup": return 0x21;
                case "pagedown": case "pgdn": return 0x22;
                case "up": case "arrowup": return 0x26;
                case "down": case "arrowdown": return 0x28;
                case "left": case "arrowleft": return 0x25;
                case "right": case "arrowright": return 0x27;
                case "plus": case "=": return 0xBB;
                case "minus": case "-": return 0xBD;
                default: return 0;
            }
        }
    }
}
