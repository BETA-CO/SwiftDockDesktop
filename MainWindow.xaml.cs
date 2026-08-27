using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using System.Windows.Data;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Windows.Media.Control;
using Windows.Devices.Radios;

namespace SwiftDock
{

    public partial class MainWindow : Window
    {
        private readonly Server _server = new Server();
        private NotifyIcon _notifyIcon = null!;
        private bool _isExiting = false;
        private EventWaitHandle? _showEvent;
        private ShortcutButton? _selectedButton;
        private readonly HashSet<ShortcutButton> _selectedBulkButtons = new HashSet<ShortcutButton>();
        private bool _isUpdatingUi = false;
        private int _currentGridPage = 0;
        private GlobalSystemMediaTransportControlsSessionManager? _mediaSessionManager;
        private bool _isMediaPlaying = false;
        private bool _isWifiOn = false;
        private bool _isBluetoothOn = false;
        private readonly List<Radio> _radiosList = new List<Radio>();
        private DispatcherTimer? _perfTimer;
        private bool _isCellContextMenuOpen = false;
        private Action? _pendingContextMenuAction = null;
        private int _currentCpu = 0;
        private int _currentGpu = 0;
        private int _currentRam = 0;
        private int _currentTemp = 0;
        private string _currentWifi = "0 KB/s";
        private readonly Dictionary<string, (string actionData, string title, string icon, string color)> _buttonTypeBackups = new Dictionary<string, (string, string, string, string)>();
        private string _activeCategoryTab = "App";
        private bool _isRearrangeModeActive = false;
        private ShortcutButton? _rearrangeSourceButton = null;
        private Point _dragStartPoint;
        private System.Windows.Controls.Primitives.Popup? _urlSearchPopup;
        private PresentationOverlayWindow? _presentationOverlay;

        public MainWindow()
        {
            InitializeComponent();
            RegisterSmoothScrollHandler();

            // Load config synchronously (fast)
            ConfigManager.Load();

            // Initialize system tray early (needed for minimize to tray)
            InitializeSystemTray();

            // Wire up server events
            _server.PinGenerated += OnPinGenerated;
            _server.ClientConnected += OnClientConnected;
            _server.ClientDisconnected += OnClientDisconnected;
            _server.PairingSuccessful += OnPairingSuccessful;
            _server.ProfileChangeRequested += OnProfileChangeRequested;
            _server.ProfileChangeRequestedDirect += id => Dispatcher.Invoke(() => OnProfileChangeRequestedDirect(id));
            _server.LayoutChanged += OnLayoutChanged;
            _server.PageChangeRequested += OnPageChangeRequested;
            _server.PresentationCmdRequested += OnPresentationCmdRequested;
            _server.PresentationGyroRequested += OnPresentationGyroRequested;

            // Show disconnected state immediately
            ShowDisconnectedPanel();

            // Event handlers
            this.PreviewMouseDown += MainWindow_PreviewMouseDown;

            // Defer heavy initialization until after window is shown
            this.Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Now that the window is visible, initialize background services
            Task.Run(async () =>
            {
                // Small delay to let the UI settle
                await Task.Delay(50);

                // Initialize monitoring services (async, non-blocking)
                Dispatcher.Invoke(() =>
                {
                    InitializeMediaMonitoring();
                    InitializeRadioMonitoring();
                    InitializePerformanceMonitoring();
                    InitializeSingleInstanceListener();
                });

                // Check for updates in background
                await CheckForUpdatesAsync(showUpToDatePrompt: false);

                // Load installed apps last (most expensive operation)
                Dispatcher.Invoke(() =>
                {
                    LoadInstalledAppsAsync();
                });
            });
        }

        private void MainWindow_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_selectedButton == null && _rearrangeSourceButton == null) return;

            var originalSource = e.OriginalSource as DependencyObject;
            if (originalSource == null) return;

            if (IsClickInsideDeckCell(originalSource) || IsClickInsidePropertyEditor(originalSource))
            {
                return;
            }

            _rearrangeSourceButton = null;
            SelectShortcutButton(null);
        }

        private bool IsClickInsideDeckCell(DependencyObject? element)
        {
            while (element != null)
            {
                if (element is Border b && (b.Tag is ShortcutButton || "DISCONNECT_BUTTON".Equals(b.Tag)))
                {
                    return true;
                }
                if (element == GridPreview || element == BtnRearrangeDock || element == BtnDoneRearrangingHeader)
                {
                    return true;
                }
                element = VisualTreeHelper.GetParent(element);
            }
            return false;
        }

        private bool IsClickInsidePropertyEditor(DependencyObject? element)
        {
            while (element != null)
            {
                if (element == PanelButtonProperties || 
                    element == BorderActionSubTabs ||
                    element == TabBtnApp ||
                    element == TabBtnSetting ||
                    element == TabBtnCtrl ||
                    element == TabBtnProfile ||
                    element == TabBtnHotkey ||
                    element == PanelAppSearch ||
                    element == PanelUrlSearch ||
                    element == TxtAppSearch ||
                    element == TxtUrlSearch ||
                    element == UrlConfigModal ||
                    element == ModalReiconsPicker ||
                    element == ModalProfilePin ||
                    element == ModalVolumeButtons ||
                    element == GridAboutOverlay ||
                    element == GridSplashScreenOverlay)
                {
                    return true;
                }

                if (element is FrameworkElement fe && fe.Name == "GridDashboardMainContent")
                {
                    return true;
                }

                if (element is System.Windows.Controls.Primitives.Popup)
                {
                    return true;
                }
                element = VisualTreeHelper.GetParent(element);
            }
            return false;
        }

        private static void RegisterSmoothScrollHandler()
        {
            EventManager.RegisterClassHandler(
                typeof(ScrollViewer),
                UIElement.PreviewMouseWheelEvent,
                new System.Windows.Input.MouseWheelEventHandler(OnPreviewMouseWheelSmooth)
            );
        }

        private static void OnPreviewMouseWheelSmooth(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            ScrollViewer? scrollViewer = sender as ScrollViewer;
            if (scrollViewer == null && e.OriginalSource is DependencyObject obj)
            {
                while (obj != null && obj is not ScrollViewer)
                {
                    obj = VisualTreeHelper.GetParent(obj);
                }
                scrollViewer = obj as ScrollViewer;
            }

            if (scrollViewer != null)
            {
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - (e.Delta * 0.45));
                e.Handled = true;
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            System.Windows.Interop.HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
        }

        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0024) // WM_GETMINMAXINFO
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            MINMAXINFO mmi = (MINMAXINFO)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(MINMAXINFO))!;

            int MONITOR_DEFAULTTONEAREST = 0x00000002;
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

            if (monitor != IntPtr.Zero)
            {
                MONITORINFO monitorInfo = new MONITORINFO();
                monitorInfo.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO));
                GetMonitorInfo(monitor, ref monitorInfo);

                RECT rcWorkArea = monitorInfo.rcWork;
                RECT rcMonitorArea = monitorInfo.rcMonitor;

                int posX = Math.Abs(rcWorkArea.Left - rcMonitorArea.Left);
                int posY = Math.Abs(rcWorkArea.Top - rcMonitorArea.Top);
                int sizeX = Math.Abs(rcWorkArea.Right - rcWorkArea.Left);
                int sizeY = Math.Abs(rcWorkArea.Bottom - rcWorkArea.Top);

                // Detect Windows Auto-Hide Taskbar setting
                try
                {
                    APPBARDATA abd = new APPBARDATA();
                    abd.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(APPBARDATA));
                    IntPtr state = SHAppBarMessage(ABM_GETSTATE, ref abd);

                    bool isAutoHide = (state.ToInt32() & ABS_AUTOHIDE) != 0;
                    if (isAutoHide)
                    {
                        SHAppBarMessage(ABM_GETTASKBARPOS, ref abd);
                        const int ABE_LEFT = 0;
                        const int ABE_TOP = 1;
                        const int ABE_RIGHT = 2;
                        const int ABE_BOTTOM = 3;

                        int autohideMargin = 2; // Leave 2px edge for Windows Shell_TrayWnd hover trigger

                        switch (abd.uEdge)
                        {
                            case ABE_BOTTOM:
                                sizeY -= autohideMargin;
                                break;
                            case ABE_TOP:
                                posY += autohideMargin;
                                sizeY -= autohideMargin;
                                break;
                            case ABE_LEFT:
                                posX += autohideMargin;
                                sizeX -= autohideMargin;
                                break;
                            case ABE_RIGHT:
                                sizeX -= autohideMargin;
                                break;
                        }
                    }
                }
                catch { }

                mmi.ptMaxPosition.x = posX;
                mmi.ptMaxPosition.y = posY;
                mmi.ptMaxSize.x = sizeX;
                mmi.ptMaxSize.y = sizeY;
            }

            System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
        }

        private const int ABM_GETSTATE = 0x00000004;
        private const int ABM_GETTASKBARPOS = 0x00000005;
        private const int ABS_AUTOHIDE = 0x00000001;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uCallbackMessage;
            public int uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [DllImport("shell32.dll")]
        private static extern IntPtr SHAppBarMessage(int dwMessage, ref APPBARDATA pData);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
            public POINT(int x, int y) { this.x = x; this.y = y; }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

        [DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        private void InitializeSystemTray()
        {
            _notifyIcon = new NotifyIcon();
            _notifyIcon.Text = "SwiftDock Control Center";
            
            try
            {
                // Extract current application icon
                _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location) 
                                   ?? SystemIcons.Application;
            }
            catch
            {
                _notifyIcon.Icon = SystemIcons.Application;
            }

            _notifyIcon.Visible = true;

            // Immediately open app on single Left-Click or Double-Click
            _notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    RestoreAndShowApp();
                }
            };
            _notifyIcon.DoubleClick += (s, e) => RestoreAndShowApp();

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Open Customizer", null, (s, e) => RestoreAndShowApp());
            contextMenu.Items.Add("Exit", null, (s, e) =>
            {
                _isExiting = true;
                this.Close();
            });

            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        private void RestoreAndShowApp()
        {
            if (!this.IsVisible)
            {
                this.Show();
            }
            if (this.WindowState == WindowState.Minimized)
            {
                this.WindowState = WindowState.Normal;
            }
            this.Activate();
            this.Focus();
            try
            {
                IntPtr handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                {
                    SetForegroundWindow(handle);
                }
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExiting)
            {
                e.Cancel = true;
                this.Hide();
                _notifyIcon.ShowBalloonTip(3000, "SwiftDock Running", "SwiftDock is running in the system tray.", ToolTipIcon.Info);
            }
            else
            {
                try
                {
                    _showEvent?.Close();
                    _showEvent = null;
                }
                catch { }
                _server.Stop();
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                base.OnClosing(e);
            }
        }

        private void InitializeSingleInstanceListener()
        {
            try
            {
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Global\\SwiftDockShowEvent-7E3F2A");
                Task.Run(() =>
                {
                    while (_showEvent != null)
                    {
                        try
                        {
                            if (_showEvent.WaitOne())
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    this.Show();
                                    this.WindowState = WindowState.Normal;
                                    this.Activate();
                                });
                            }
                        }
                        catch
                        {
                            break;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting single instance listener: {ex.Message}");
            }
        }

        private void OnPresentationCmdRequested(string cmd)
        {
            Dispatcher.Invoke(() =>
            {
                if (_presentationOverlay == null)
                {
                    _presentationOverlay = new PresentationOverlayWindow();
                }

                switch (cmd?.ToLower())
                {
                    case "next_slide":
                        ActionExecutor.SendNextSlide();
                        break;
                    case "prev_slide":
                        ActionExecutor.SendPrevSlide();
                        break;
                    case "laser":
                        if (_presentationOverlay.GetMode() == PresentationOverlayWindow.OverlayMode.Laser)
                            _presentationOverlay.SetMode(PresentationOverlayWindow.OverlayMode.Off);
                        else
                            _presentationOverlay.SetMode(PresentationOverlayWindow.OverlayMode.Laser);
                        break;
                    case "spotlight":
                        if (_presentationOverlay.GetMode() == PresentationOverlayWindow.OverlayMode.Spotlight)
                            _presentationOverlay.SetMode(PresentationOverlayWindow.OverlayMode.Off);
                        else
                            _presentationOverlay.SetMode(PresentationOverlayWindow.OverlayMode.Spotlight);
                        break;
                    case "exit":
                        _presentationOverlay.SetMode(PresentationOverlayWindow.OverlayMode.Off);
                        break;
                }
            });
        }

        private void OnPresentationGyroRequested(string mode, double dx, double dy)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                if (_presentationOverlay != null)
                {
                    _presentationOverlay.ApplyGyroDelta(dx, dy);
                }
            });
        }

        // Title bar window controls
        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void BtnMaximizeRestore_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
                BtnMaximizeRestore.Content = "\uE922";
            }
            else
            {
                this.WindowState = WindowState.Maximized;
                BtnMaximizeRestore.Content = "\uE923";
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void OnPinGenerated(string pin)
        {
        }

        private void OnPairingSuccessful()
        {
        }

        private void OnClientConnected(string mobileDeviceName)
        {
            Dispatcher.Invoke(() =>
            {
                // Record to connection history, removing any existing entry for the same device to prevent duplication
                ConfigManager.Current.ConnectionHistory.RemoveAll(c => c.DeviceName.Equals(mobileDeviceName, StringComparison.OrdinalIgnoreCase));

                var historyItem = new DeviceConnection
                {
                    DeviceName = mobileDeviceName,
                    ConnectionTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };
                ConfigManager.Current.ConnectionHistory.Insert(0, historyItem);
                if (ConfigManager.Current.ConnectionHistory.Count > 20)
                {
                    ConfigManager.Current.ConnectionHistory.RemoveAt(20);
                }
                ConfigManager.Save();

                // Make sure settings content is hidden and dashboard main content is visible
                HideSidebarSettings();
                GridDashboardMainContent.Visibility = Visibility.Visible;

                UpdateDashboardConnectionStatus(true, mobileDeviceName);
                RefreshProfilesList();
                RefreshGridPreview();
            });
        }

        private int _gridCols = 4;
        private int _gridRows = 2;
        private int GridPageSize => _gridCols * _gridRows;

        private void OnLayoutChanged(int cols, int rows)
        {
            Dispatcher.Invoke(() =>
            {
                if (cols > 0 && rows > 0)
                {
                    int totalButtons = cols * rows;
                    if (totalButtons == 15)
                    {
                        _gridCols = 5;
                        _gridRows = 3;
                    }
                    else
                    {
                        _gridCols = 4;
                        _gridRows = 2;
                    }

                    if (GridPreview != null)
                    {
                        GridPreview.Columns = _gridCols;
                        GridPreview.Rows = _gridRows;
                    }
                    RefreshGridPreview();
                }
            });
        }

        private void OnPageChangeRequested(int pageIndex)
        {
            Dispatcher.Invoke(() =>
            {
                if (pageIndex >= 0)
                {
                    _currentGridPage = pageIndex;
                    RefreshGridPreview();
                }
            });
        }

        private void OnClientDisconnected()
        {
            Dispatcher.Invoke(() =>
            {
                UpdateDashboardConnectionStatus(false, "");
                _gridCols = 4;
                _gridRows = 2;
                if (GridPreview != null)
                {
                    GridPreview.Columns = 4;
                    GridPreview.Rows = 2;
                }
                RefreshGridPreview();
            });
        }

        private void HideSidebarSettings()
        {
            if (GridSidebarSettings != null && GridSidebarSettings.Visibility == Visibility.Visible)
            {
                AnimateSidebarTransition(false);
            }
        }

        private void ShowDisconnectedPanel()
        {
            string name = ConfigManager.Current.DeviceName;
            if (string.IsNullOrEmpty(name)) name = Environment.MachineName;
            ConfigManager.Current.DeviceName = name;
            ConfigManager.Save();

            // Automatically start server if not already running
            if (!_server.IsRunning)
            {
                _server.Start(name);
            }

            // Hide sidebar settings to show profiles/shortcuts
            HideSidebarSettings();

            // Show main dashboard content and waiting message on status bar
            GridDashboardMainContent.Visibility = Visibility.Visible;
            UpdateDashboardConnectionStatus(_server.IsClientConnected, _server.ConnectedDeviceName);

            RefreshProfilesList();
            SelectShortcutButton(null);
        }

        private void UpdateDashboardConnectionStatus(bool connected, string name)
        {
            if (connected)
            {
                if (LblDeviceName != null) LblDeviceName.Text = string.IsNullOrEmpty(name) ? "Your Device" : name;
                if (DotStatus != null) DotStatus.Fill = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)); // Green
                if (LblConnectedStatus != null)
                {
                    LblConnectedStatus.Text = "Connected";
                    LblConnectedStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)); // Gray
                }
            }
            else
            {
                if (LblDeviceName != null) LblDeviceName.Text = "Your Device";
                if (DotStatus != null) DotStatus.Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)); // Red
                if (LblConnectedStatus != null)
                {
                    LblConnectedStatus.Text = "Not connected";
                    LblConnectedStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)); // Gray
                }
            }
        }

        private string _targetDownloadUrl = string.Empty;
        private CancellationTokenSource? _downloadCts;

        private class UpdateInfo
        {
            public string version { get; set; } = string.Empty;
            public string url { get; set; } = string.Empty;
            public string changelog { get; set; } = string.Empty;
        }

        private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            await CheckForUpdatesAsync(showUpToDatePrompt: true);
        }

        private async Task CheckForUpdatesAsync(bool showUpToDatePrompt)
        {
            try
            {
                using (var client = new System.Net.Http.HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("SwiftDock/1.0");

                    string jsonString = await client.GetStringAsync("https://raw.githubusercontent.com/BETA-CO/SwiftDockDesktop/main/update.json");
                    var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var update = System.Text.Json.JsonSerializer.Deserialize<UpdateInfo>(jsonString, options);

                    if (update != null && !string.IsNullOrEmpty(update.version))
                    {
                        var currentVersion = new Version("1.3.0");
                        var onlineVersion = new Version(update.version);

                        if (onlineVersion > currentVersion)
                        {
                            _targetDownloadUrl = update.url;
                            
                            TxtUpdateTitle.Text = "Software Update Available";
                            TxtUpdateStatus.Text = $"Version {update.version} is available. (Changelog: {update.changelog})";
                            PrgUpdateDownload.Value = 0;
                            TxtUpdateProgress.Text = "0%";
                            PanelProgress.Visibility = Visibility.Collapsed;

                            BtnUpdateLater.Visibility = Visibility.Visible;
                            BtnUpdateInstall.Content = "Update Now";
                            BtnUpdateInstall.IsEnabled = true;

                            GridUpdateOverlay.Visibility = Visibility.Visible;
                            return;
                        }
                    }
                }

                if (showUpToDatePrompt)
                {
                    GridUpToDateOverlay.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                if (showUpToDatePrompt)
                {
                    System.Windows.MessageBox.Show($"Unable to check for updates: {ex.Message}", 
                        "Check for Updates", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
            }
        }

        private void BtnCloseUpToDate_Click(object sender, RoutedEventArgs e)
        {
            GridUpToDateOverlay.Visibility = Visibility.Collapsed;
        }

        private void BtnUpdateLater_Click(object sender, RoutedEventArgs e)
        {
            _downloadCts?.Cancel();
            GridUpdateOverlay.Visibility = Visibility.Collapsed;
        }

        private async void BtnUpdateInstall_Click(object sender, RoutedEventArgs e)
        {
            if (BtnUpdateInstall.Content.ToString() == "Update Now")
            {
                BtnUpdateInstall.IsEnabled = false;
                BtnUpdateLater.Visibility = Visibility.Collapsed;
                PanelProgress.Visibility = Visibility.Visible;

                _downloadCts = new CancellationTokenSource();
                bool success = await DownloadUpdateAsync(_targetDownloadUrl, _downloadCts.Token);

                if (success)
                {
                    try
                    {
                        string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SwiftDockSetup.exe");
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = tempPath,
                            UseShellExecute = true
                        });

                        System.Windows.Application.Current.Shutdown();
                    }
                    catch (Exception ex)
                    {
                        System.Windows.MessageBox.Show($"Failed to launch installer: {ex.Message}", "Update Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                        BtnUpdateInstall.Content = "Update Now";
                        BtnUpdateInstall.IsEnabled = true;
                        BtnUpdateLater.Visibility = Visibility.Visible;
                    }
                }
                else
                {
                    BtnUpdateInstall.Content = "Update Now";
                    BtnUpdateInstall.IsEnabled = true;
                    BtnUpdateLater.Visibility = Visibility.Visible;
                }
            }
        }

        private async Task<bool> DownloadUpdateAsync(string url, CancellationToken token)
        {
            try
            {
                string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SwiftDockSetup.exe");

                using (var client = new System.Net.Http.HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("SwiftDock/1.0");

                    using (var response = await client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, token))
                    {
                        response.EnsureSuccessStatusCode();

                        long? totalBytes = response.Content.Headers.ContentLength;

                        using (var contentStream = await response.Content.ReadAsStreamAsync(token))
                        using (var fileStream = new System.IO.FileStream(tempPath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None, 8192, true))
                        {
                            var buffer = new byte[8192];
                            long totalReadBytes = 0;
                            int readBytes;

                            while ((readBytes = await contentStream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, readBytes, token);
                                totalReadBytes += readBytes;

                                if (totalBytes.HasValue)
                                {
                                    double progress = (double)totalReadBytes / totalBytes.Value * 100;
                                    Dispatcher.Invoke(() =>
                                    {
                                        PrgUpdateDownload.Value = progress;
                                        TxtUpdateProgress.Text = $"{Math.Round(progress)}%";
                                    });
                                }
                            }
                        }
                    }
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                System.Windows.MessageBox.Show("Download cancelled.", "Update Info", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return false;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Download failed: {ex.Message}", "Update Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return false;
            }
        }

        private void BtnHelpFeedback_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://beta-co.github.io/SwiftDockWeb/",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void BtnReportBug_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://beta-co.github.io/SwiftDockWeb/",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void BtnAbout_Click(object sender, RoutedEventArgs e)
        {
            GridAboutOverlay.Visibility = Visibility.Visible;
        }

        private void BtnCloseAbout_Click(object sender, RoutedEventArgs e)
        {
            GridAboutOverlay.Visibility = Visibility.Collapsed;
        }

        // Shortcuts Editor Logic
        private void SelectShortcutButton(ShortcutButton? btn)
        {
            if (_isUpdatingUi) return;

            _selectedButton = btn;

            // Calculate the page index for the selected button
            if (_selectedButton != null)
            {
                var buttons = ConfigManager.CurrentButtons;
                int selectedIndex = buttons != null ? buttons.IndexOf(_selectedButton) : -1;
                int previewSelectedIndex = selectedIndex >= 0 ? selectedIndex + 1 : -1;
                if (previewSelectedIndex >= 0)
                {
                    _currentGridPage = previewSelectedIndex / 8;
                }
            }

            HideSidebarSettings();
            GridDashboardMainContent.Visibility = Visibility.Visible;

            _isUpdatingUi = true;
            try
            {
                PanelButtonProperties.Visibility = Visibility.Visible;
                if (PanelNoSelectionPlaceholder != null)
                {
                    PanelNoSelectionPlaceholder.Visibility = Visibility.Collapsed;
                }

                string activeType = _selectedButton != null ? _selectedButton.ActionType : _activeCategoryTab;
                UpdateCategoryTabsHighlight(activeType);

                LoadActionDetails();
            }
            finally
            {
                _isUpdatingUi = false;
            }

            RefreshGridPreview();
        }

        private void UpdateCategoryTabsHighlight(string actionType)
        {
            TabBtnApp.Background = System.Windows.Media.Brushes.Transparent;
            TabBtnApp.Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));
            
            TabBtnCtrl.Background = System.Windows.Media.Brushes.Transparent;
            TabBtnCtrl.Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));

            TabBtnSetting.Background = System.Windows.Media.Brushes.Transparent;
            TabBtnSetting.Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));

            TabBtnProfile.Background = System.Windows.Media.Brushes.Transparent;
            TabBtnProfile.Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));

            if (TabBtnHotkey != null)
            {
                TabBtnHotkey.Background = System.Windows.Media.Brushes.Transparent;
                TabBtnHotkey.Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));
            }

            Button? activeBtn = null;
            switch (actionType?.ToLower())
            {
                case "app": activeBtn = TabBtnApp; break;
                case "url": activeBtn = TabBtnCtrl; break;
                case "system": activeBtn = TabBtnSetting; break;
                case "profile": activeBtn = TabBtnProfile; break;
                case "hotkey": activeBtn = TabBtnHotkey; break;
            }

            if (activeBtn != null)
            {
                activeBtn.Background = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x4C));
                activeBtn.Foreground = System.Windows.Media.Brushes.White;
            }

            if (PanelAppSearch != null)
            {
                PanelAppSearch.Visibility = (_selectedButton != null && "app".Equals(actionType, StringComparison.OrdinalIgnoreCase)) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public static List<InstalledApp>? AllInstalledApps;
        private List<InstalledApp>? _allInstalledApps
        {
            get => AllInstalledApps;
            set => AllInstalledApps = value;
        }

        private void TxtAppSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtAppSearchWatermark != null)
            {
                TxtAppSearchWatermark.Visibility = string.IsNullOrEmpty(TxtAppSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ListInstalledApps == null) return;

            if (_allInstalledApps == null && ListInstalledApps.ItemsSource is List<InstalledApp> currentList)
            {
                _allInstalledApps = currentList;
            }

            var appsToFilter = _allInstalledApps ?? (ListInstalledApps.ItemsSource as List<InstalledApp>);
            if (appsToFilter == null) return;

            string query = TxtAppSearch.Text.Trim();
            if (string.IsNullOrEmpty(query))
            {
                ListInstalledApps.ItemsSource = appsToFilter;
            }
            else
            {
                var filtered = appsToFilter.Where(a => 
                    (!string.IsNullOrEmpty(a.DisplayName) && a.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(a.ShortcutPath) && System.IO.Path.GetFileName(a.ShortcutPath).Contains(query, StringComparison.OrdinalIgnoreCase))
                ).ToList();
                ListInstalledApps.ItemsSource = filtered;
            }
        }

        private void RefreshProfilesList()
        {
            _isUpdatingUi = true;
            try
            {
                ListProfiles.ItemsSource = null;
                ListProfiles.ItemsSource = ConfigManager.Current.Profiles;

                // Select current profile
                Profile? currentProfile = ConfigManager.Current.Profiles.Find(p => p.Id == ConfigManager.Current.CurrentProfileId);
                if (currentProfile != null)
                {
                    ListProfiles.SelectedItem = currentProfile;
                }
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void RefreshShortcutsList()
        {
            // Empty placeholder as ListShortcuts is removed
        }

        private void RefreshGridPreview()
        {
            if (BtnDoneRearrangingHeader != null)
            {
                BtnDoneRearrangingHeader.Visibility = _isRearrangeModeActive ? Visibility.Visible : Visibility.Collapsed;
            }

            if (PanelBulkActions != null)
            {
                if (_selectedBulkButtons.Count > 0)
                {
                    PanelBulkActions.Visibility = Visibility.Visible;
                    if (BtnDeleteSelectedBulk != null)
                    {
                        BtnDeleteSelectedBulk.Content = $"Delete Selected ({_selectedBulkButtons.Count})";
                    }
                }
                else
                {
                    PanelBulkActions.Visibility = Visibility.Collapsed;
                }
            }

            GridPreview.Children.Clear();
            var buttons = ConfigManager.CurrentButtons;

            // Create a preview list that includes the disconnect button at index 0
            var previewList = new List<object>();
            previewList.Add("DISCONNECT_BUTTON");
            foreach (var btn in buttons)
            {
                previewList.Add(btn);
            }

            int totalPages = (int)Math.Ceiling(previewList.Count / (double)GridPageSize);
            if (buttons.Count < 63 && previewList.Count % GridPageSize == 0)
            {
                totalPages++;
            }
            totalPages = Math.Max(1, totalPages);

            // Keep manual page index in bounds
            if (_currentGridPage >= totalPages)
            {
                _currentGridPage = totalPages - 1;
            }
            if (_currentGridPage < 0)
            {
                _currentGridPage = 0;
            }

            // Update arrow button states
            if (BtnPrevPage != null && BtnNextPage != null)
            {
                BtnPrevPage.Opacity = _currentGridPage > 0 ? 1.0 : 0.3;
                BtnPrevPage.IsEnabled = _currentGridPage > 0;
                BtnNextPage.Opacity = _currentGridPage < totalPages - 1 ? 1.0 : 0.3;
                BtnNextPage.IsEnabled = _currentGridPage < totalPages - 1;
            }

            RefreshPageDots(_currentGridPage, totalPages);

            int startIdx = _currentGridPage * GridPageSize;

            for (int i = 0; i < GridPageSize; i++)
            {
                int absoluteIdx = startIdx + i;
                Border cell = new Border
                {
                    Width = 74,
                    Height = 74,
                    Margin = new Thickness(6),
                    CornerRadius = new CornerRadius(20),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                if (absoluteIdx < previewList.Count)
                {
                    var item = previewList[absoluteIdx];
                    if (item is string && (string)item == "DISCONNECT_BUTTON")
                    {
                        // Settings gear button slot 0 (styled as matte dark keycap to match mobile Settings button)
                        cell.Background = new LinearGradientBrush(
                            Color.FromRgb(0x12, 0x12, 0x1A),
                            Color.FromRgb(0x06, 0x06, 0x0A),
                            new Point(0, 0),
                            new Point(1, 1)
                        );
                        cell.BorderThickness = new Thickness(0);
                        
                        TextBlock txt = new TextBlock
                        {
                            Text = "\uE713", // Gear settings icon glyph in Segoe MDL2 Assets
                            FontFamily = new FontFamily("Segoe MDL2 Assets"),
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            FontSize = 36,
                            FontWeight = FontWeights.Normal,
                            Foreground = new SolidColorBrush(Colors.White)
                        };
                        cell.Child = txt;
                    }
                    else if (item is ShortcutButton btn)
                    {
                        // Selected state border highlight or Rearrange mode highlight
                        if (_isRearrangeModeActive)
                        {
                            if (btn == _rearrangeSourceButton)
                            {
                                cell.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)); // Glowing Amber selection ring for swap
                                cell.BorderThickness = new Thickness(3);
                            }
                            else
                            {
                                cell.BorderBrush = new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xF1)); // Dashed edit border
                                cell.BorderThickness = new Thickness(1.5);
                            }
                        }
                        else if (btn == _selectedButton)
                        {
                            cell.BorderBrush = new SolidColorBrush(Colors.White);
                            cell.BorderThickness = new Thickness(2);
                        }
                        else
                        {
                            cell.BorderThickness = new Thickness(0);
                        }

                        cell.Background = new LinearGradientBrush(
                            Color.FromRgb(0x12, 0x12, 0x1A),
                            Color.FromRgb(0x06, 0x06, 0x0A),
                            new Point(0, 0),
                            new Point(1, 1)
                        );
                        cell.Cursor = System.Windows.Input.Cursors.Hand;
                        cell.Tag = btn;

                        cell.ContextMenu = CreateCellContextMenu(btn);
                        System.Windows.Controls.ContextMenuService.SetPlacement(cell, System.Windows.Controls.Primitives.PlacementMode.MousePoint);
                        
                        // Drag and Drop & 2-Click Swap handlers
                        cell.AllowDrop = _isRearrangeModeActive;

                        cell.PreviewMouseLeftButtonDown += (s, e) =>
                        {
                            if (_isRearrangeModeActive)
                            {
                                _dragStartPoint = e.GetPosition(null);
                            }
                        };

                        cell.MouseMove += (s, e) =>
                        {
                            if (_isRearrangeModeActive && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                            {
                                Point pos = e.GetPosition(null);
                                if (Math.Abs(pos.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                                    Math.Abs(pos.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                                {
                                    System.Windows.DragDrop.DoDragDrop(cell, btn, System.Windows.DragDropEffects.Move);
                                }
                            }
                        };

                        cell.DragOver += (s, e) =>
                        {
                            if (_isRearrangeModeActive)
                            {
                                e.Effects = System.Windows.DragDropEffects.Move;
                                e.Handled = true;
                            }
                        };

                        cell.Drop += (s, e) =>
                        {
                            if (_isRearrangeModeActive && e.Data.GetData(typeof(ShortcutButton)) is ShortcutButton sourceBtn)
                            {
                                SwapButtons(sourceBtn, btn);
                                e.Handled = true;
                            }
                        };

                        cell.MouseDown += (s, e) =>
                        {
                            var clickedBtn = (ShortcutButton)((Border)s).Tag;
                            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                            {
                                if (_isRearrangeModeActive)
                                {
                                    if (_rearrangeSourceButton == null)
                                    {
                                        _rearrangeSourceButton = clickedBtn;
                                        RefreshGridPreview();
                                    }
                                    else if (_rearrangeSourceButton == clickedBtn)
                                    {
                                        _rearrangeSourceButton = null;
                                        RefreshGridPreview();
                                    }
                                    else
                                    {
                                        SwapButtons(_rearrangeSourceButton, clickedBtn);
                                    }
                                }
                                else if (_selectedBulkButtons.Count > 0)
                                {
                                    if (_selectedBulkButtons.Contains(clickedBtn))
                                    {
                                        _selectedBulkButtons.Remove(clickedBtn);
                                    }
                                    else
                                    {
                                        _selectedBulkButtons.Add(clickedBtn);
                                    }
                                    RefreshGridPreview();
                                }
                                else
                                {
                                    SelectShortcutButton(clickedBtn);
                                }
                                e.Handled = true;
                            }
                        };

                        // Check if profile button targets a locked profile
                        Profile? targetProf = btn.ActionType.Equals("Profile", StringComparison.OrdinalIgnoreCase)
                            ? ConfigManager.Current.Profiles.Find(p => p.Id.Equals(btn.ActionData, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(btn.ActionData, StringComparison.OrdinalIgnoreCase))
                            : null;

                        if (targetProf != null && targetProf.IsLocked)
                        {
                            cell.Child = CreateIconElement("locked_profile", 36, System.Windows.HorizontalAlignment.Center, VerticalAlignment.Center, new Thickness(0));
                        }
                        else if (btn.Icon != null && btn.Icon.Contains("|"))
                        {
                            var parts = btn.Icon.Split('|', StringSplitOptions.RemoveEmptyEntries);
                            var grid = new Grid
                            {
                                Width = 48,
                                Height = 48,
                                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                                VerticalAlignment = VerticalAlignment.Center
                            };

                            int count = Math.Min(parts.Length, 4);
                            int rows = count > 2 ? 2 : 1;
                            int cols = count > 1 ? 2 : 1;

                            for (int r = 0; r < rows; r++)
                                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                            for (int c = 0; c < cols; c++)
                                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                            for (int idx = 0; idx < count; idx++)
                            {
                                var part = parts[idx].Trim();
                                UIElement itemEl;
                                if (part.StartsWith("data:"))
                                {
                                    string b64 = part.Substring(5);
                                    var imgSource = Base64PngToImageSource(b64);
                                    if (imgSource != null)
                                    {
                                        itemEl = new System.Windows.Controls.Image
                                        {
                                            Source = imgSource,
                                            Stretch = Stretch.Uniform,
                                            Margin = new Thickness(1),
                                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                                            VerticalAlignment = VerticalAlignment.Center
                                        };
                                    }
                                    else
                                    {
                                        itemEl = CreateIconElement("url", 16, System.Windows.HorizontalAlignment.Center, VerticalAlignment.Center, new Thickness(0));
                                    }
                                }
                                else
                                {
                                    itemEl = CreateIconElement(part, 16, System.Windows.HorizontalAlignment.Center, VerticalAlignment.Center, new Thickness(0));
                                }

                                int rowIdx = idx / 2;
                                int colIdx = idx % 2;
                                if (rows == 1)
                                {
                                    rowIdx = 0;
                                    colIdx = idx;
                                }

                                Grid.SetRow(itemEl, rowIdx);
                                Grid.SetColumn(itemEl, colIdx);
                                grid.Children.Add(itemEl);
                            }

                            cell.Child = grid;
                        }
                        else if (btn.Icon != null && btn.Icon.StartsWith("data:"))
                        {
                            string b64 = btn.Icon.Substring(5);
                            var imgSource = Base64PngToImageSource(b64);
                            if (imgSource != null)
                            {
                                var img = new System.Windows.Controls.Image
                                {
                                    Source = imgSource,
                                    Width = 42,
                                    Height = 42,
                                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                                    VerticalAlignment = VerticalAlignment.Center
                                };
                                cell.Child = img;
                            }
                            else
                            {
                                cell.Child = CreateIconElement(btn.Icon, 36, System.Windows.HorizontalAlignment.Center, VerticalAlignment.Center, new Thickness(0));
                            }
                        }
                        else
                        {
                            bool isPerfBtn = btn.ActionType.Equals("System", StringComparison.OrdinalIgnoreCase) &&
                                             btn.ActionData.StartsWith("perf_", StringComparison.OrdinalIgnoreCase);

                            if (isPerfBtn)
                            {
                                var grid = new Grid();

                                grid.Children.Add(CreateIconElement(btn.Icon, 20, System.Windows.HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(8, 8, 0, 0)));

                                string valStr = "";
                                switch (btn.ActionData.ToLower())
                                {
                                    case "perf_cpu": valStr = $"{_currentCpu}%"; break;
                                    case "perf_gpu": valStr = $"{_currentGpu}%"; break;
                                    case "perf_ram": valStr = $"{_currentRam}%"; break;
                                    case "perf_temp": valStr = $"{_currentTemp}°C"; break;
                                    case "perf_wifi": valStr = _currentWifi; break;
                                }

                                double fontSize = btn.ActionData.Equals("perf_wifi", StringComparison.OrdinalIgnoreCase) ? 14 : 20;

                                var valTxt = new TextBlock
                                {
                                    Text = valStr,
                                    FontSize = fontSize,
                                    FontWeight = FontWeights.Bold,
                                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                                    VerticalAlignment = VerticalAlignment.Center,
                                    Foreground = new SolidColorBrush(Colors.White),
                                    Margin = new Thickness(0, 8, 0, 0) // Visual offset down to balance grid layout
                                };
                                grid.Children.Add(valTxt);

                                cell.Child = grid;
                            }
                            else
                            {
                                cell.Child = CreateIconElement(btn.Icon, 36, System.Windows.HorizontalAlignment.Center, VerticalAlignment.Center, new Thickness(0));
                            }
                        }



                        // Visual checkmark overlay if bulk-selected
                        if (_selectedBulkButtons.Contains(btn))
                        {
                            var originalChild = cell.Child;
                            cell.Child = null;

                            var containerGrid = new Grid();
                            if (originalChild != null)
                            {
                                containerGrid.Children.Add(originalChild);
                            }

                            // Create checkmark overlay badge
                            var checkBadge = new Border
                            {
                                Width = 18,
                                Height = 18,
                                CornerRadius = new CornerRadius(9),
                                Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), // Crimson/Red #EF4444
                                BorderBrush = new SolidColorBrush(Colors.White),
                                BorderThickness = new Thickness(1.5),
                                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                                VerticalAlignment = VerticalAlignment.Top,
                                Margin = new Thickness(0, 4, 4, 0),
                                IsHitTestVisible = false
                            };

                            var checkText = new TextBlock
                            {
                                Text = "\uE73E", // Checkmark icon in Segoe MDL2 Assets
                                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                                FontSize = 9,
                                FontWeight = FontWeights.Bold,
                                Foreground = new SolidColorBrush(Colors.White),
                                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                                VerticalAlignment = VerticalAlignment.Center
                            };

                            checkBadge.Child = checkText;
                            containerGrid.Children.Add(checkBadge);

                            cell.Child = containerGrid;
                        }
                    }
                }
                else
                {
                    // Empty slot placeholder
                    cell.Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x22));
                    cell.BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x32));
                    cell.BorderThickness = new Thickness(1);
                    cell.CornerRadius = new CornerRadius(14);
                    
                    TextBlock txt = new TextBlock
                    {
                        Text = "+",
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 26,
                        FontWeight = FontWeights.Medium,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63))
                    };
                    cell.Child = txt;
                    
                    cell.Cursor = System.Windows.Input.Cursors.Hand;
                    
                    var emptyContextMenu = new System.Windows.Controls.ContextMenu();
                    var rearrangeEmptyMenu = new System.Windows.Controls.MenuItem
                    {
                        Header = _isRearrangeModeActive ? "Done Rearranging" : "Rearrange Dock"
                    };
                    rearrangeEmptyMenu.Click += (s, e) => BtnRearrangeDock_Click(this, new RoutedEventArgs());
                    emptyContextMenu.Items.Add(rearrangeEmptyMenu);
                    cell.ContextMenu = emptyContextMenu;
                    System.Windows.Controls.ContextMenuService.SetPlacement(cell, System.Windows.Controls.Primitives.PlacementMode.MousePoint);

                    cell.MouseDown += (s, e) =>
                    {
                        if (e.ChangedButton == System.Windows.Input.MouseButton.Left && _selectedBulkButtons.Count == 0)
                        {
                            BtnAddButton_Click(null!, null!);
                        }
                    };
                }

                GridPreview.Children.Add(cell);
            }
        }

        private void SwapButtons(ShortcutButton source, ShortcutButton target)
        {
            if (source == null || target == null || source == target) return;

            var buttons = ConfigManager.CurrentButtons;
            int idxA = buttons.IndexOf(source);
            int idxB = buttons.IndexOf(target);

            if (idxA >= 0 && idxB >= 0)
            {
                buttons[idxA] = target;
                buttons[idxB] = source;

                _rearrangeSourceButton = null;
                ConfigManager.Save();
                _server.SyncButtons();
                RefreshGridPreview();
            }
        }

        private void BtnRearrangeDock_Click(object sender, RoutedEventArgs e)
        {
            _isRearrangeModeActive = !_isRearrangeModeActive;
            _rearrangeSourceButton = null;

            if (_isRearrangeModeActive)
            {
                if (BtnRearrangeDock != null)
                {
                    BtnRearrangeDock.Background = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)); // Green
                    BtnRearrangeDock.BorderBrush = new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69));
                }
                if (IconRearrangeDock != null) IconRearrangeDock.Text = "\uE73E"; // Checkmark icon
                if (LblRearrangeDock != null)
                {
                    LblRearrangeDock.Text = "Done Rearranging";
                    LblRearrangeDock.Foreground = new SolidColorBrush(Colors.White);
                }
            }
            else
            {
                if (BtnRearrangeDock != null)
                {
                    BtnRearrangeDock.ClearValue(System.Windows.Controls.Button.BackgroundProperty);
                    BtnRearrangeDock.ClearValue(System.Windows.Controls.Button.BorderBrushProperty);
                }
                if (IconRearrangeDock != null) IconRearrangeDock.Text = "\uE8D8"; // Rearrange icon
                if (LblRearrangeDock != null)
                {
                    LblRearrangeDock.Text = "Rearrange";
                    LblRearrangeDock.Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE7));
                }
            }

            RefreshGridPreview();
        }

        private void BtnCancelBulkSelection_Click(object sender, RoutedEventArgs e)
        {
            _selectedBulkButtons.Clear();
            RefreshGridPreview();
        }

        private void BtnDeleteSelectedBulk_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedBulkButtons.Count == 0) return;

            var confirm = MessageBox.Show(
                $"Are you sure you want to delete the {_selectedBulkButtons.Count} selected shortcut(s)?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm == MessageBoxResult.Yes)
            {
                foreach (var bulkBtn in _selectedBulkButtons)
                {
                    ConfigManager.CurrentButtons.Remove(bulkBtn);
                    if (_selectedButton == bulkBtn)
                    {
                        SelectShortcutButton(null);
                    }
                }
                _selectedBulkButtons.Clear();
                TriggerConfigSync();
            }
        }

        private System.Windows.Controls.ContextMenu CreateCellContextMenu(ShortcutButton btn)
        {
            var contextMenu = new System.Windows.Controls.ContextMenu();
            contextMenu.Opened += (s, e) => _isCellContextMenuOpen = true;
            contextMenu.Closed += (s, e) =>
            {
                _isCellContextMenuOpen = false;
                if (_pendingContextMenuAction != null)
                {
                    var action = _pendingContextMenuAction;
                    _pendingContextMenuAction = null;
                    System.Threading.Tasks.Task.Delay(200).ContinueWith(_ =>
                    {
                        Dispatcher.Invoke(action);
                    });
                }
            };

            // MenuItem: Rearrange Dock
            var rearrangeMenu = new System.Windows.Controls.MenuItem
            {
                Header = _isRearrangeModeActive ? "Done Rearranging" : "Rearrange Dock"
            };
            rearrangeMenu.Click += (s, e) =>
            {
                _pendingContextMenuAction = () =>
                {
                    BtnRearrangeDock_Click(this, new RoutedEventArgs());
                };
            };
            contextMenu.Items.Add(rearrangeMenu);

            // MenuItem: Delete Shortcut
            var deleteMenu = new System.Windows.Controls.MenuItem
            {
                Header = "Delete Shortcut"
            };
            deleteMenu.Click += (s, e) =>
            {
                _pendingContextMenuAction = () =>
                {
                    ConfigManager.CurrentButtons.Remove(btn);
                    if (_selectedButton == btn)
                    {
                        SelectShortcutButton(null);
                    }
                    _selectedBulkButtons.Remove(btn);
                    TriggerConfigSync();
                };
            };
            contextMenu.Items.Add(deleteMenu);

            // MenuItem: Select / Deselect for Bulk Delete
            bool isBulkSelected = _selectedBulkButtons.Contains(btn);
            var selectMenu = new System.Windows.Controls.MenuItem
            {
                Header = isBulkSelected ? "Deselect Shortcut" : "Delete Multiple Shortcuts"
            };
            selectMenu.Click += (s, e) =>
            {
                _pendingContextMenuAction = () =>
                {
                    if (isBulkSelected)
                    {
                        _selectedBulkButtons.Remove(btn);
                    }
                    else
                    {
                        _selectedBulkButtons.Add(btn);
                        SelectShortcutButton(null);
                    }
                    RefreshGridPreview();
                };
            };
            contextMenu.Items.Add(selectMenu);

            // MenuItem: Delete Selected (N)
            if (_selectedBulkButtons.Count > 0)
            {
                var deleteSelectedMenu = new System.Windows.Controls.MenuItem
                {
                    Header = $"Delete Selected ({_selectedBulkButtons.Count})"
                };
                deleteSelectedMenu.Click += (s, e) =>
                {
                    var confirm = System.Windows.MessageBox.Show(
                        $"Are you sure you want to delete the {_selectedBulkButtons.Count} selected shortcut(s)?",
                        "Confirm Delete",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Question);
                    if (confirm == System.Windows.MessageBoxResult.Yes)
                    {
                        foreach (var bulkBtn in _selectedBulkButtons)
                        {
                            ConfigManager.CurrentButtons.Remove(bulkBtn);
                            if (_selectedButton == bulkBtn)
                            {
                                SelectShortcutButton(null);
                            }
                        }
                        _selectedBulkButtons.Clear();
                        TriggerConfigSync();
                    }
                };
                contextMenu.Items.Add(deleteSelectedMenu);
            }

            return contextMenu;
        }

        private void RefreshPageDots(int currentPage, int totalPages)
        {
            if (PanelPageDots == null) return;
            PanelPageDots.Children.Clear();

            for (int i = 0; i < totalPages; i++)
            {
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Margin = new Thickness(4, 0, 4, 0),
                    Fill = (i == currentPage)
                        ? new SolidColorBrush(Colors.White)
                        : new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63))
                };
                PanelPageDots.Children.Add(dot);
            }
        }

        private void AnimateGridPreviewTransition(int direction)
        {
            if (GridPreview == null) return;

            GridPreview.BeginAnimation(UIElement.OpacityProperty, null);

            var translateTransform = GridPreview.RenderTransform as TranslateTransform;
            if (translateTransform == null)
            {
                translateTransform = new TranslateTransform();
                GridPreview.RenderTransform = translateTransform;
                GridPreview.RenderTransformOrigin = new Point(0.5, 0.5);
            }
            translateTransform.BeginAnimation(TranslateTransform.XProperty, null);

            double startX = direction > 0 ? 35.0 : (direction < 0 ? -35.0 : 0.0);

            var opacityAnim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0.2,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };

            var translateAnim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = startX,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };

            GridPreview.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
            translateTransform.BeginAnimation(TranslateTransform.XProperty, translateAnim);
        }

        private void BtnPrevPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentGridPage > 0)
            {
                _currentGridPage--;
                RefreshGridPreview();
                AnimateGridPreviewTransition(-1);
            }
        }

        private void BtnNextPage_Click(object sender, RoutedEventArgs e)
        {
            var buttons = ConfigManager.CurrentButtons;
            var previewListCount = buttons.Count + 1; // including the disconnect button
            int totalPages = (int)Math.Ceiling(previewListCount / 8.0);
            if (buttons.Count < 63 && previewListCount % 8 == 0)
            {
                totalPages++;
            }
            totalPages = Math.Max(1, totalPages);

            if (_currentGridPage < totalPages - 1)
            {
                _currentGridPage++;
                RefreshGridPreview();
                AnimateGridPreviewTransition(1);
            }
        }

        public static string GetSvgPathForIconName(string iconName)
        {
            if (string.IsNullOrEmpty(iconName)) return "";
            switch (iconName.ToLower())
            {
                case "play":
                case "media_play":
                case "media_play_pause":
                    return "M8,5 L19,12 L8,19 Z";
                case "media_pause":
                    return "M6,19h4V5H6v14zm8,-14v14h4V5h-4z";
                case "volume_up":
                    return "M3,9 H7 L12,4 V20 L7,15 H3 Z M16.5,8.5 A4.5,4.5 0,0,1 16.5,15.5 M19.5,5.5 A8.5,8.5 0,0,1 19.5,18.5";
                case "volume_down":
                    return "M3,9 H7 L12,4 V20 L7,15 H3 Z M16.5,8.5 A4.5,4.5 0,0,1 16.5,15.5";
                case "volume_mute":
                    return "M3,9 H7 L12,4 V20 L7,15 H3 Z M16,9.5 L21,14.5 M21,9.5 L16,14.5";
                case "brightness":
                case "brightness_up":
                    return "M12,12 m-4,0 a4,4 0,1,1 8,0 a4,4 0,1,1 -8,0 M12,2 V5 M12,19 V22 M2,12 H5 M19,12 H22 M5.6,5.6 L7.7,7.7 M16.3,16.3 L18.4,18.4 M18.4,5.6 L16.3,7.7 M7.7,16.3 L5.6,18.4";
                case "brightness_down":
                    return "M12,12 m-4,0 a4,4 0,1,1 8,0 a4,4 0,1,1 -8,0 M12,4 V5.5 M12,18.5 V20 M4,12 H5.5 M18.5,12 H20 M6.8,6.8 L7.9,7.9 M16.1,16.1 L17.2,17.2 M17.2,6.8 L16.1,7.9 M7.9,16.1 L6.8,17.2";
                case "media_next":
                    return "M6,6 L15,12 L6,18 Z M18,6 v12";
                case "media_prev":
                    return "M6,6 v12 M18,6 L9,12 L18,18 Z";
                case "media_forward_10":
                    return "M4,6 L12,12 L4,18 Z M12,6 L20,12 L12,18 Z";
                case "media_backward_10":
                    return "M20,6 L12,12 L20,18 Z M12,6 L4,12 L12,18 Z";
                case "web":
                case "url":
                    return "M12,12 m-10,0 a10,10 0,1,1 20,0 a10,10 0,1,1 -20,0 M2,12 H22 M12,12 m-4,0 a4,10 0,1,1 8,0 a4,10 0,1,1 -8,0";
                case "mic":
                    return "M12,2 A3,3 0,0,0 9,5 V12 A3,3 0,0,0 15,12 V5 A3,3 0,0,0 12,2 Z M6,10 A6,6 0,0,0 12,16 A6,6 0,0,0 18,10 M12,16 V21 M8,21 H16";
                case "code":
                    return "M8,7 L3,12 L8,17 M16,7 L21,12 L16,17 M14,6 L10,18";
                case "rocket":
                    return "M12,2 C12,2 15,6 15,11 C15,15 13,18 12,19 C11,18 9,15 9,11 C9,6 12,2 12,2 Z M9,15 L6,18 M15,15 L18,18 M12,19 V22";
                case "screen_record":
                    return "M17,10.5 V7 C17,6.45 16.55,6 16,6 H4 C3.45,6 3,6.45 3,7 V17 C3,17.55 3.45,18 4,18 H16 C16.55,18 17,17.55 17,17 V13.5 L21,17.5 V6.5 L17,10.5 Z";
                case "screenshot":
                    return "M9,2 L7.17,4 H4 C2.9,4 2,4.9 2,6 V18 C2,19.1 2.9,20 4,20 H20 C21.1,20 22,19.1 22,18 V6 C22,4.9 21.1,4 20,4 H16.83 L15,2 H9 Z M12,17 C9.24,17 7,14.76 7,12 C7,9.24 9.24,7 12,7 C14.76,7 17,9.24 17,12 C17,14.76 14.76,17 12,17 Z";
                case "home_screen":
                    return "M10 20v-6h4v6h5v-8h3L12 3 2 12h3v8z";
                case "close_all_apps":
                    return "M19,6.41 L17.59,5 L12,10.59 L6.41,5 L5,6.41 L10.59,12 L5,17.59 L6.41,19 L12,13.41 L17.59,19 L19,17.59 L13.41,12 Z";
                case "pc_shutdown":
                    return "M16.56,5.44L15.11,6.89C16.84,7.94 18,9.83 18,12A6,6 0 0,1 12,18A6,6 0 0,1 6,12C6,9.83 7.16,7.94 8.88,6.88L7.44,5.44C5.18,7.12 3.75,9.88 3.75,13A8.25,8.25 0 0,0 12,21.25A8.25,8.25 0 0,0 20.25,13C20.25,9.88 18.82,7.12 16.56,5.44M11,3H13V13H11V3Z";
                case "pc_sleep":
                    return "M3,12.79 A9,9 0,1,0 12.79,3 A7,7 0,0,1 3,12.79 Z";
                case "pc_lock":
                    return "M18,8h-1V6c0,-2.76 -2.24,-5 -5,-5S7,3.24 7,6v2H6c-1.1,0 -2,0.9 -2,2v10c0,1.1 0.9,2 2,2h12c1.1,0 2,-0.9 2,-2V10C20,8.9 19.1,8 18,8zM9,6c0,-1.66 1.34,-3 3,-3s3,1.34 3,3v2H9V6zM18,20H6V10h12V20zM12,13c-1.1,0 -2,0.9 -2,2s0.9,2 2,2s2,-0.9 2,-2S13.1,13 12,13z";
                case "pc_restart":
                    return "M3,12A9,9 0 1,0 5.64,5.64 M3,3V9H9";
                case "settings":
                    return "M19.43,12.98c0.04,-0.32 0.07,-0.64 0.07,-0.98s-0.03,-0.66 -0.07,-0.98l2.11,-1.65c0.19,-0.15 0.24,-0.42 0.12,-0.64l-2,-3.46c-0.12,-0.22 -0.39,-0.3 -0.61,-0.22l-2.49,1c-0.52,-0.4 -1.08,-0.73 -1.69,-0.98l-0.38,-2.65C14.46,2.18 14.25,2 14,2h-4c-0.25,0 -0.46,0.18 -0.49,0.42l-0.38,2.65c-0.61,0.25 -1.17,0.59 -1.69,0.98l-2.49,-1c-0.23,-0.09 -0.49,0 -0.61,0.22l-2,3.46c-0.13,0.22 -0.07,0.49 0.12,0.64l2.11,1.65c-0.04,0.32 -0.07,0.65 -0.07,0.98s0.03,0.66 0.07,0.98l-2.11,1.65c-0.19,0.15 -0.24,0.42 -0.12,0.64l2,3.46c0.12,0.22 0.39,0.3 0.61,0.22l2.49,-1c0.52,0.4 1.08,0.73 1.69,0.98l0.38,2.65c0.03,0.24 0.24,0.42 0.49,0.42h4c0.25,0 0.46,-0.18 0.49,-0.42l0.38,-2.65c0.61,-0.25 1.17,-0.59 1.69,-0.98l2.49,1c0.23,0.09 0.49,0 0.61,-0.22l2,-3.46c0.12,-0.22 0.07,-0.49 -0.12,-0.64l-2.11,-1.65zM12,15.5c-1.93,0 -3.5,-1.57 -3.5,-3.5s1.57,-3.5 3.5,-3.5 3.5,1.57 3.5,3.5 -1.57,3.5 -3.5,3.5z";
                case "folder":
                case "macro":
                    return "M10,4H4c-1.1,0 -1.99,0.9 -1.99,2L2,18c0,1.1 0.9,2 2,2h16c1.1,0 2,-0.9 2,-2V8c0,-1.1 -0.9,-2 -2,-2h-8l-2,-2z";
                case "locked_profile":
                    return "M3,4.2 C3,3.54 3.54,3 4.2,3 H8.4 C8.82,3 9.22,3.17 9.52,3.47 L11.7,5.7 H19.8 C20.46,5.7 21,6.24 21,6.9 V18.3 C21,18.96 20.46,19.5 19.8,19.5 H4.2 C3.54,19.5 3,18.96 3,18.3 V4.2 Z";
                case "wifi":
                    return "M12,21l11.64,-13.64C23.27,6.99 18.23,4 12,4S0.73,6.99 0.36,7.36L12,21z";
                case "wifi_off":
                    return "M12,21l11.64,-13.64C23.27,6.99 18.23,4 12,4S0.73,6.99 0.36,7.36L12,21z M2,2 L22,22";
                case "bluetooth":
                    return "M17.71,7.71L12,2h-1v7.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L11,14.41V22h1l5.71,-5.71L13.41,12L17.71,7.71z M13,5.83l1.88,1.88L13,9.59V5.83z M13,18.17v-3.76l1.88,1.88L13,18.17z";
                case "bluetooth_off":
                    return "M17.71,7.71L12,2h-1v7.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L11,14.41V22h1l5.71,-5.71L13.41,12L17.71,7.71z M13,5.83l1.88,1.88L13,9.59V5.83z M13,18.17v-3.76l1.88,1.88L13,18.17z M2,2 L22,22";
                case "perf_cpu":
                    return "M 4,4 H 20 V 20 H 4 Z M 4,13 H 7 L 9,16 L 11,6 L 13,17 L 15,10 L 17,13 H 20";
                case "perf_gpu":
                    return "M 3,4 V 18 M 3,7 H 21 V 15 H 3 Z M 6,15 V 17 H 12 V 15 M 8.5,11 m -3,0 a 3,3 0 1,0 6,0 a 3,3 0 1,0 -6,0 M 6.5,9.5 L 10.5,12.5 M 6.5,12.5 L 10.5,9.5 M 15.5,11 m -3,0 a 3,3 0 1,0 6,0 a 3,3 0 1,0 -6,0 M 13.5,9.5 L 17.5,12.5 M 13.5,12.5 L 17.5,9.5";
                case "perf_ram":
                    return "M 8,8 H 16 V 16 H 8 Z M 10,8 V 4 M 14,8 V 4 M 10,16 V 20 M 14,16 V 20 M 8,10 H 4 M 8,14 H 4 M 16,10 H 20 M 16,14 H 20";
                case "perf_temp":
                    return "M 10,13 V 6 A 2,2 0 0,1 14,6 V 13 A 4.5,4.5 0 1,1 10,13 Z M 16,6 H 18 M 16,9 H 18 M 16,12 H 18 M 12,17 A 2,2 0 1,1 12,13 V 8";
                case "perf_wifi":
                    return "M 3,16 H 5 V 19 H 3 Z M 7,13 H 9 V 19 H 7 Z M 11,10 H 13 V 19 H 11 Z M 15,7 H 17 V 19 H 15 Z M 19,4 H 21 V 19 H 19 Z";
                case "presentation_mode":
                case "presentation":
                    return "M 3.5,7 A 2.5,2.5 0 0,1 6,4.5 H 18 A 2.5,2.5 0 0,1 20.5,7 V 15 A 2.5,2.5 0 0,1 18,17.5 H 6 A 2.5,2.5 0 0,1 3.5,15 Z M 8,9 H 16 M 8,12 H 13";
                case "default":
                case "app_default":
                    return "M13,2 L4,13 H12 L11,22 L20,11 H12 Z";
                default:
                    return "";
            }
        }

        public static UIElement CreateIconElementStatic(string? iconName, double fontSize, System.Windows.HorizontalAlignment horizAlign, VerticalAlignment vertAlign, Thickness margin, bool isForPicker = false)
        {
            var brush = new SolidColorBrush(Colors.White);

            if (string.IsNullOrEmpty(iconName))
            {
                return new TextBlock
                {
                    Text = "\uE8A9",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    HorizontalAlignment = horizAlign,
                    VerticalAlignment = vertAlign,
                    FontSize = fontSize,
                    Foreground = brush,
                    Margin = margin
                };
            }

            // 1. Base64 Data URL Image (data:image/png;base64,... or data:...)
            if (iconName.StartsWith("data:"))
            {
                string b64 = iconName.StartsWith("data:image/png;base64,") ? iconName.Substring("data:image/png;base64,".Length) : (iconName.StartsWith("data:") ? iconName.Substring(5) : iconName);
                if (b64.Contains(",")) b64 = b64.Substring(b64.IndexOf(",") + 1);

                var imgSource = Base64PngToImageSource(b64);
                if (imgSource != null)
                {
                    return new System.Windows.Controls.Image
                    {
                        Source = imgSource,
                        Width = fontSize * 1.1,
                        Height = fontSize * 1.1,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = horizAlign,
                        VerticalAlignment = vertAlign,
                        Margin = margin
                    };
                }
            }

            // 2. HTTP / HTTPS Online URL Image (e.g. google.com/s2/favicons...)
            if (iconName.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || iconName.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconName));
                    return new System.Windows.Controls.Image
                    {
                        Source = bitmap,
                        Width = fontSize * 1.1,
                        Height = fontSize * 1.1,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = horizAlign,
                        VerticalAlignment = vertAlign,
                        Margin = margin
                    };
                }
                catch { }
            }

            // 3. Pipe-separated Multi-Icon Grid (e.g. icon1|icon2|icon3|icon4)
            if (iconName.Contains("|"))
            {
                var parts = iconName.Split('|', StringSplitOptions.RemoveEmptyEntries);
                var grid = new Grid
                {
                    Width = fontSize * 1.2,
                    Height = fontSize * 1.2,
                    HorizontalAlignment = horizAlign,
                    VerticalAlignment = vertAlign,
                    Margin = margin
                };

                int count = Math.Min(parts.Length, 4);
                int rows = count > 2 ? 2 : 1;
                int cols = count > 1 ? 2 : 1;

                for (int r = 0; r < rows; r++)
                    grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                for (int c = 0; c < cols; c++)
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                for (int idx = 0; idx < count; idx++)
                {
                    var part = parts[idx].Trim();
                    UIElement itemEl = CreateIconElementStatic(part, fontSize * 0.45, System.Windows.HorizontalAlignment.Center, VerticalAlignment.Center, new Thickness(1), isForPicker);

                    int rowIdx = idx / 2;
                    int colIdx = idx % 2;
                    if (rows == 1)
                    {
                        rowIdx = 0;
                        colIdx = idx;
                    }

                    Grid.SetRow(itemEl, rowIdx);
                    Grid.SetColumn(itemEl, colIdx);
                    grid.Children.Add(itemEl);
                }

                return grid;
            }

            // 4. Text / Emoji Symbol (text:...)
            if (iconName.StartsWith("text:"))
            {
                string txt = iconName.Substring(5);
                return new TextBlock
                {
                    Text = txt,
                    FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Inter, Segoe UI Emoji"),
                    HorizontalAlignment = horizAlign,
                    VerticalAlignment = vertAlign,
                    FontSize = txt.Length <= 2 ? fontSize * 1.0 : fontSize * 0.7,
                    FontWeight = FontWeights.Bold,
                    Foreground = brush,
                    Margin = margin,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                };
            }

            // 5. SVG Path (svgpath:...)
            if (iconName.StartsWith("svgpath:"))
            {
                string pData = iconName.Substring(8);
                try
                {
                    return new System.Windows.Shapes.Path
                    {
                        Data = Geometry.Parse(pData),
                        Fill = brush,
                        Width = fontSize * 1.1,
                        Height = fontSize * 1.1,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = horizAlign,
                        VerticalAlignment = vertAlign,
                        Margin = margin
                    };
                }
                catch
                {
                    return new TextBlock
                    {
                        Text = "\uE8A9",
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        HorizontalAlignment = horizAlign,
                        VerticalAlignment = vertAlign,
                        FontSize = fontSize,
                        Foreground = brush,
                        Margin = margin
                    };
                }
            }

            // 6. Vector Reicon Path
            string pathData = GetSvgPathForIconName(iconName);
            if (!string.IsNullOrEmpty(pathData))
            {
                var strokeFill = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse(pathData),
                    Stroke = brush,
                    StrokeThickness = 1.2,
                    Width = fontSize * 0.9,
                    Height = fontSize * 0.9,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = horizAlign,
                    VerticalAlignment = vertAlign,
                    Margin = margin
                };

                if (iconName.Equals("settings", StringComparison.OrdinalIgnoreCase) ||
                    iconName.Equals("bluetooth", StringComparison.OrdinalIgnoreCase) ||
                    iconName.Equals("bluetooth_off", StringComparison.OrdinalIgnoreCase) ||
                    iconName.Equals("folder", StringComparison.OrdinalIgnoreCase) ||
                    iconName.Equals("macro", StringComparison.OrdinalIgnoreCase) ||
                    iconName.Equals("wifi", StringComparison.OrdinalIgnoreCase) ||
                    iconName.Equals("wifi_off", StringComparison.OrdinalIgnoreCase) ||
                    iconName.Equals("perf_wifi", StringComparison.OrdinalIgnoreCase))
                {
                    strokeFill.Fill = brush;
                    strokeFill.StrokeThickness = 0;
                }
                
                return strokeFill;
            }

            // 7. Segoe MDL2 Fallback Glyph
            return new TextBlock
            {
                Text = GetGlyphForIconStatic(iconName),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                HorizontalAlignment = horizAlign,
                VerticalAlignment = vertAlign,
                FontSize = fontSize,
                FontWeight = FontWeights.Normal,
                Foreground = brush,
                Margin = margin
            };
        }

        private UIElement CreateIconElement(string? iconName, double fontSize, System.Windows.HorizontalAlignment horizAlign, VerticalAlignment vertAlign, Thickness margin)
        {
            return CreateIconElementStatic(iconName, fontSize, horizAlign, vertAlign, margin, false);
        }

        private static string GetGlyphForIconStatic(string? iconName)
        {
            if (string.IsNullOrEmpty(iconName)) return "\uE8A9";
            if (iconName.StartsWith("re_"))
            {
                var match = ReiconService.Search("").FirstOrDefault(i => i.Id.Equals(iconName, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match.Glyph;
            }
            switch (iconName.ToLower())
            {
                case "settings": return "\uE713";
                case "play": return "\uE768";
                case "folder": return "\uE8B7";
                case "app_default": return "\uE8A9"; // Grid of four box (Segoe MDL2 Assets)
                case "volume_up": return "\uE995";
                case "volume_down": return "\uE994";
                case "volume_mute": return "\uE74F";
                case "brightness": return "\uE706";
                case "brightness_up": return "\uE706";   // Sun (bright)
                case "brightness_down": return "\uE706"; // Sun with short rays (dim)
                case "media_play": return "\uE768";
                case "media_pause": return "\uE769";
                case "media_play_pause": return "\uE768";
                case "media_next": return "\uE893";
                case "media_prev": return "\uE892";
                case "media_forward_10": return "\uE760";
                case "media_backward_10": return "\uE75F";
                case "web":
                case "url": return "\uE774";
                case "mic": return "\uE720";
                case "camera":
                case "camera_toggle": return "\uE722";
                case "code": return "\uE943";
                case "screen_record": return "\uE714"; // Video camera
                case "screenshot": return "\uE722"; // Camera
                case "home_screen": return "\uE80F"; // Home
                case "close_all_apps": return "\uE711"; // Close X
                case "pc_shutdown": return "\uE7E8"; // Power icon
                case "pc_sleep": return "\uE708"; // Moon/sleep icon
                case "pc_lock": return "\uE72E"; // Padlock lock icon
                case "pc_restart": return "\uE777";
                case "wifi": return "\uE701"; // WiFi icon
                case "wifi_off": return "\uE701"; 
                case "bluetooth": return "\uE702"; // Bluetooth icon
                case "bluetooth_off": return "\uE702";
                case "rocket": return "\uF133";
                case "perf_cpu": return "\uE9D9"; // Chip / CPU
                case "perf_gpu": return "\uE7F1"; // Display/Video/GPU
                case "perf_ram": return "\uE9A9"; // RAM/Memory chip
                case "perf_temp": return "\u2103"; // Celsius degree symbol (℃)
                case "perf_wifi": return "\uEC3B"; // Speedometer / WiFi Speed
                case "keyboard":
                case "hotkey": return "\uE765"; // Keyboard icon
                default: return "\uE8A9";
            }
        }

        private string GetGlyphForIcon(string? iconName)
        {
            return GetGlyphForIconStatic(iconName);
        }

        private LinearGradientBrush GetGradientBrushFromHex(string hex)
        {
            try
            {
                var baseColor = (Color)ColorConverter.ConvertFromString(hex);
                var darkerColor = Color.FromRgb(
                    (byte)Math.Max(0, baseColor.R * 0.75),
                    (byte)Math.Max(0, baseColor.G * 0.75),
                    (byte)Math.Max(0, baseColor.B * 0.75)
                );
                return new LinearGradientBrush(baseColor, darkerColor, new Point(0, 0), new Point(1, 1));
            }
            catch
            {
                var start = (Color)ColorConverter.ConvertFromString("#6366F1");
                var end = (Color)ColorConverter.ConvertFromString("#4F46E5");
                return new LinearGradientBrush(start, end, new Point(0, 0), new Point(1, 1));
            }
        }        private void LoadActionDetails()
        {
            string actionType = _selectedButton != null ? _selectedButton.ActionType : _activeCategoryTab;
            string actionData = _selectedButton != null ? (_selectedButton.ActionData ?? "") : "";
            string iconVal = _selectedButton != null ? (_selectedButton.Icon ?? "") : "";
            string titleVal = _selectedButton != null ? (_selectedButton.Title ?? "") : "";

            // Reset search inputs & close search popup
            if (TxtUrlSearch != null) TxtUrlSearch.Text = "";
            if (TxtAppSearch != null) TxtAppSearch.Text = "";
            if (_urlSearchPopup != null) _urlSearchPopup.IsOpen = false;

            // Show/hide subpanels
            PanelActionParameter.Visibility = Visibility.Visible;
            PanelProfileActionLayout.Visibility = Visibility.Collapsed;
            ScrollSystemActions.Visibility = Visibility.Collapsed;
            if (ScrollHotkeyActions != null) ScrollHotkeyActions.Visibility = Visibility.Collapsed;
            TxtActionData.Visibility = Visibility.Collapsed;
            if (ScrollUrlLinks != null) ScrollUrlLinks.Visibility = Visibility.Collapsed;
            if (ListInstalledApps != null) ListInstalledApps.Visibility = Visibility.Collapsed;
            if (PanelAppSearch != null) PanelAppSearch.Visibility = Visibility.Collapsed;
            if (PanelUrlSearch != null) PanelUrlSearch.Visibility = Visibility.Collapsed;
            
            if (actionType.Equals("System", StringComparison.OrdinalIgnoreCase))
            {
                if (BorderActionSubTabs != null) BorderActionSubTabs.Visibility = Visibility.Visible;
                if (BtnActionTabSelect != null) BtnActionTabSelect.Content = "Select System Control";
                ScrollSystemActions.Visibility = Visibility.Visible;
                LblActionParameter.Text = "Select System Command";

                // Populate system action items grouped by category
                var systemActions = new List<SystemActionItem>
                {
                    // Media
                    new SystemActionItem { Category = "Media", ActionId = "media_play_pause", Label = "Play/Pause", Glyph = GetGlyphForIcon("media_play") },
                    new SystemActionItem { Category = "Media", ActionId = "media_next", Label = "Next", Glyph = GetGlyphForIcon("media_next") },
                    new SystemActionItem { Category = "Media", ActionId = "media_prev", Label = "Previous", Glyph = GetGlyphForIcon("media_prev") },
                    new SystemActionItem { Category = "Media", ActionId = "media_forward_10", Label = "Skip 10s", Glyph = GetGlyphForIcon("media_forward_10") },
                    new SystemActionItem { Category = "Media", ActionId = "media_backward_10", Label = "Back 10s", Glyph = GetGlyphForIcon("media_backward_10") },
                    new SystemActionItem { Category = "Media", ActionId = "volume_up", Label = "Vol Up", Glyph = GetGlyphForIcon("volume_up") },
                    new SystemActionItem { Category = "Media", ActionId = "volume_down", Label = "Vol Down", Glyph = GetGlyphForIcon("volume_down") },
                    new SystemActionItem { Category = "Media", ActionId = "volume_mute", Label = "Mute", Glyph = GetGlyphForIcon("volume_mute") },

                    // System
                    new SystemActionItem { Category = "System", ActionId = "pc_lock", Label = "Lock PC", Glyph = GetGlyphForIcon("pc_lock") },
                    new SystemActionItem { Category = "System", ActionId = "pc_shutdown", Label = "Power Off", Glyph = GetGlyphForIcon("pc_shutdown") },
                    new SystemActionItem { Category = "System", ActionId = "pc_restart", Label = "Restart PC", Glyph = GetGlyphForIcon("pc_restart") },
                    new SystemActionItem { Category = "System", ActionId = "pc_sleep", Label = "Hibernate PC", Glyph = GetGlyphForIcon("pc_sleep") },
                    new SystemActionItem { Category = "System", ActionId = "home_screen", Label = "Home Screen", Glyph = GetGlyphForIcon("home_screen") },
                    new SystemActionItem { Category = "System", ActionId = "close_all_apps", Label = "Close All Apps", Glyph = GetGlyphForIcon("close_all_apps") },

                    // Presentation Mode
                    new SystemActionItem { Category = "Presentation Mode", ActionId = "presentation_mode", Label = "Presentation", Glyph = GetGlyphForIcon("presentation_mode") },

                    // Display
                    new SystemActionItem { Category = "Display", ActionId = "brightness_up", Label = "Bright Up", Glyph = GetGlyphForIcon("brightness_up") },
                    new SystemActionItem { Category = "Display", ActionId = "brightness_down", Label = "Bright Down", Glyph = GetGlyphForIcon("brightness_down") },

                    // Connectivity
                    new SystemActionItem { Category = "Connectivity", ActionId = "wifi_toggle", Label = "Toggle Wi-Fi", Glyph = GetGlyphForIcon("wifi") },
                    new SystemActionItem { Category = "Connectivity", ActionId = "bluetooth_toggle", Label = "Toggle Bluetooth", Glyph = GetGlyphForIcon("bluetooth") },

                    // Audio & Input
                    new SystemActionItem { Category = "Audio & Input", ActionId = "mic_toggle", Label = "Mic", Glyph = GetGlyphForIcon("mic") },
                    new SystemActionItem { Category = "Audio & Input", ActionId = "screen_record", Label = "Screen Record", Glyph = GetGlyphForIcon("screen_record") },
                    new SystemActionItem { Category = "Audio & Input", ActionId = "screenshot", Label = "Screenshot", Glyph = GetGlyphForIcon("screenshot") },

                    // Monitoring
                    new SystemActionItem { Category = "Monitoring", ActionId = "perf_cpu", Label = "CPU Usage", Glyph = GetGlyphForIcon("perf_cpu") },
                    new SystemActionItem { Category = "Monitoring", ActionId = "perf_gpu", Label = "GPU Usage", Glyph = GetGlyphForIcon("perf_gpu") },
                    new SystemActionItem { Category = "Monitoring", ActionId = "perf_ram", Label = "RAM Usage", Glyph = GetGlyphForIcon("perf_ram") },
                    new SystemActionItem { Category = "Monitoring", ActionId = "perf_temp", Label = "PC Temp", Glyph = GetGlyphForIcon("perf_temp") },
                    new SystemActionItem { Category = "Monitoring", ActionId = "perf_wifi", Label = "WiFi Speed", Glyph = GetGlyphForIcon("perf_wifi") },
                };

                BuildSystemActionSections(systemActions, actionData);
                SelectGeneralActionTab("ActionSelect");
            }
            else if (actionType.Equals("App", StringComparison.OrdinalIgnoreCase))
            {
                if (BorderActionSubTabs != null) BorderActionSubTabs.Visibility = Visibility.Visible;
                if (BtnActionTabSelect != null) BtnActionTabSelect.Content = "Select Application";
                if (PanelAppSearch != null) PanelAppSearch.Visibility = Visibility.Visible;
                if (ListInstalledApps != null)
                {
                    ListInstalledApps.Visibility = Visibility.Visible;
                    LblActionParameter.Text = "Select Application from List";

                    // Select matching app in list
                    _isUpdatingUi = true;
                    try
                    {
                        ListInstalledApps.SelectedIndex = -1;
                        if (ListInstalledApps.ItemsSource is List<InstalledApp> apps && !string.IsNullOrEmpty(actionData))
                        {
                            int idx = apps.FindIndex(a => a.ShortcutPath.Equals(actionData, StringComparison.OrdinalIgnoreCase));
                            if (idx >= 0)
                            {
                                ListInstalledApps.SelectedIndex = idx;
                                ListInstalledApps.ScrollIntoView(apps[idx]);
                            }
                        }
                    }
                    finally
                    {
                        _isUpdatingUi = false;
                    }
                }
                SelectGeneralActionTab("ActionSelect");
            }
            else if (actionType.Equals("Profile", StringComparison.OrdinalIgnoreCase))
            {
                PanelActionParameter.Visibility = Visibility.Collapsed;
                PanelProfileActionLayout.Visibility = Visibility.Visible;
                
                _isUpdatingUi = true;
                try
                {
                    ListActionProfiles.ItemsSource = null;
                    ListActionProfiles.ItemsSource = ConfigManager.Current.Profiles;
                    ListActionProfiles.SelectedIndex = -1;
                    // Select matching profile
                    int idx = ConfigManager.Current.Profiles.FindIndex(p => p.Id == actionData);
                    if (idx >= 0)
                    {
                        ListActionProfiles.SelectedIndex = idx;
                    }
                    else if (_selectedButton != null)
                    {
                        // Handle shared/imported profile button linked to missing profile ID
                        var activeProfile = ConfigManager.Current.Profiles.Find(p => p.Id == ConfigManager.Current.CurrentProfileId) ?? (ConfigManager.Current.Profiles.Count > 0 ? ConfigManager.Current.Profiles[0] : null);
                        string fallbackId = activeProfile?.Id ?? (ConfigManager.Current.Profiles.Count > 0 ? ConfigManager.Current.Profiles[0].Id : "");

                        if (!string.IsNullOrEmpty(fallbackId))
                        {
                            _selectedButton.ActionData = fallbackId;
                            int fallbackIdx = ConfigManager.Current.Profiles.FindIndex(p => p.Id == fallbackId);
                            if (fallbackIdx >= 0)
                            {
                                ListActionProfiles.SelectedIndex = fallbackIdx;
                            }
                        }
                    }

                    if (ListInstalledApps != null && ListProfileIconApps != null)
                    {
                        ListProfileIconApps.ItemsSource = ListInstalledApps.ItemsSource;
                    }

                    UpdateKeycapLivePreviews();

                    if (iconVal.Contains("|"))
                    {
                        SelectComboProfileItemByTag("Grid");
                        if (PanelProfileIconText != null) PanelProfileIconText.Visibility = Visibility.Collapsed;
                        if (PanelProfileIconFile != null) PanelProfileIconFile.Visibility = Visibility.Collapsed;
                        if (PanelProfileIconReicon != null) PanelProfileIconReicon.Visibility = Visibility.Collapsed;
                        if (PanelProfileIconGrid != null) PanelProfileIconGrid.Visibility = Visibility.Visible;
                    }
                    else if (iconVal.StartsWith("svgpath:") || ReiconService.Search("").Any(i => i.Id.Equals(iconVal, StringComparison.OrdinalIgnoreCase)))
                    {
                        SelectComboProfileItemByTag("Reicon");
                        if (PanelProfileIconText != null) PanelProfileIconText.Visibility = Visibility.Collapsed;
                        if (PanelProfileIconFile != null) PanelProfileIconFile.Visibility = Visibility.Collapsed;
                        if (PanelProfileIconGrid != null) PanelProfileIconGrid.Visibility = Visibility.Collapsed;
                        if (PanelProfileIconReicon != null) PanelProfileIconReicon.Visibility = Visibility.Visible;
                        PopulateReiconCategories();
                        RefreshReiconGrid();
                    }
                    else if (iconVal.StartsWith("text:"))
                    {
                        SelectComboProfileItemByTag("Text");
                        if (TxtProfileIconText != null) TxtProfileIconText.Text = iconVal.Substring(5);
                        if (PanelProfileIconText != null) PanelProfileIconText.Visibility = Visibility.Visible;
                        if (PanelProfileIconFile != null) PanelProfileIconFile.Visibility = Visibility.Collapsed;
                        if (PanelProfileIconReicon != null) PanelProfileIconReicon.Visibility = Visibility.Collapsed;
                        if (PanelProfileIconGrid != null) PanelProfileIconGrid.Visibility = Visibility.Collapsed;
                    }
                    else if (iconVal.StartsWith("data:"))
                    {
                        SelectComboProfileItemByTag("File");
                        if (TxtProfileIconFilePath != null) TxtProfileIconFilePath.Text = "(custom image)";
                        if (PanelProfileIconFile != null) PanelProfileIconFile.Visibility = Visibility.Visible;
                        if (PanelProfileIconText != null) PanelProfileIconText.Visibility = Visibility.Collapsed;
                        if (PanelProfileIconReicon != null) PanelProfileIconReicon.Visibility = Visibility.Collapsed;
                        if (PanelProfileIconGrid != null) PanelProfileIconGrid.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        SelectComboProfileItemByTag("Grid");
                        if (PanelProfileIconText != null) PanelProfileIconText.Visibility = Visibility.Collapsed;
                        if (PanelProfileIconFile != null) PanelProfileIconFile.Visibility = Visibility.Collapsed;
                        if (PanelProfileIconReicon != null) PanelProfileIconReicon.Visibility = Visibility.Collapsed;
                        if (PanelProfileIconGrid != null) PanelProfileIconGrid.Visibility = Visibility.Visible;
                    }

                    SelectProfileTab("SelectConfig");
                }
                finally
                {
                    _isUpdatingUi = false;
                }
            }
            else
            {
                if (actionType.Equals("URL", StringComparison.OrdinalIgnoreCase))
                {
                    if (BorderActionSubTabs != null) BorderActionSubTabs.Visibility = Visibility.Visible;
                    if (BtnActionTabSelect != null) BtnActionTabSelect.Content = "Select Website Link";
                    if (PanelUrlSearch != null) PanelUrlSearch.Visibility = Visibility.Visible;
                    LblActionParameter.Text = "Website Link (URL)";
                    if (ScrollUrlLinks != null)
                    {
                        ScrollUrlLinks.Visibility = Visibility.Visible;
                        RefreshUrlLinksLayout();
                    }
                    SelectGeneralActionTab("ActionSelect");
                }
                else if (actionType.Equals("Hotkey", StringComparison.OrdinalIgnoreCase))
                {
                    if (BorderActionSubTabs != null) BorderActionSubTabs.Visibility = Visibility.Collapsed;
                    if (ScrollHotkeyActions != null) ScrollHotkeyActions.Visibility = Visibility.Visible;
                    LblActionParameter.Text = "Configure Hotkey / Key Combination";
                    SelectGeneralActionTab("ActionSelect");

                    PopulateHotkeyRecorderFromData(actionData);
                    BuildHotkeyActionSections(actionData);

                    var savedItem = ConfigManager.Current?.SavedCustomHotkeys?.FirstOrDefault(h => 
                        h.ActionId.Equals(actionData, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(titleVal) && h.Label.Equals(titleVal, StringComparison.OrdinalIgnoreCase)));

                    if (savedItem != null)
                    {
                        _editingSavedActionId = savedItem.ActionId;
                        if (TxtHotkeyName != null) TxtHotkeyName.Text = savedItem.Label;
                        _customHotkeyConfiguredIcon = savedItem.Glyph;
                    }
                    else
                    {
                        _editingSavedActionId = null;
                        if (TxtHotkeyName != null) TxtHotkeyName.Text = titleVal;
                        _customHotkeyConfiguredIcon = (!string.IsNullOrEmpty(iconVal) && iconVal != "default" && iconVal != "keyboard") ? iconVal : null;
                    }
                    UpdateHotkeyIconPreview();
                }
                else
                {
                    if (BtnActionTabSelect != null) BtnActionTabSelect.Content = "Select Action";
                    TxtActionData.Visibility = Visibility.Visible;
                    TxtActionData.Text = actionData;
                    LblActionParameter.Text = "Action Detail";
                }
                SelectGeneralActionTab("ActionSelect");
            }
        }

        // Saving edits triggers Sync
        private void TriggerConfigSync()
        {
            if (_isUpdatingUi) return;

            if (!EnsureCurrentProfileUnlocked()) return;

            if (_selectedButton != null)
            {
                if (_selectedButton.ActionType.Equals("Profile", StringComparison.OrdinalIgnoreCase))
                {
                    if (ComboProfileButtonIconType != null && ComboProfileButtonIconType.SelectedItem is ComboBoxItem cbi && Equals(cbi.Tag, "Grid"))
                    {
                        _ = UpdateProfileButtonIconGridAsync(false);
                    }
                }
            }

            UpdateAllProfileGridIcons();

            ConfigManager.Save();
            
            _isUpdatingUi = true;
            try
            {
                RefreshGridPreview();
                RefreshProfilesList();
            }
            finally
            {
                _isUpdatingUi = false;
            }

            _server.SyncButtons();
            _server.SyncProfiles();
        }

        private string GetDefaultColorForType(string type)
        {
            switch (type?.ToLower())
            {
                case "app": return "#FFFFFF"; // Normal White
                case "url": return "#FFFFFF"; // Normal White
                case "macro": return "#FFFFFF"; // Normal White
                case "system": return "#FFFFFF"; // Normal White
                case "profile": return "#FFFFFF"; // Normal White
                case "hotkey": return "#FFFFFF"; // Normal White
                default: return "#FFFFFF"; // Normal White
            }
        }

        private string GetDefaultIconForType(string type, string actionData = "")
        {
            switch (type?.ToLower())
            {
                case "app": return string.IsNullOrEmpty(actionData) ? "app_default" : "rocket";
                case "url": return "url";
                case "macro": return "folder";
                case "profile": return "folder";
                case "hotkey": return "keyboard";
                case "system":
                    if (!string.IsNullOrEmpty(actionData))
                    {
                        string dataLower = actionData.ToLower();
                        if (dataLower.Contains("volume"))
                        {
                            if (dataLower.Contains("mute")) return "volume_mute";
                            if (dataLower.Contains("down")) return "volume_down";
                            return "volume_up";
                        }
                        if (dataLower.Contains("brightness"))
                        {
                            if (dataLower.Contains("down")) return "brightness_down";
                            return "brightness_up";
                        }
                        if (dataLower.Contains("media"))
                        {
                            if (dataLower.Contains("next")) return "media_next";
                            if (dataLower.Contains("prev")) return "media_prev";
                            if (dataLower.Contains("forward")) return "media_forward_10";
                            if (dataLower.Contains("backward")) return "media_backward_10";
                            return _isMediaPlaying ? "media_play" : "media_pause";
                        }
                        if (dataLower.Contains("pc_shutdown")) return "pc_shutdown";
                        if (dataLower.Contains("pc_sleep")) return "pc_sleep";
                        if (dataLower.Contains("pc_lock")) return "pc_lock";
                        if (dataLower.Contains("pc_restart")) return "pc_restart";
                        if (dataLower.Contains("screen_record")) return "screen_record";
                        if (dataLower.Contains("screenshot")) return "screenshot";
                        if (dataLower.Contains("home_screen")) return "home_screen";
                        if (dataLower.Contains("close_all_apps")) return "close_all_apps";
                        if (dataLower.Contains("perf_cpu")) return "perf_cpu";
                        if (dataLower.Contains("perf_gpu")) return "perf_gpu";
                        if (dataLower.Contains("perf_ram")) return "perf_ram";
                        if (dataLower.Contains("perf_temp")) return "perf_temp";
                        if (dataLower.Contains("perf_wifi")) return "perf_wifi";
                        if (dataLower.Contains("presentation")) return "presentation_mode";
                        if (dataLower.Contains("wifi")) return _isWifiOn ? "wifi" : "wifi_off";
                        if (dataLower.Contains("bluetooth")) return _isBluetoothOn ? "bluetooth" : "bluetooth_off";
                        if (dataLower.Contains("mic")) return "mic";
                        if (dataLower.Contains("camera")) return "camera";
                    }
                    return "settings";
                default: return "default";
            }
        }

        private string GetDefaultTitleForType(string type)
        {
            switch (type?.ToLower())
            {
                case "app": return "Select App";
                case "url": return "Open URL";
                case "macro": return "Run Macro";
                case "system": return "System Cmd";
                case "profile": return "Switch Profile";
                case "hotkey": return "Hot Key";
                default: return "New Button";
            }
        }

        private string GetSystemActionTitle(string action)
        {
            if (string.IsNullOrEmpty(action)) return "System Cmd";
            switch (action.ToLower())
            {
                case "volume_up": return "Volume Up";
                case "volume_down": return "Volume Down";
                case "volume_mute": return "Mute Volume";
                case "media_play_pause": return "Play/Pause";
                case "media_next": return "Next Track";
                case "media_prev": return "Prev Track";
                case "media_forward_10": return "Skip 10s";
                case "media_backward_10": return "Back 10s";
                case "brightness_up": return "Brightness +";
                case "brightness_down": return "Brightness -";
                case "mic_toggle": return "Toggle Mic";
                case "camera_toggle": return "Toggle Cam";
                case "pc_shutdown": return "Power Off";
                case "pc_sleep": return "Hibernate PC";
                case "pc_lock": return "Lock PC";
                case "pc_restart": return "Restart PC";
                case "wifi_toggle": return "Toggle Wi-Fi";
                case "bluetooth_toggle": return "Toggle Bluetooth";
                case "screen_record": return "Screen Recording";
                case "screenshot": return "Screenshot";
                case "home_screen": return "Home Screen";
                case "close_all_apps": return "Close All Apps";
                case "perf_cpu": return "CPU Usage";
                case "perf_gpu": return "GPU Usage";
                case "perf_ram": return "RAM Usage";
                case "perf_temp": return "PC Temp";
                case "perf_wifi": return "WiFi Speed";
                default: return action;
            }
        }

        [ComImport]
        [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler([In] IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, [Out] out IntPtr ppv);
            void GetParent([Out, MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
            void GetDisplayName([In] uint sigdnName, [Out, MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes([In] uint sfgaoMask, [Out] out uint psfgaoAttribs);
            void Compare([In] IShellItem psi, [In] uint hint, [Out] out int piOrder);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private static readonly Guid IShellItemGuid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            [In] IntPtr pbc,
            [In] ref Guid riid,
            [Out, MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(
            IntPtr pcb, // PIDL
            uint dwFileAttributes,
            ref SHFILEINFO psfi,
            uint cbFileInfo,
            uint uFlags);

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

        private static HashSet<string> _unlockedProfilesForSession = new HashSet<string>();
        public static HashSet<string> UnlockedProfilesForSession => _unlockedProfilesForSession;

        public static bool IsProfileUnlockedForSession(string profileId)
        {
            return !string.IsNullOrEmpty(profileId) && _unlockedProfilesForSession.Contains(profileId);
        }

        private Profile? _pendingUnlockProfileTarget = null;
        private Action? _pendingUnlockAction = null;

        public void PromptProfilePinUnlock(Profile profile, Action? onUnlockAction = null)
        {
            _pendingUnlockProfileTarget = profile;
            _pendingUnlockAction = onUnlockAction;
            if (string.IsNullOrEmpty(ConfigManager.Current.ProfilePin))
            {
                MessageBox.Show("This profile is locked, but no Profile PIN has been set yet. Please set a Profile PIN in Settings.", "PIN Required", MessageBoxButton.OK, MessageBoxImage.Information);
                BtnOpenSettings_Click(this, new RoutedEventArgs());
                return;
            }

            if (TxtProfilePinModalTitle != null) TxtProfilePinModalTitle.Text = $"Unlock '{profile.Name}' to Access";
            if (TxtEntryProfilePin != null) TxtEntryProfilePin.Password = "";
            if (TxtProfilePinError != null) TxtProfilePinError.Visibility = Visibility.Collapsed;
            if (ModalProfilePin != null) ModalProfilePin.Visibility = Visibility.Visible;
            TxtEntryProfilePin?.Focus();
        }

        private bool EnsureCurrentProfileUnlocked(Action? actionOnSuccess = null)
        {
            var currentProfile = ConfigManager.Current.Profiles.Find(p => p.Id == ConfigManager.Current.CurrentProfileId);
            if (currentProfile != null && currentProfile.IsLocked)
            {
                PromptProfilePinUnlock(currentProfile, actionOnSuccess);
                return false;
            }
            return true;
        }

        private void ListProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi) return;

            var selectedProfile = ListProfiles.SelectedItem as Profile;
            if (selectedProfile != null)
            {
                if (selectedProfile.Id == ConfigManager.Current.CurrentProfileId) return;

                if (selectedProfile.IsLocked)
                {
                    _isUpdatingUi = true;
                    try
                    {
                        var currentProf = ConfigManager.Current.Profiles.Find(p => p.Id == ConfigManager.Current.CurrentProfileId);
                        ListProfiles.SelectedItem = currentProf;
                    }
                    finally
                    {
                        _isUpdatingUi = false;
                    }

                    PromptProfilePinUnlock(selectedProfile, () => OnProfileChangeRequestedDirect(selectedProfile.Id));
                    return;
                }

                OnProfileChangeRequestedDirect(selectedProfile.Id);
            }
        }

        private void OnProfileChangeRequestedDirect(string profileId)
        {
            var profile = ConfigManager.Current.Profiles.Find(p => p.Id == profileId);
            if (profile != null)
            {
                ConfigManager.Current.CurrentProfileId = profile.Id;
                ConfigManager.Save();

                _isUpdatingUi = true;
                try
                {
                    ListProfiles.SelectedItem = profile;
                }
                finally
                {
                    _isUpdatingUi = false;
                }

                SelectShortcutButton(null);
                _selectedBulkButtons.Clear();
                RefreshGridPreview();
                _server.SyncButtons();
                _server.SyncProfiles();
            }
        }

        private void BtnMoveProfileUp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Profile profile)
            {
                MoveProfile(profile, -1);
            }
        }

        private void BtnMoveProfileDown_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Profile profile)
            {
                MoveProfile(profile, 1);
            }
        }

        private void MenuItemMoveProfileUp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.MenuItem item && item.DataContext is Profile profile)
            {
                MoveProfile(profile, -1);
            }
        }

        private void MenuItemMoveProfileDown_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.MenuItem item && item.DataContext is Profile profile)
            {
                MoveProfile(profile, 1);
            }
        }

        private void MoveProfile(Profile profile, int direction)
        {
            if (profile == null || ConfigManager.Current == null || ConfigManager.Current.Profiles == null) return;

            var profiles = ConfigManager.Current.Profiles;
            int currentIndex = profiles.IndexOf(profile);
            if (currentIndex < 0) return;

            int newIndex = currentIndex + direction;
            if (newIndex < 0 || newIndex >= profiles.Count) return;

            // Swap profiles in list
            profiles.RemoveAt(currentIndex);
            profiles.Insert(newIndex, profile);

            ConfigManager.Save();
            RefreshProfilesList();
            _server.SyncProfiles();
        }

        public void OnProfileChangeRequested(string profileId)
        {
            Dispatcher.Invoke(() =>
            {
                var profile = ConfigManager.Current.Profiles.Find(p => p.Id == profileId);
                if (profile != null)
                {
                    if (profile.IsLocked)
                    {
                        PromptProfilePinUnlock(profile, () => OnProfileChangeRequestedDirect(profileId));
                        return;
                    }

                    OnProfileChangeRequestedDirect(profileId);
                }
                else
                {
                    // Target profile ID is missing (imported shared profile button)
                    this.Show();
                    this.WindowState = WindowState.Normal;
                    this.Activate();

                    var missingBtn = ConfigManager.CurrentButtons.Find(b => b.ActionType.Equals("Profile", StringComparison.OrdinalIgnoreCase) && b.ActionData == profileId);
                    if (missingBtn == null)
                    {
                        missingBtn = ConfigManager.CurrentButtons.Find(b => b.ActionType.Equals("Profile", StringComparison.OrdinalIgnoreCase));
                    }

                    if (missingBtn != null)
                    {
                        SelectShortcutButton(missingBtn);
                    }
                    else
                    {
                        HideSidebarSettings();
                        if (GridSidebarProfiles != null) GridSidebarProfiles.Visibility = Visibility.Visible;
                    }
                }
            });
        }   [DllImport("shell32.dll")]
        private static extern int SHGetIDListFromObject(
            [MarshalAs(UnmanagedType.IUnknown)] object punk,
            out IntPtr ppidl);

        [DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static System.Windows.Media.ImageSource? GetShellIcon(string parsingName)
        {
            IntPtr pidl = IntPtr.Zero;
            IntPtr hIcon = IntPtr.Zero;
            try
            {
                IShellItem shellItem;
                Guid riid = IShellItemGuid;
                SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref riid, out shellItem);

                int hr = SHGetIDListFromObject(shellItem, out pidl);
                if (hr == 0 && pidl != IntPtr.Zero)
                {
                    SHFILEINFO sfi = new SHFILEINFO();
                    // SHGFI_PIDL = 0x8, SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0
                    IntPtr hResult = SHGetFileInfo(pidl, 0, ref sfi, (uint)Marshal.SizeOf(sfi), 0x8 | 0x100 | 0x0);
                    if (sfi.hIcon != IntPtr.Zero)
                    {
                        hIcon = sfi.hIcon;
                        var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                            hIcon,
                            System.Windows.Int32Rect.Empty,
                            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                        
                        bitmapSource.Freeze();
                        return bitmapSource;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting shell icon for {parsingName}: {ex.Message}");
            }
            finally
            {
                if (pidl != IntPtr.Zero)
                {
                    ILFree(pidl);
                }
                if (hIcon != IntPtr.Zero)
                {
                    DestroyIcon(hIcon);
                }
            }
            return null;
        }

        private static string? ImageSourceToBase64Png(System.Windows.Media.ImageSource? source)
        {
            if (source == null) return null;
            try
            {
                var bitmapSource = source as System.Windows.Media.Imaging.BitmapSource;
                if (bitmapSource == null) return null;

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmapSource));
                using (var ms = new MemoryStream())
                {
                    encoder.Save(ms);
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
            catch
            {
                return null;
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Windows.Media.ImageSource> _imageBase64Cache = new();

        private static System.Windows.Media.ImageSource? Base64PngToImageSource(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64)) return null;

            string cacheKey = base64.Length > 100 ? base64.Substring(0, 100) + "_" + base64.Length : base64;
            if (_imageBase64Cache.TryGetValue(cacheKey, out var cachedImg))
            {
                return cachedImg;
            }

            try
            {
                string rawB64 = base64;
                if (rawB64.Contains(","))
                {
                    rawB64 = rawB64.Substring(rawB64.IndexOf(",") + 1);
                }
                else if (rawB64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    rawB64 = rawB64.Substring(5);
                }

                byte[] bytes = Convert.FromBase64String(rawB64.Trim());
                using (var ms = new MemoryStream(bytes))
                {
                    var image = new System.Windows.Media.Imaging.BitmapImage();
                    image.BeginInit();
                    image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    image.StreamSource = ms;
                    image.EndInit();
                    image.Freeze();

                    _imageBase64Cache[cacheKey] = image;
                    return image;
                }
            }
            catch
            {
                return null;
            }
        }

        private async void InitializeRadioMonitoring()
        {
            try
            {
                var accessLevel = await Radio.RequestAccessAsync();
                if (accessLevel == RadioAccessStatus.Allowed)
                {
                    var radios = await Radio.GetRadiosAsync();
                    foreach (var radio in radios)
                    {
                        _radiosList.Add(radio);
                        radio.StateChanged += Radio_StateChanged;
                    }
                    UpdateRadioStates();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize radio monitoring: {ex.Message}");
            }
        }

        private void Radio_StateChanged(Radio sender, object args)
        {
            UpdateRadioStates();
        }

        private void UpdateRadioStates()
        {
            bool wifiState = false;
            bool bluetoothState = false;

            foreach (var radio in _radiosList)
            {
                if (radio.Kind == RadioKind.WiFi)
                {
                    wifiState = radio.State == RadioState.On;
                }
                else if (radio.Kind == RadioKind.Bluetooth)
                {
                    bluetoothState = radio.State == RadioState.On;
                }
            }

            bool wifiChanged = _isWifiOn != wifiState;
            bool bluetoothChanged = _isBluetoothOn != bluetoothState;

            _isWifiOn = wifiState;
            _isBluetoothOn = bluetoothState;

            if (wifiChanged || bluetoothChanged)
            {
                Dispatcher.Invoke(() =>
                {
                    bool changed = false;

                    foreach (var btn in ConfigManager.CurrentButtons)
                    {
                        if (btn.ActionType.Equals("System", StringComparison.OrdinalIgnoreCase))
                        {
                            if (btn.ActionData.Equals("wifi_toggle", StringComparison.OrdinalIgnoreCase))
                            {
                                string newIcon = wifiState ? "wifi" : "wifi_off";
                                if (btn.Icon != newIcon)
                                {
                                    btn.Icon = newIcon;
                                    changed = true;
                                }
                            }
                            else if (btn.ActionData.Equals("bluetooth_toggle", StringComparison.OrdinalIgnoreCase))
                            {
                                string newIcon = bluetoothState ? "bluetooth" : "bluetooth_off";
                                if (btn.Icon != newIcon)
                                {
                                    btn.Icon = newIcon;
                                    changed = true;
                                }
                            }
                        }
                    }

                    if (changed)
                    {
                        RefreshGridPreview();
                        _server.SyncButtons();
                    }
                });
            }
        }

        private async void InitializeMediaMonitoring()
        {
            try
            {
                _mediaSessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_mediaSessionManager != null)
                {
                    _mediaSessionManager.CurrentSessionChanged += MediaSessionManager_CurrentSessionChanged;
                    UpdateMediaPlaybackState();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize media monitoring: {ex.Message}");
            }
        }

        private void MediaSessionManager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            UpdateMediaPlaybackState();
        }

        private void UpdateMediaPlaybackState()
        {
            if (_mediaSessionManager == null) return;

            try
            {
                var session = _mediaSessionManager.GetCurrentSession();
                if (session != null)
                {
                    session.PlaybackInfoChanged += Session_PlaybackInfoChanged;
                    var info = session.GetPlaybackInfo();
                    bool isPlaying = info != null && info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    UpdateMediaPlayingState(isPlaying);
                }
                else
                {
                    UpdateMediaPlayingState(false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating media state: {ex.Message}");
            }
        }

        private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            try
            {
                var info = sender.GetPlaybackInfo();
                bool isPlaying = info != null && info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                UpdateMediaPlayingState(isPlaying);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in PlaybackInfoChanged: {ex.Message}");
            }
        }

        private void UpdateMediaPlayingState(bool isPlaying)
        {
            if (_isMediaPlaying == isPlaying) return;
            _isMediaPlaying = isPlaying;

            Dispatcher.Invoke(() =>
            {
                bool changed = false;
                foreach (var btn in ConfigManager.CurrentButtons)
                {
                    if (btn.ActionType.Equals("System", StringComparison.OrdinalIgnoreCase) &&
                        btn.ActionData.Equals("media_play_pause", StringComparison.OrdinalIgnoreCase))
                    {
                        string newIcon = isPlaying ? "media_play" : "media_pause";
                        if (btn.Icon != newIcon)
                        {
                            btn.Icon = newIcon;
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    RefreshGridPreview();
                    _server.SyncButtons();
                }
            });
        }

        private static string GetAppsCachePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = System.IO.Path.Combine(appData, "SwiftDock");
            Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, "installed_apps_cache.json");
        }

        private void LoadCachedInstalledAppsIfAny()
        {
            try
            {
                string cacheFile = GetAppsCachePath();
                if (File.Exists(cacheFile))
                {
                    string json = File.ReadAllText(cacheFile);
                    var cachedApps = System.Text.Json.JsonSerializer.Deserialize<List<CachedInstalledApp>>(json);
                    if (cachedApps != null && cachedApps.Count > 0)
                    {
                        var appsList = new List<InstalledApp>();
                        foreach (var c in cachedApps)
                        {
                            appsList.Add(new InstalledApp
                            {
                                DisplayName = c.DisplayName,
                                ShortcutPath = c.ShortcutPath,
                                Icon = Base64PngToImageSource(c.IconBase64)
                            });
                        }
                        _allInstalledApps = appsList;
                        ListInstalledApps.ItemsSource = appsList;
                    }
                }
            }
            catch { }
        }

        private void SaveInstalledAppsCache(List<InstalledApp> apps)
        {
            try
            {
                var cachedList = new List<CachedInstalledApp>();
                foreach (var app in apps)
                {
                    string? iconB64 = ImageSourceToBase64Png(app.Icon);
                    cachedList.Add(new CachedInstalledApp
                    {
                        DisplayName = app.DisplayName,
                        ShortcutPath = app.ShortcutPath,
                        IconBase64 = iconB64 ?? ""
                    });
                }
                string json = System.Text.Json.JsonSerializer.Serialize(cachedList);
                File.WriteAllText(GetAppsCachePath(), json);
            }
            catch { }
        }

        private bool _isSplashFading = false;

        private void TriggerSplashClickAnimation()
        {
            Dispatcher.Invoke(() =>
            {
                // 1. Tactile Keycap Press-Down Animation (Scale 1.0 -> 0.88 -> 1.0)
                if (ScaleSplashLogo != null)
                {
                    var scaleDownX = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.88, TimeSpan.FromMilliseconds(130))
                    {
                        EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
                    };
                    var scaleDownY = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.88, TimeSpan.FromMilliseconds(130))
                    {
                        EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
                    };

                    scaleDownX.Completed += (s, e) =>
                    {
                        var scaleUpX = new System.Windows.Media.Animation.DoubleAnimation(0.88, 1.0, TimeSpan.FromMilliseconds(240))
                        {
                            EasingFunction = new System.Windows.Media.Animation.BackEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Amplitude = 0.5 }
                        };
                        var scaleUpY = new System.Windows.Media.Animation.DoubleAnimation(0.88, 1.0, TimeSpan.FromMilliseconds(240))
                        {
                            EasingFunction = new System.Windows.Media.Animation.BackEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Amplitude = 0.5 }
                        };

                        ScaleSplashLogo.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUpX);
                        ScaleSplashLogo.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUpY);
                    };

                    ScaleSplashLogo.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDownX);
                    ScaleSplashLogo.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDownY);
                }

                // 2. Vector Dock Grid Cubes Become Visible After Click (Opacity 0.0 -> 1.0)
                if (PathDockGrid != null)
                {
                    var gridLightUpAnim = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(300))
                    {
                        EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                    };
                    PathDockGrid.BeginAnimation(UIElement.OpacityProperty, gridLightUpAnim);
                }

                // 3. Perimeter Shape Edge Glow Flare (Opacity 0.0 -> 0.85)
                if (EffectSplashGlow != null)
                {
                    var glowOpacityAnim = new System.Windows.Media.Animation.DoubleAnimation(0.0, 0.85, TimeSpan.FromMilliseconds(450))
                    {
                        EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                    };
                    EffectSplashGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, glowOpacityAnim);
                }
            });
        }

        private void HideSplashScreen()
        {
            if (_isSplashFading || GridSplashScreenOverlay == null) return;
            _isSplashFading = true;

            Dispatcher.InvokeAsync(async () =>
            {
                if (TxtSplashStatus != null) TxtSplashStatus.Text = "CALIBRATING SWITCH...";
                await Task.Delay(250);

                // Play automatic keypress animation & white radial glow flare
                TriggerSplashClickAnimation();

                await Task.Delay(130);
                if (TxtSplashStatus != null) TxtSplashStatus.Text = "CONNECTED";

                await Task.Delay(650);

                var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 1.0,
                    To = 0.0,
                    Duration = TimeSpan.FromMilliseconds(500),
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };

                fadeAnim.Completed += (s, e) =>
                {
                    GridSplashScreenOverlay.Visibility = Visibility.Collapsed;
                };

                GridSplashScreenOverlay.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            });
        }

        private void LoadInstalledAppsAsync()
        {
            LoadCachedInstalledAppsIfAny();

            Task.Run(() =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (TxtSplashStatus != null) TxtSplashStatus.Text = "SCANNING APPLICATIONS...";
                });

                var apps = new List<InstalledApp>();
                try
                {
                    Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                    if (shellType != null)
                    {
                        object? shell = Activator.CreateInstance(shellType);
                        if (shell != null)
                        {
                            object? folder = shellType.InvokeMember("NameSpace",
                                System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { "shell:AppsFolder" });
                            
                            if (folder != null)
                            {
                                object? items = folder.GetType().InvokeMember("Items",
                                    System.Reflection.BindingFlags.InvokeMethod, null, folder, null);
                                
                                if (items != null)
                                {
                                    int count = (int)(items.GetType().InvokeMember("Count",
                                        System.Reflection.BindingFlags.GetProperty, null, items, null) ?? 0);
                                    
                                    for (int i = 0; i < count; i++)
                                    {
                                        object? item = items.GetType().InvokeMember("Item",
                                            System.Reflection.BindingFlags.InvokeMethod, null, items, new object[] { i });
                                        
                                        if (item != null)
                                        {
                                            string? name = item.GetType().InvokeMember("Name",
                                                System.Reflection.BindingFlags.GetProperty, null, item, null) as string;
                                            string? path = item.GetType().InvokeMember("Path",
                                                System.Reflection.BindingFlags.GetProperty, null, item, null) as string;
                                            
                                            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) continue;

                                            string pathLower = path.ToLower();
                                            string nameLower = name.ToLower();

                                            if (pathLower.StartsWith("http://") || pathLower.StartsWith("https://") ||
                                                pathLower.EndsWith(".html") || pathLower.EndsWith(".htm") ||
                                                pathLower.EndsWith(".chm") || pathLower.EndsWith(".txt") ||
                                                pathLower.EndsWith(".pdf") || pathLower.EndsWith(".url") ||
                                                pathLower.Contains("microsoft.autogenerated") ||
                                                pathLower.Contains("uninstall") ||
                                                nameLower.Contains("uninstall") ||
                                                nameLower.Contains("readme") ||
                                                nameLower.Contains("read me") ||
                                                nameLower.Contains("help") ||
                                                nameLower.Contains("license") ||
                                                nameLower.Contains("documentation") ||
                                                nameLower.Contains("manual"))
                                            {
                                                continue;
                                            }

                                            string parsingName = path;
                                            if (!path.Contains(":") && !path.StartsWith("\\"))
                                            {
                                                parsingName = @"shell:AppsFolder\" + path;
                                            }

                                            if (apps.Exists(a => a.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                                            {
                                                continue;
                                            }

                                            apps.Add(new InstalledApp
                                            {
                                                DisplayName = name,
                                                ShortcutPath = parsingName,
                                                Icon = GetShellIcon(parsingName)
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Scan Desktop and Start Menu shortcuts to include Chrome, Edge, and other missing apps
                    var shortcutFolders = new[]
                    {
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
                    };

                    foreach (var folderPath in shortcutFolders)
                    {
                        SafeScanShortcuts(folderPath, apps);
                    }

                    apps.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

                    SaveInstalledAppsCache(apps);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error scanning AppsFolder: {ex.Message}");
                }

                Dispatcher.Invoke(() =>
                {
                    _allInstalledApps = apps;
                    ListInstalledApps.ItemsSource = apps;

                    if (_selectedButton != null && _selectedButton.ActionType.Equals("App", StringComparison.OrdinalIgnoreCase))
                    {
                        int idx = apps.FindIndex(a => a.ShortcutPath.Equals(_selectedButton.ActionData, StringComparison.OrdinalIgnoreCase));
                        if (idx >= 0)
                        {
                            _isUpdatingUi = true;
                            try
                            {
                                ListInstalledApps.SelectedIndex = idx;
                                ListInstalledApps.ScrollIntoView(apps[idx]);
                            }
                            finally
                            {
                                _isUpdatingUi = false;
                            }
                        }
                    }

                    if (TxtSplashStatus != null) TxtSplashStatus.Text = "READY";
                    HideSplashScreen();
                });
            });
        }

        private static void SafeScanShortcuts(string folderPath, List<InstalledApp> apps)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return;

            try
            {
                foreach (var file in Directory.GetFiles(folderPath, "*.lnk"))
                {
                    try
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        if (string.IsNullOrEmpty(name)) continue;

                        string nameLower = name.ToLower();
                        if (nameLower.Contains("uninstall") || 
                            nameLower.Contains("readme") || 
                            nameLower.Contains("read me") || 
                            nameLower.Contains("help") || 
                            nameLower.Contains("license") || 
                            nameLower.Contains("documentation") || 
                            nameLower.Contains("manual"))
                        {
                            continue;
                        }

                        if (apps.Exists(a => a.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        apps.Add(new InstalledApp
                        {
                            DisplayName = name,
                            ShortcutPath = file,
                            Icon = GetShellIcon(file)
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error processing shortcut {file}: {ex.Message}");
                    }
                }

                foreach (var subDir in Directory.GetDirectories(folderPath))
                {
                    SafeScanShortcuts(subDir, apps);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error scanning folder {folderPath}: {ex.Message}");
            }
        }

        private void ListInstalledApps_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selectedButton == null || ListInstalledApps.SelectedItem == null || _isUpdatingUi) return;

            var selectedApp = ListInstalledApps.SelectedItem as InstalledApp;
            if (selectedApp != null)
            {
                _selectedButton.ActionData = selectedApp.ShortcutPath;
                _selectedButton.Title = selectedApp.DisplayName;

                // Encode the app's actual icon as base64 PNG
                string? iconBase64 = ImageSourceToBase64Png(selectedApp.Icon);
                if (!string.IsNullOrEmpty(iconBase64))
                {
                    _selectedButton.Icon = "data:" + iconBase64;
                }
                else
                {
                    _selectedButton.Icon = "rocket";
                }
                _selectedButton.Color = GetDefaultColorForType("app");
                if (TxtAppSearch != null) TxtAppSearch.Text = "";
                TriggerConfigSync();
            }
        }

        private void ListInstalledApps_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            var scrollViewer = FindVisualChild<ScrollViewer>(ListInstalledApps);
            if (scrollViewer != null)
            {
                // Scroll by a small fixed amount (20 pixels) for smooth controlled scrolling
                double offset = scrollViewer.VerticalOffset - (e.Delta > 0 ? 20 : -20);
                scrollViewer.ScrollToVerticalOffset(offset);
                e.Handled = true;
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T result) return result;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private void TabBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;

            var clickedBtn = sender as Button;
            if (clickedBtn == null || clickedBtn.Tag == null) return;

            string actionType = clickedBtn.Tag.ToString()!;
            _activeCategoryTab = actionType;

            if (_selectedButton != null)
            {
                string oldType = _selectedButton.ActionType;

                if (!actionType.Equals(oldType, StringComparison.OrdinalIgnoreCase))
                {
                    // 1. Back up current active settings for the old type
                    string backupKey = _selectedButton.Id + "_" + oldType.ToLower();
                    _buttonTypeBackups[backupKey] = (_selectedButton.ActionData, _selectedButton.Title, _selectedButton.Icon, _selectedButton.Color);

                    // 2. Set new ActionType
                    _selectedButton.ActionType = actionType;

                    // 3. Try to restore previous settings for the new type
                    string restoreKey = _selectedButton.Id + "_" + actionType.ToLower();
                    if (_buttonTypeBackups.TryGetValue(restoreKey, out var backup))
                    {
                        _selectedButton.ActionData = backup.actionData;
                        _selectedButton.Title = backup.title;
                        _selectedButton.Icon = backup.icon;
                        _selectedButton.Color = backup.color;
                    }
                    else
                    {
                        // No backup exists, set default settings for this type
                        _selectedButton.ActionData = "";
                        _selectedButton.Title = GetDefaultTitleForType(actionType);
                        _selectedButton.Color = GetDefaultColorForType(actionType);
                        _selectedButton.Icon = GetDefaultIconForType(actionType);

                        if (actionType.Equals("System", StringComparison.OrdinalIgnoreCase))
                        {
                            _selectedButton.ActionData = "volume_up";
                            _selectedButton.Title = GetSystemActionTitle("volume_up");
                            _selectedButton.Icon = GetDefaultIconForType("system", "volume_up");
                        }
                        else if (actionType.Equals("Hotkey", StringComparison.OrdinalIgnoreCase))
                        {
                            _selectedButton.ActionData = "";
                            _selectedButton.Title = "";
                            _selectedButton.Icon = "";
                        }
                    }
                }
                TriggerConfigSync();
            }

            UpdateCategoryTabsHighlight(actionType);
            LoadActionDetails();
            RefreshGridPreview();
        }

        private void PopulateHotkeyRecorderFromData(string data)
        {
            if (TxtHotkeyRecorder == null) return;

            string normalized = (data ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(normalized))
            {
                TxtHotkeyRecorder.Text = "";
                if (TxtHotkeyName != null) TxtHotkeyName.Text = "";
                _customHotkeyConfiguredIcon = null;
                UpdateHotkeyIconPreview();

                if (BorderHotkeyName != null) BorderHotkeyName.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A2A3A"));
                if (BorderHotkeyIconPicker != null) BorderHotkeyIconPicker.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A2A3A"));
                if (TxtHotkeyNameError != null) TxtHotkeyNameError.Visibility = Visibility.Collapsed;
                return;
            }

            TxtHotkeyRecorder.Text = FormatHotkeyDisplayString(normalized);
        }

        private string FormatHotkeyDisplayString(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return "";
            string[] parts = data.Split(new char[] { '+', '|', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var displayTokens = new List<string>();

            foreach (var part in parts)
            {
                string t = part.Trim().ToLowerInvariant();
                switch (t)
                {
                    case "ctrl": case "control": displayTokens.Add("Ctrl"); break;
                    case "alt": case "menu": displayTokens.Add("Alt"); break;
                    case "shift": displayTokens.Add("Shift"); break;
                    case "win": case "windows": case "cmd": displayTokens.Add("Win"); break;
                    default:
                        if (t.Length == 1) displayTokens.Add(t.ToUpperInvariant());
                        else if (t.StartsWith("f") && t.Length <= 3) displayTokens.Add(t.ToUpperInvariant());
                        else displayTokens.Add(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(t));
                        break;
                }
            }
            return string.Join(" + ", displayTokens);
        }

        private string? _customHotkeyConfiguredIcon = null;
        private string? _editingSavedActionId = null;
        private string? _lastRecordedHotkeyData = null;

        private void UpdateHotkeyIconPreview()
        {
            if (ContentHotkeyIconPreview != null)
            {
                if (!string.IsNullOrEmpty(_customHotkeyConfiguredIcon))
                {
                    ContentHotkeyIconPreview.Content = CreateIconElementStatic(_customHotkeyConfiguredIcon, 22, System.Windows.HorizontalAlignment.Center, System.Windows.VerticalAlignment.Center, new Thickness(0), true);
                    if (BorderHotkeyIconPicker != null)
                    {
                        BorderHotkeyIconPicker.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A2A3A"));
                    }
                }
                else
                {
                    var placeholder = new TextBlock
                    {
                        Text = "\uE710",
                        FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                        FontSize = 16,
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF")),
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    ContentHotkeyIconPreview.Content = placeholder;
                }
            }
        }

        private void ShowHotkeyNameError(string message)
        {
            if (BorderHotkeyName != null)
            {
                BorderHotkeyName.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
            }
            if (TxtHotkeyNameError != null)
            {
                TxtHotkeyNameError.Text = message;
                TxtHotkeyNameError.Visibility = Visibility.Visible;
            }
            TxtHotkeyName?.Focus();
        }

        private void TxtHotkeyName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(TxtHotkeyName?.Text))
            {
                if (BorderHotkeyName != null)
                {
                    BorderHotkeyName.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A2A3A"));
                }
                if (TxtHotkeyNameError != null)
                {
                    TxtHotkeyNameError.Visibility = Visibility.Collapsed;
                }
            }

            if (!_isUpdatingUi && _selectedButton != null && _selectedButton.ActionType.Equals("Hotkey", StringComparison.OrdinalIgnoreCase))
            {
                _selectedButton.Title = TxtHotkeyName?.Text?.Trim() ?? "";
                RefreshGridPreview();
            }
        }

        private void BtnSaveHotkey_Click(object sender, RoutedEventArgs e)
        {
            string rawData = _selectedButton != null ? (_selectedButton.ActionData ?? "") : (_lastRecordedHotkeyData ?? "");
            if (string.IsNullOrWhiteSpace(rawData))
            {
                ShowHotkeyNameError("Please record a key combination first");
                return;
            }

            string customNameInput = TxtHotkeyName?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(customNameInput))
            {
                ShowHotkeyNameError("Please enter a combination name");
                return;
            }

            if (string.IsNullOrEmpty(_customHotkeyConfiguredIcon))
            {
                if (BorderHotkeyIconPicker != null)
                {
                    BorderHotkeyIconPicker.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                }
                ShowHotkeyNameError("Please select an icon for this combination");
                return;
            }

            string normalizedData = rawData.Trim().ToLowerInvariant();
            string keysDisplay = FormatHotkeyDisplayString(normalizedData);
            string finalLabel = customNameInput;
            string iconToSave = _customHotkeyConfiguredIcon;

            if (ConfigManager.Current != null)
            {
                if (ConfigManager.Current.SavedCustomHotkeys == null)
                {
                    ConfigManager.Current.SavedCustomHotkeys = new List<HotkeyActionItem>();
                }

                // Check for duplicate name or duplicate key combination (excluding the item currently being edited)
                var duplicateName = ConfigManager.Current.SavedCustomHotkeys.FirstOrDefault(h =>
                    h.Label.Equals(finalLabel, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(_editingSavedActionId) || !h.ActionId.Equals(_editingSavedActionId, StringComparison.OrdinalIgnoreCase)));

                var duplicateKeys = ConfigManager.Current.SavedCustomHotkeys.FirstOrDefault(h =>
                    h.ActionId.Equals(normalizedData, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(_editingSavedActionId) || !h.ActionId.Equals(_editingSavedActionId, StringComparison.OrdinalIgnoreCase)));

                if (duplicateName != null)
                {
                    ShowHotkeyNameError("A saved combination with this name already exists");
                    return;
                }

                if (duplicateKeys != null)
                {
                    ShowHotkeyNameError("A saved combination with these shortcut keys already exists");
                    return;
                }

                // Reset validation UI
                if (BorderHotkeyName != null)
                {
                    BorderHotkeyName.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A2A3A"));
                }
                if (BorderHotkeyIconPicker != null)
                {
                    BorderHotkeyIconPicker.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A2A3A"));
                }
                if (TxtHotkeyNameError != null)
                {
                    TxtHotkeyNameError.Visibility = Visibility.Collapsed;
                }

                var existing = ConfigManager.Current.SavedCustomHotkeys.FirstOrDefault(h =>
                    (!string.IsNullOrEmpty(_editingSavedActionId) && (h.ActionId.Equals(_editingSavedActionId, StringComparison.OrdinalIgnoreCase) || h.Label.Equals(finalLabel, StringComparison.OrdinalIgnoreCase))) ||
                    h.ActionId.Equals(normalizedData, StringComparison.OrdinalIgnoreCase) ||
                    h.Label.Equals(finalLabel, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    existing.Label = finalLabel;
                    existing.KeysDisplay = keysDisplay;
                    existing.ActionId = normalizedData;
                    existing.Glyph = iconToSave;
                }
                else
                {
                    ConfigManager.Current.SavedCustomHotkeys.Add(new HotkeyActionItem
                    {
                        Category = "Saved Combinations",
                        ActionId = normalizedData,
                        Label = finalLabel,
                        KeysDisplay = keysDisplay,
                        Glyph = iconToSave
                    });
                }
                ConfigManager.Save();
            }

            if (_selectedButton != null)
            {
                _selectedButton.ActionType = "Hotkey";
                _selectedButton.ActionData = normalizedData;
                _selectedButton.Title = finalLabel;
                _selectedButton.Icon = iconToSave;

                RefreshGridPreview();
                TriggerConfigSync();
            }
            else
            {
                _server.SyncButtons();
            }

            // Reset recorder input fields after saving combination
            ResetHotkeyRecorderInputs();

            // Rebuild preset list showing Saved Combinations section
            BuildHotkeyActionSections(normalizedData);
        }

        private void ResetHotkeyRecorderInputs()
        {
            StopRecordingHotkey();
            if (TxtHotkeyName != null) TxtHotkeyName.Text = "";
            if (TxtHotkeyRecorder != null) TxtHotkeyRecorder.Text = "";
            _customHotkeyConfiguredIcon = null;
            _lastRecordedHotkeyData = null;
            UpdateHotkeyIconPreview();
            _editingSavedActionId = null;

            if (BorderHotkeyName != null)
            {
                BorderHotkeyName.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A2A3A"));
            }
            if (BorderHotkeyIconPicker != null)
            {
                BorderHotkeyIconPicker.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A2A3A"));
            }
            if (TxtHotkeyNameError != null)
            {
                TxtHotkeyNameError.Visibility = Visibility.Collapsed;
            }
        }

        private static string GetDefaultPresetIcon(string actionId, string label)
        {
            string id = (actionId ?? "").ToLowerInvariant().Trim();
            string lbl = (label ?? "").ToLowerInvariant().Trim();

            if (id == "ctrl+c" || lbl == "copy")
                return "svgpath:M16 1H4c-1.1 0-2 .9-2 2v14h2V3h12V1zm3 4H8c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h11c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2zm0 16H8V7h11v14z";

            if (id == "ctrl+v" || lbl == "paste")
                return "svgpath:M19 2h-4.18C14.4 1.84 13.3 1 12 1s-2.4.84-2.82 1H5c-1.1 0-2 .9-2 2v16c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm-7 0c.55 0 1 .45 1 1s-.45 1-1 1-1-.45-1-1 .45-1 1-1zm7 18H5V4h2v3h10V4h2v16z";

            if (id == "ctrl+x" || lbl == "cut")
                return "svgpath:M9.64 7.64c.23-.5.36-1.05.36-1.64 0-2.21-1.79-4-4-4S2 3.79 2 6s1.79 4 4 4c.59 0 1.14-.13 1.64-.36L10 12l-2.36 2.36C7.14 14.13 6.59 14 6 14c-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4c0-.59-.13-1.14-.36-1.64L12 14l7 7h3v-1L9.64 7.64zM6 8c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2zm0 12c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2zm13-8l3-3V6h-3l-5 5 5 5z";

            if (id == "ctrl+a" || lbl == "select all")
                return "svgpath:M3 3h2v2H3V3zm0 4h2v2H3V7zm0 4h2v2H3v-2zm0 4h2v2H3v-2zm0 4h2v2H3v-2zm4 0h2v2H7v-2zm4 0h2v2h-2v-2zm4 0h2v2h-2v-2zm4 0h2v2h-2v-2zm0-4h2v2h-2v-2zm0-4h2v2h-2v-2zm0-4h2v2h-2V7zm0-4h2v2h-2V3zm-4 0h2v2h-2V3zm-4 0h2v2h-2V3zm-4 0h2v2H7V3zm2 4h6v6H9V7z";

            if (id == "ctrl+z" || lbl == "undo")
                return "svgpath:M12.5 8c-2.65 0-5.05.99-6.9 2.6L2 7v9h9l-3.62-3.62c1.39-1.16 3.16-1.88 5.12-1.88 3.54 0 6.55 2.31 7.6 5.5l2.37-.78C21.08 11.03 17.15 8 12.5 8z";

            if (id == "ctrl+y" || lbl == "redo")
                return "svgpath:M18.4 10.6C16.55 8.99 14.15 8 11.5 8c-4.65 0-8.58 3.03-9.96 7.22l2.37.78C4.95 12.81 7.96 10.5 11.5 10.5c1.96 0 3.73.72 5.12 1.88L13 16h9V7l-3.6 3.6z";

            if (id == "ctrl+s" || lbl == "save")
                return "svgpath:M17 3H5c-1.11 0-2 .9-2 2v14c0 1.1.89 2 2 2h14c1.1 0 2-.9 2-2V7l-4-4zm-5 16c-1.66 0-3-1.34-3-3s1.34-3 3-3 3 1.34 3 3-1.34 3-3 3zm3-10H5V5h10v4z";

            if (id == "ctrl+f" || lbl == "find" || lbl == "search")
                return "svgpath:M15.5 14h-.79l-.28-.27C15.41 12.59 16 11.11 16 9.5 16 5.91 13.09 3 9.5 3S3 5.91 3 9.5 5.91 16 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z";

            if (id == "ctrl+t" || lbl == "new tab")
                return "svgpath:M7 4h10c1.66 0 3 1.34 3 3v10c0 1.66-1.34 3-3 3H7c-1.66 0-3-1.34-3-3V7c0-1.66 1.34-3 3-3zm0 2c-.55 0-1 .45-1 1v10c0 .55.45 1 1 1h10c.55 0 1-.45 1-1V7c0-.55-.45-1-1-1H7zm5 2c.55 0 1 .45 1 1v2h2c.55 0 1 .45 1 1s-.45 1-1 1h-2v2c0 .55-.45 1-1 1s-1-.45-1-1v-2H9c-.55 0-1-.45-1-1s.45-1 1-1h2V9c0-.55.45-1 1-1z";

            if (id == "ctrl+w" || lbl == "close tab")
                return "svgpath:M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z";

            if (id == "ctrl+shift+t" || lbl == "reopen tab")
                return "svgpath:M12 5V1L7 6l5 5V7c3.31 0 6 2.69 6 6s-2.69 6-6 6-6-2.69-6-6H4c0 4.42 3.58 8 8 8s8-3.58 8-8-3.58-8-8-8z";

            if (id == "ctrl+r" || lbl == "refresh")
                return "svgpath:M17.65 6.35C16.2 4.9 14.21 4 12 4c-4.42 0-7.99 3.58-7.99 8s3.57 8 7.99 8c3.73 0 6.84-2.55 7.73-6h-2.08c-.82 2.33-3.04 4-5.65 4-3.31 0-6-2.69-6-6s2.69-6 6-6c1.66 0 3.14.69 4.22 1.78L13 11h7V4l-2.35 2.35z";

            if (id == "ctrl+tab" || lbl == "next tab")
                return "svgpath:M10 6L8.59 7.41 13.17 12l-4.58 4.59L10 18l6-6z";

            if (id == "ctrl+shift+tab" || lbl == "prev tab")
                return "svgpath:M15.41 7.41L14 6l-6 6 6 6 1.41-1.41L10.83 12z";

            if (id == "f1" || lbl == "f1") return "text:F1";
            if (id == "f2" || lbl == "f2") return "text:F2";
            if (id == "f3" || lbl == "f3") return "text:F3";
            if (id == "f4" || lbl == "f4") return "text:F4";
            if (id == "f5" || lbl == "f5") return "text:F5";
            if (id == "f6" || lbl == "f6") return "text:F6";
            if (id == "f7" || lbl == "f7") return "text:F7";
            if (id == "f8" || lbl == "f8") return "text:F8";
            if (id == "f9" || lbl == "f9") return "text:F9";
            if (id == "f10" || lbl == "f10") return "text:F10";
            if (id == "f11" || lbl == "f11") return "text:F11";
            if (id == "f12" || lbl == "f12") return "text:F12";

            if (id.StartsWith("f") && id.Length <= 3)
                return $"text:{id.ToUpperInvariant()}";

            return "";
        }

        private void BuildHotkeyActionSections(string? selectedActionId)
        {
            if (PanelHotkeyActionsContainer == null) return;
            PanelHotkeyActionsContainer.Children.Clear();

            var hotkeyActions = new List<HotkeyActionItem>();

            // Saved Combinations (User custom saved hotkeys)
            if (ConfigManager.Current?.SavedCustomHotkeys != null && ConfigManager.Current.SavedCustomHotkeys.Count > 0)
            {
                foreach (var saved in ConfigManager.Current.SavedCustomHotkeys)
                {
                    string glyphToUse = string.IsNullOrEmpty(saved.Glyph) || saved.Glyph == "keyboard" ? GetDefaultPresetIcon(saved.ActionId, saved.Label) : saved.Glyph;
                    hotkeyActions.Add(new HotkeyActionItem
                    {
                        Category = "Saved Combinations",
                        ActionId = saved.ActionId,
                        Label = saved.Label,
                        KeysDisplay = saved.KeysDisplay,
                        Glyph = glyphToUse
                    });
                }
            }

            // Clipboard & Editing
            hotkeyActions.Add(new HotkeyActionItem { Category = "Clipboard & Editing", ActionId = "ctrl+c", Label = "Copy", KeysDisplay = "Ctrl + C", Glyph = GetDefaultPresetIcon("ctrl+c", "Copy") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Clipboard & Editing", ActionId = "ctrl+v", Label = "Paste", KeysDisplay = "Ctrl + V", Glyph = GetDefaultPresetIcon("ctrl+v", "Paste") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Clipboard & Editing", ActionId = "ctrl+x", Label = "Cut", KeysDisplay = "Ctrl + X", Glyph = GetDefaultPresetIcon("ctrl+x", "Cut") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Clipboard & Editing", ActionId = "ctrl+a", Label = "Select All", KeysDisplay = "Ctrl + A", Glyph = GetDefaultPresetIcon("ctrl+a", "Select All") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Clipboard & Editing", ActionId = "ctrl+z", Label = "Undo", KeysDisplay = "Ctrl + Z", Glyph = GetDefaultPresetIcon("ctrl+z", "Undo") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Clipboard & Editing", ActionId = "ctrl+y", Label = "Redo", KeysDisplay = "Ctrl + Y", Glyph = GetDefaultPresetIcon("ctrl+y", "Redo") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Clipboard & Editing", ActionId = "ctrl+s", Label = "Save", KeysDisplay = "Ctrl + S", Glyph = GetDefaultPresetIcon("ctrl+s", "Save") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Clipboard & Editing", ActionId = "ctrl+f", Label = "Find", KeysDisplay = "Ctrl + F", Glyph = GetDefaultPresetIcon("ctrl+f", "Find") });

            // Browser Navigation
            hotkeyActions.Add(new HotkeyActionItem { Category = "Browser Navigation", ActionId = "ctrl+t", Label = "New Tab", KeysDisplay = "Ctrl + T", Glyph = GetDefaultPresetIcon("ctrl+t", "New Tab") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Browser Navigation", ActionId = "ctrl+w", Label = "Close Tab", KeysDisplay = "Ctrl + W", Glyph = GetDefaultPresetIcon("ctrl+w", "Close Tab") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Browser Navigation", ActionId = "ctrl+shift+t", Label = "Reopen Tab", KeysDisplay = "Ctrl + Shift + T", Glyph = GetDefaultPresetIcon("ctrl+shift+t", "Reopen Tab") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Browser Navigation", ActionId = "ctrl+r", Label = "Refresh", KeysDisplay = "Ctrl + R", Glyph = GetDefaultPresetIcon("ctrl+r", "Refresh") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Browser Navigation", ActionId = "ctrl+tab", Label = "Next Tab", KeysDisplay = "Ctrl + Tab", Glyph = GetDefaultPresetIcon("ctrl+tab", "Next Tab") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Browser Navigation", ActionId = "ctrl+shift+tab", Label = "Prev Tab", KeysDisplay = "Ctrl + Shift + Tab", Glyph = GetDefaultPresetIcon("ctrl+shift+tab", "Prev Tab") });

            // Function Keys
            hotkeyActions.Add(new HotkeyActionItem { Category = "Function Keys", ActionId = "f1", Label = "F1", KeysDisplay = "F1", Glyph = GetDefaultPresetIcon("f1", "F1") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Function Keys", ActionId = "f2", Label = "F2", KeysDisplay = "F2", Glyph = GetDefaultPresetIcon("f2", "F2") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Function Keys", ActionId = "f3", Label = "F3", KeysDisplay = "F3", Glyph = GetDefaultPresetIcon("f3", "F3") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Function Keys", ActionId = "f4", Label = "F4", KeysDisplay = "F4", Glyph = GetDefaultPresetIcon("f4", "F4") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Function Keys", ActionId = "f5", Label = "F5", KeysDisplay = "F5", Glyph = GetDefaultPresetIcon("f5", "F5") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Function Keys", ActionId = "f6", Label = "F6", KeysDisplay = "F6", Glyph = GetDefaultPresetIcon("f6", "F6") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Function Keys", ActionId = "f7", Label = "F7", KeysDisplay = "F7", Glyph = GetDefaultPresetIcon("f7", "F7") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Function Keys", ActionId = "f8", Label = "F8", KeysDisplay = "F8", Glyph = GetDefaultPresetIcon("f8", "F8") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Function Keys", ActionId = "f9", Label = "F9", KeysDisplay = "F9", Glyph = GetDefaultPresetIcon("f9", "F9") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Function Keys", ActionId = "f10", Label = "F10", KeysDisplay = "F10", Glyph = GetDefaultPresetIcon("f10", "F10") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Function Keys", ActionId = "f11", Label = "F11", KeysDisplay = "F11", Glyph = GetDefaultPresetIcon("f11", "F11") });
            hotkeyActions.Add(new HotkeyActionItem { Category = "Function Keys", ActionId = "f12", Label = "F12", KeysDisplay = "F12", Glyph = GetDefaultPresetIcon("f12", "F12") });

            var categories = hotkeyActions.Select(a => a.Category).Distinct().ToList();
            bool isFirst = true;

            foreach (var category in categories)
            {
                var items = hotkeyActions.Where(a => a.Category == category).ToList();

                if (!isFirst)
                {
                    var divider = new Border
                    {
                        BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C1C24")),
                        BorderThickness = new Thickness(0, 1, 0, 0),
                        Margin = new Thickness(0, 4, 0, 14)
                    };
                    PanelHotkeyActionsContainer.Children.Add(divider);
                }
                isFirst = false;

                var header = new TextBlock
                {
                    Text = category,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF")),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 12)
                };
                PanelHotkeyActionsContainer.Children.Add(header);

                var wrapPanel = new WrapPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 12)
                };

                foreach (var item in items)
                {
                    bool isSelected = item.ActionId.Equals(selectedActionId, StringComparison.OrdinalIgnoreCase);

                    var itemPanel = new StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Vertical,
                        Width = 76,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        Margin = new Thickness(4, 0, 4, 12)
                    };

                    var border = new Border
                    {
                        Width = 76,
                        Height = 76,
                        CornerRadius = new CornerRadius(16),
                        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isSelected ? "#252535" : "#111116")),
                        BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isSelected ? "#8B5CF6" : "#1C1C24")),
                        BorderThickness = new Thickness(isSelected ? 2 : 1.5),
                        SnapsToDevicePixels = true,
                        Tag = item,
                        Cursor = System.Windows.Input.Cursors.Hand
                    };

                    var iconElement = CreateIconElementStatic(string.IsNullOrEmpty(item.Glyph) ? "keyboard" : item.Glyph, 32, System.Windows.HorizontalAlignment.Center, System.Windows.VerticalAlignment.Center, new Thickness(0), true);
                    border.Child = iconElement;

                    border.MouseEnter += (s, ev) =>
                    {
                        if (border.Tag is HotkeyActionItem hi && !hi.ActionId.Equals(_selectedButton?.ActionData, StringComparison.OrdinalIgnoreCase))
                        {
                            border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E28"));
                            border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8B5CF6"));
                        }
                    };
                    border.MouseLeave += (s, ev) =>
                    {
                        if (border.Tag is HotkeyActionItem hi && !hi.ActionId.Equals(_selectedButton?.ActionData, StringComparison.OrdinalIgnoreCase))
                        {
                            border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111116"));
                            border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C1C24"));
                        }
                    };

                    border.MouseLeftButtonDown += HotkeyActionItem_Click;

                    if (item.Category == "Saved Combinations")
                    {
                        var cm = new System.Windows.Controls.ContextMenu();
                        
                        var miDelete = new System.Windows.Controls.MenuItem { Header = "Delete Saved Combination" };
                        miDelete.Click += (s, ev) =>
                        {
                            ConfigManager.Current?.SavedCustomHotkeys?.RemoveAll(h => h.ActionId.Equals(item.ActionId, StringComparison.OrdinalIgnoreCase) || h.Label.Equals(item.Label, StringComparison.OrdinalIgnoreCase));
                            ConfigManager.Save();
                            BuildHotkeyActionSections(_selectedButton?.ActionData);
                        };
                        cm.Items.Add(miDelete);
                        border.ContextMenu = cm;
                    }

                    itemPanel.Children.Add(border);

                    var label = new TextBlock
                    {
                        Text = item.Label,
                        FontSize = 10.5,
                        FontWeight = FontWeights.Medium,
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF")),
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        Margin = new Thickness(0, 6, 0, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 76
                    };
                    itemPanel.Children.Add(label);

                    wrapPanel.Children.Add(itemPanel);
                }

                PanelHotkeyActionsContainer.Children.Add(wrapPanel);
            }
        }

        private void HotkeyActionItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is HotkeyActionItem item)
            {
                _lastRecordedHotkeyData = item.ActionId;
                _customHotkeyConfiguredIcon = string.IsNullOrEmpty(item.Glyph) ? "keyboard" : item.Glyph;
                _editingSavedActionId = item.Category == "Saved Combinations" ? item.ActionId : null;
                UpdateHotkeyIconPreview();

                if (TxtHotkeyName != null)
                {
                    TxtHotkeyName.Text = item.Category == "Saved Combinations" ? item.Label : "";
                }

                PopulateHotkeyRecorderFromData(item.ActionId);

                if (_selectedButton != null)
                {
                    _selectedButton.ActionData = item.ActionId;
                    _selectedButton.Title = item.Label;
                    _selectedButton.Icon = _customHotkeyConfiguredIcon;
                    RefreshGridPreview();
                    TriggerConfigSync();
                }

                BuildHotkeyActionSections(item.ActionId);
            }
        }        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private readonly HashSet<int> _activeRecordedVkCodes = new HashSet<int>();

        private void StartRecordingHotkey()
        {
            _activeRecordedVkCodes.Clear();
            if (!LowLevelKeyboardHook.IsHooked)
            {
                LowLevelKeyboardHook.Start(OnLowLevelKeyRecorded);
            }
            if (PanelHotkeyRecordingIndicator != null)
            {
                PanelHotkeyRecordingIndicator.Visibility = Visibility.Visible;
            }
        }

        private void StopRecordingHotkey()
        {
            _activeRecordedVkCodes.Clear();
            if (LowLevelKeyboardHook.IsHooked)
            {
                LowLevelKeyboardHook.Stop();
            }
            if (PanelHotkeyRecordingIndicator != null)
            {
                PanelHotkeyRecordingIndicator.Visibility = Visibility.Collapsed;
            }
        }

        private void TxtHotkeyRecorder_GotFocus(object sender, RoutedEventArgs e)
        {
            StartRecordingHotkey();
        }

        private void TxtHotkeyRecorder_LostFocus(object sender, RoutedEventArgs e)
        {
            StopRecordingHotkey();
        }

        private void TxtHotkeyRecorder_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            TxtHotkeyRecorder?.Focus();
            StartRecordingHotkey();
        }

        private void TxtHotkeyName_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Return)
            {
                e.Handled = true;
                BtnSaveHotkey_Click(sender, e);
            }
        }

        private void TxtHotkeyRecorder_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            e.Handled = true;

            System.Windows.Input.Key k = (e.Key == System.Windows.Input.Key.System) ? e.SystemKey : e.Key;
            if (k == System.Windows.Input.Key.Enter || k == System.Windows.Input.Key.Return)
            {
                if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
                {
                    BtnSaveHotkey_Click(sender, e);
                    return;
                }
            }

            int vkCode = System.Windows.Input.KeyInterop.VirtualKeyFromKey(k);
            if (vkCode > 0)
            {
                OnLowLevelKeyRecorded(vkCode, true);
            }
        }

        private bool OnLowLevelKeyRecorded(int vkCode, bool isKeyDown)
        {
            if (!isKeyDown)
            {
                _activeRecordedVkCodes.Remove(vkCode);
                return true; // Suppress system execution of key release without clearing recorded shortcut!
            }

            _activeRecordedVkCodes.Add(vkCode);

            // Determine if modifier keys are down (checked from tracked keys and AsyncKeyState)
            bool isCtrl = _activeRecordedVkCodes.Any(vk => vk == 0x11 || vk == 0xA2 || vk == 0xA3) ||
                         (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0xA2) & 0x8000) != 0 || (GetAsyncKeyState(0xA3) & 0x8000) != 0;

            bool isAlt = _activeRecordedVkCodes.Any(vk => vk == 0x12 || vk == 0xA4 || vk == 0xA5) ||
                        (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0xA4) & 0x8000) != 0 || (GetAsyncKeyState(0xA5) & 0x8000) != 0;

            bool isShift = _activeRecordedVkCodes.Any(vk => vk == 0x10 || vk == 0xA0 || vk == 0xA1) ||
                          (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0xA0) & 0x8000) != 0 || (GetAsyncKeyState(0xA1) & 0x8000) != 0;

            bool isWin = _activeRecordedVkCodes.Any(vk => vk == 0x5B || vk == 0x5C) ||
                         (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;

            // Non-modifier target keys currently pressed
            var targetVkCodes = _activeRecordedVkCodes.Where(vk =>
                vk != 0x10 && vk != 0x11 && vk != 0x12 && vk != 0x5B && vk != 0x5C &&
                vk != 0xA0 && vk != 0xA1 && vk != 0xA2 && vk != 0xA3 && vk != 0xA4 && vk != 0xA5
            ).ToList();

            var parts = new List<string>();
            if (isCtrl) parts.Add("ctrl");
            if (isAlt) parts.Add("alt");
            if (isShift) parts.Add("shift");
            if (isWin) parts.Add("win");

            foreach (var targetVk in targetVkCodes)
            {
                string keyName = GetKeyNameFromVkCode(targetVk);
                if (!string.IsNullOrEmpty(keyName) && !parts.Contains(keyName))
                {
                    parts.Add(keyName);
                }
            }

            if (parts.Count > 0)
            {
                string dataStr = string.Join("+", parts);

                Dispatcher.InvokeAsync(() =>
                {
                    _lastRecordedHotkeyData = dataStr;
                    PopulateHotkeyRecorderFromData(dataStr);

                    if (_selectedButton != null)
                    {
                        _selectedButton.ActionData = dataStr;
                        _selectedButton.Title = FormatHotkeyDisplayString(dataStr);
                        if (!string.IsNullOrEmpty(_customHotkeyConfiguredIcon))
                        {
                            _selectedButton.Icon = _customHotkeyConfiguredIcon;
                        }

                        RefreshGridPreview();
                        TriggerConfigSync();
                    }

                    if (targetVkCodes.Count > 0)
                    {
                        BuildHotkeyActionSections(dataStr);
                    }
                });
            }

            return true; // Suppress system execution of the key!
        }

        private static string GetKeyNameFromVkCode(int vkCode)
        {
            if (vkCode >= 0x41 && vkCode <= 0x5A) return ((char)vkCode).ToString().ToLowerInvariant();
            if (vkCode >= 0x30 && vkCode <= 0x39) return (vkCode - 0x30).ToString();
            if (vkCode >= 0x70 && vkCode <= 0x87) return "f" + (vkCode - 0x6F);
            if (vkCode >= 0x60 && vkCode <= 0x69) return (vkCode - 0x60).ToString();

            return vkCode switch
            {
                0x09 => "tab",
                0x14 => "capslock",
                0x20 => "space",
                0x0D => "enter",
                0x08 => "backspace",
                0x1B => "escape",
                0x2E => "delete",
                0x2D => "insert",
                0x24 => "home",
                0x23 => "end",
                0x21 => "pageup",
                0x22 => "pagedown",
                0x26 => "up",
                0x28 => "down",
                0x25 => "left",
                0x27 => "right",
                0xBB => "plus",
                0xBD => "minus",
                0xAE => "volume_down",
                0xAF => "volume_up",
                0xAD => "volume_mute",
                0xB0 => "media_next",
                0xB1 => "media_prev",
                0xB2 => "media_stop",
                0xB3 => "media_play_pause",
                0x2C => "printscreen",
                0x13 => "pause",
                0xBF => "slash",
                0xDC => "backslash",
                0xC0 => "tilde",
                0xDB => "leftbracket",
                0xDD => "rightbracket",
                0xBA => "semicolon",
                0xDE => "quote",
                0xBC => "comma",
                0xBE => "period",
                _ => ""
            };
        }

        private string GetStringFromKey(System.Windows.Input.Key key)
        {
            if (key >= System.Windows.Input.Key.A && key <= System.Windows.Input.Key.Z) return key.ToString().ToLowerInvariant();
            if (key >= System.Windows.Input.Key.D0 && key <= System.Windows.Input.Key.D9) return (key - System.Windows.Input.Key.D0).ToString();
            if (key >= System.Windows.Input.Key.NumPad0 && key <= System.Windows.Input.Key.NumPad9) return (key - System.Windows.Input.Key.NumPad0).ToString();
            if (key >= System.Windows.Input.Key.F1 && key <= System.Windows.Input.Key.F24) return key.ToString().ToLowerInvariant();

            switch (key)
            {
                case System.Windows.Input.Key.Tab: return "tab";
                case System.Windows.Input.Key.Capital: return "capslock";
                case System.Windows.Input.Key.Space: return "space";
                case System.Windows.Input.Key.Return: return "enter";
                case System.Windows.Input.Key.Back: return "backspace";
                case System.Windows.Input.Key.Escape: return "escape";
                case System.Windows.Input.Key.Delete: return "delete";
                case System.Windows.Input.Key.Insert: return "insert";
                case System.Windows.Input.Key.Home: return "home";
                case System.Windows.Input.Key.End: return "end";
                case System.Windows.Input.Key.PageUp: return "pageup";
                case System.Windows.Input.Key.PageDown: return "pagedown";
                case System.Windows.Input.Key.Up: return "up";
                case System.Windows.Input.Key.Down: return "down";
                case System.Windows.Input.Key.Left: return "left";
                case System.Windows.Input.Key.Right: return "right";
                case System.Windows.Input.Key.OemPlus: return "plus";
                case System.Windows.Input.Key.OemMinus: return "minus";
                default: return key.ToString().ToLowerInvariant();
            }
        }

        private void BtnClearHotkey_Click(object sender, RoutedEventArgs e)
        {
            ResetHotkeyRecorderInputs();
            if (_selectedButton != null)
            {
                _selectedButton.ActionData = "";
                RefreshGridPreview();
                TriggerConfigSync();
                BuildHotkeyActionSections("");
            }
        }

        private System.Windows.Threading.DispatcherTimer? _reiconSearchTimer;
        private string _activeReiconCategory = "All";

        private void BtnBrowseReicons_Click(object sender, RoutedEventArgs e)
        {
            if (ModalReiconsPicker != null)
            {
                ModalReiconsPicker.Visibility = Visibility.Visible;
                if (TxtReiconSearch != null) TxtReiconSearch.Text = "";
                _activeReiconCategory = "All";
                PopulateReiconCategories();
                RefreshReiconGrid();
            }
        }

        private void BtnCloseReiconModal_Click(object sender, RoutedEventArgs e)
        {
            if (ModalReiconsPicker != null)
            {
                ModalReiconsPicker.Visibility = Visibility.Collapsed;
            }
        }

        private void TxtReiconSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_reiconSearchTimer == null)
            {
                _reiconSearchTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(300)
                };
                _reiconSearchTimer.Tick += (s, ev) =>
                {
                    _reiconSearchTimer.Stop();
                    RefreshReiconGrid();
                };
            }
            _reiconSearchTimer.Stop();
            _reiconSearchTimer.Start();
        }

        private void PopulateReiconCategories()
        {
            if (ModalStackReiconCategories != null) ModalStackReiconCategories.Children.Clear();

            var categories = ReiconService.GetCategories();
            foreach (var cat in categories)
            {
                bool isSelected = string.Equals(cat, _activeReiconCategory, StringComparison.OrdinalIgnoreCase);

                var btn = new Button
                {
                    Content = cat,
                    Style = (Style)FindResource("CategoryTabButton"),
                    Margin = new Thickness(0, 0, 6, 0),
                    Padding = new Thickness(14, 6, 14, 6),
                    Tag = cat,
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isSelected ? "#2A2A38" : "#14141C")),
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isSelected ? "#444458" : "#22222E")),
                    BorderThickness = new Thickness(1),
                    Foreground = isSelected ? System.Windows.Media.Brushes.White : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF"))
                };

                btn.Click += (s, ev) =>
                {
                    if (s is Button b && b.Tag is string categoryName)
                    {
                        _activeReiconCategory = categoryName;
                        PopulateReiconCategories();
                        RefreshReiconGrid();
                    }
                };

                if (ModalStackReiconCategories != null) ModalStackReiconCategories.Children.Add(btn);
            }
        }

        private async void RefreshReiconGrid()
        {
            if (WrapReiconGrid != null) WrapReiconGrid.Children.Clear();
            if (ModalWrapReiconGrid != null) ModalWrapReiconGrid.Children.Clear();

            string query = "";
            if (TxtReiconSearch != null && !string.IsNullOrEmpty(TxtReiconSearch.Text))
                query = TxtReiconSearch.Text;
            else if (ModalTxtReiconSearch != null && !string.IsNullOrEmpty(ModalTxtReiconSearch.Text))
                query = ModalTxtReiconSearch.Text;

            var items = await ReiconService.SearchAsync(query, _activeReiconCategory);

            foreach (var item in items)
            {
                var createItemPanel = () => {
                    var itemPanel = new StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Vertical,
                        Width = 84,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        Margin = new Thickness(6, 6, 6, 12)
                    };

                    var border = new Border
                    {
                        Width = 76,
                        Height = 76,
                        CornerRadius = new CornerRadius(14),
                        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14141C")),
                        BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#22222E")),
                        BorderThickness = new Thickness(1),
                        SnapsToDevicePixels = true,
                        Tag = item,
                        Cursor = System.Windows.Input.Cursors.Hand
                    };

                    UIElement iconVisual;
                    if (!string.IsNullOrEmpty(item.PathData))
                    {
                        try
                        {
                            var geom = Geometry.Parse(item.PathData);
                            var vectorPath = new System.Windows.Shapes.Path
                            {
                                Data = geom,
                                Fill = System.Windows.Media.Brushes.White,
                                Width = 28,
                                Height = 28,
                                Stretch = Stretch.Uniform,
                                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                                VerticalAlignment = VerticalAlignment.Center
                            };
                            iconVisual = vectorPath;
                        }
                        catch
                        {
                            iconVisual = new TextBlock
                            {
                                Text = "\uE8A9",
                                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                                FontSize = 26,
                                Foreground = System.Windows.Media.Brushes.White,
                                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                                VerticalAlignment = VerticalAlignment.Center
                            };
                        }
                    }
                    else
                    {
                        iconVisual = new TextBlock
                        {
                            Text = item.Glyph,
                            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                            FontSize = 28,
                            Foreground = System.Windows.Media.Brushes.White,
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                    }
                    border.Child = iconVisual;

                    border.MouseEnter += (s, ev) =>
                    {
                        border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#20202C"));
                        border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B3B4E"));
                    };
                    border.MouseLeave += (s, ev) =>
                    {
                        border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14141C"));
                        border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#22222E"));
                    };

                    border.MouseLeftButtonDown += (s, ev) =>
                    {
                        if (border.Tag is ReiconItem selectedItem)
                        {
                            _customHotkeyConfiguredIcon = selectedItem.Id;
                            UpdateHotkeyIconPreview();
                            if (_selectedButton != null)
                            {
                                _selectedButton.Icon = selectedItem.Id;
                                if (_selectedButton.ActionType.Equals("Profile", StringComparison.OrdinalIgnoreCase))
                                {
                                    LoadActionDetails();
                                }
                                RefreshGridPreview();
                                TriggerConfigSync();
                            }
                            if (ModalReiconsPicker != null) ModalReiconsPicker.Visibility = Visibility.Collapsed;
                        }
                    };

                    itemPanel.Children.Add(border);

                    var label = new TextBlock
                    {
                        Text = item.Name,
                        FontSize = 11,
                        FontWeight = FontWeights.Medium,
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF")),
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        Margin = new Thickness(0, 6, 0, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 84
                    };
                    itemPanel.Children.Add(label);
                    return itemPanel;
                };

                if (WrapReiconGrid != null) WrapReiconGrid.Children.Add(createItemPanel());
                if (ModalWrapReiconGrid != null) ModalWrapReiconGrid.Children.Add(createItemPanel());
            }

            if (items.Count == 0)
            {
                var createNoResults = () => new TextBlock
                {
                    Text = "No matching icons found.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6B7280")),
                    Margin = new Thickness(12)
                };
                if (WrapReiconGrid != null) WrapReiconGrid.Children.Add(createNoResults());
                if (ModalWrapReiconGrid != null) ModalWrapReiconGrid.Children.Add(createNoResults());
            }
        }

        private void TxtActionData_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_selectedButton == null || _isUpdatingUi) return;
            if (_selectedButton.ActionType.Equals("Macro", StringComparison.OrdinalIgnoreCase) || 
                _selectedButton.ActionType.Equals("System", StringComparison.OrdinalIgnoreCase)) return;

            _selectedButton.ActionData = TxtActionData.Text;

            if (_selectedButton.ActionType.Equals("URL", StringComparison.OrdinalIgnoreCase))
            {
                string url = TxtActionData.Text.Trim();
                string title = "Open URL";
                if (!string.IsNullOrEmpty(url))
                {
                    title = url.Replace("http://", "").Replace("https://", "").Replace("www.", "").TrimEnd('/');
                    if (title.Length > 15) title = title.Substring(0, 12) + "...";
                }
                _selectedButton.Title = title;
                _selectedButton.Icon = "url";
                _selectedButton.Color = GetDefaultColorForType("url");
            }

            TriggerConfigSync();
        }


        private static readonly System.Net.Http.HttpClient _httpClient = new System.Net.Http.HttpClient();

        private async Task<string?> FetchFaviconAsBase64Async(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            string cleanUrl = url.Trim();
            if (!cleanUrl.StartsWith("http://") && !cleanUrl.StartsWith("https://"))
            {
                cleanUrl = "https://" + cleanUrl;
            }

            try
            {
                var uri = new Uri(cleanUrl);
                string domain = uri.Host;
                string faviconUrl = $"https://www.google.com/s2/favicons?sz=64&domain={domain}";

                byte[] bytes = await _httpClient.GetByteArrayAsync(faviconUrl);
                if (bytes != null && bytes.Length > 0)
                {
                    return Convert.ToBase64String(bytes);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching favicon for {cleanUrl}: {ex.Message}");
            }
            return null;
        }

        private List<SwiftDockDesktop.BrowserProfileOption> _availableBrowserProfiles = new List<SwiftDockDesktop.BrowserProfileOption>();

        private void RenderYourWebsitesUI()
        {
            if (WrapYourWebsites == null || PanelYourWebsites == null) return;
            WrapYourWebsites.Children.Clear();

            var saved = ConfigManager.Current.SavedWebsites;
            if (saved == null || saved.Count == 0)
            {
                PanelYourWebsites.Visibility = Visibility.Collapsed;
                return;
            }

            PanelYourWebsites.Visibility = Visibility.Visible;

            foreach (var site in saved)
            {
                var stack = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Vertical,
                    Margin = new Thickness(4, 0, 4, 12),
                    Width = 76,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                };

                var btn = new Button
                {
                    ToolTip = site.Title,
                    Style = FindResource("PopularWebsiteBtnStyle") as Style,
                    Tag = $"{site.Url}|{site.Title}|{site.ActionData}"
                };

                var img = new System.Windows.Controls.Image
                {
                    Width = 44,
                    Height = 44,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

                if (!string.IsNullOrEmpty(site.Icon) && site.Icon.StartsWith("data:"))
                {
                    try
                    {
                        string b64 = site.Icon.Substring(5);
                        byte[] bytes = Convert.FromBase64String(b64);
                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                        bmp.BeginInit();
                        bmp.StreamSource = new MemoryStream(bytes);
                        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        img.Source = bmp;
                    }
                    catch
                    {
                        img.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri($"https://www.google.com/s2/favicons?sz=128&domain={GetCleanDomain(site.Url)}"));
                    }
                }
                else
                {
                    img.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri($"https://www.google.com/s2/favicons?sz=128&domain={GetCleanDomain(site.Url)}"));
                }

                btn.Content = img;
                var targetSite = site;
                string siteUrl = site.Url;
                string siteTitle = site.Title;
                string siteActionData = site.ActionData;
                btn.Click += (s, e) =>
                {
                    OpenUrlConfigModal(siteUrl, siteTitle, siteActionData);
                };

                // Context menu with Delete option on right click
                var contextMenu = new System.Windows.Controls.ContextMenu
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16161A")),
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252535")),
                    BorderThickness = new Thickness(1)
                };

                var deleteMenuItem = new System.Windows.Controls.MenuItem
                {
                    Header = "Delete Saved Website",
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444")),
                    Padding = new Thickness(8, 6, 8, 6)
                };

                deleteMenuItem.Click += (s, e) =>
                {
                    ConfigManager.Current.SavedWebsites?.Remove(targetSite);
                    ConfigManager.Save();
                    RenderYourWebsitesUI();
                    TriggerConfigSync();
                };

                contextMenu.Items.Add(deleteMenuItem);
                btn.ContextMenu = contextMenu;
                stack.ContextMenu = contextMenu;

                var txt = new TextBlock
                {
                    Text = site.Title,
                    FontSize = 10.5,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF")),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    Margin = new Thickness(0, 6, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 76
                };

                stack.Children.Add(btn);
                stack.Children.Add(txt);
                WrapYourWebsites.Children.Add(stack);
            }
        }

        private string GetCleanDomain(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return "google.com";
            string domain = rawUrl.Replace("http://", "").Replace("https://", "").Replace("www.", "").TrimEnd('/');
            int slashIdx = domain.IndexOf('/');
            if (slashIdx > 0) domain = domain.Substring(0, slashIdx);
            return domain;
        }

        private void OpenUrlConfigModal(string url, string siteName, string existingActionData = "")
        {

            string targetUrl = string.IsNullOrWhiteSpace(url) ? "https://" : url;
            string targetName = string.IsNullOrWhiteSpace(siteName) ? "Custom Website" : siteName;

            TxtModalWebsiteName.Text = targetName;
            TxtModalWebsiteUrl.Text = targetUrl;
            TxtModalTitlePreview.Text = targetName;
            TxtModalUrlPreview.Text = targetUrl;

            // Load installed browser profiles into ComboBox
            _availableBrowserProfiles = SwiftDockDesktop.BrowserDetector.GetInstalledBrowserProfiles();
            ComboModalBrowserProfile.ItemsSource = _availableBrowserProfiles;
            ComboModalBrowserProfile.DisplayMemberPath = "DisplayName";

            int selectedIdx = 0;
            if (!string.IsNullOrWhiteSpace(existingActionData))
            {
                var parts = existingActionData.Split('|');
                if (parts.Length >= 2)
                {
                    string exe = parts[1].Trim();
                    string profileArg = parts.Length >= 3 ? parts[2].Trim() : "";
                    int matchIdx = _availableBrowserProfiles.FindIndex(b => b.ExePath.Equals(exe, StringComparison.OrdinalIgnoreCase) && b.ProfileArg.Equals(profileArg, StringComparison.OrdinalIgnoreCase));
                    if (matchIdx >= 0) selectedIdx = matchIdx;
                }
            }
            ComboModalBrowserProfile.SelectedIndex = selectedIdx;

            UpdateModalFaviconPreview(targetUrl);
            UrlConfigModal.Visibility = Visibility.Visible;
        }

        private void UpdateModalFaviconPreview(string targetUrl)
        {
            if (string.IsNullOrWhiteSpace(targetUrl) || ImgModalFavicon == null) return;
            string domain = GetCleanDomain(targetUrl);
            try
            {
                ImgModalFavicon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri($"https://www.google.com/s2/favicons?sz=128&domain={domain}"));
            }
            catch { }
        }

        private void TxtModalWebsiteName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtModalTitlePreview != null && TxtModalWebsiteName != null)
            {
                TxtModalTitlePreview.Text = string.IsNullOrWhiteSpace(TxtModalWebsiteName.Text) ? "Website Preview" : TxtModalWebsiteName.Text;
            }
        }

        private void TxtModalWebsiteUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtModalUrlPreview != null && TxtModalWebsiteUrl != null)
            {
                string u = TxtModalWebsiteUrl.Text;
                TxtModalUrlPreview.Text = string.IsNullOrWhiteSpace(u) ? "https://..." : u;
                UpdateModalFaviconPreview(u);
            }
        }

        private async void BtnSaveUrlModal_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtModalWebsiteName.Text.Trim();
            string rawUrl = TxtModalWebsiteUrl.Text.Trim();

            if (string.IsNullOrWhiteSpace(rawUrl)) return;

            if (!rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                rawUrl = "https://" + rawUrl;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = GetCleanDomain(rawUrl);
            }

            string exePath = "";
            string profileArg = "";
            if (ComboModalBrowserProfile.SelectedItem is SwiftDockDesktop.BrowserProfileOption selectedProfile)
            {
                exePath = selectedProfile.ExePath;
                profileArg = selectedProfile.ProfileArg;
            }

            string actionData = string.IsNullOrWhiteSpace(exePath)
                ? rawUrl
                : $"{rawUrl}|{exePath}|{profileArg}";

            // If configuring Volume Key Action
            if (_isVolKeyUrlConfigMode)
            {
                _isVolKeyUrlConfigMode = false;

                string volIcon = "url";
                var b64Icon = await FetchFaviconAsBase64Async(rawUrl);
                if (!string.IsNullOrEmpty(b64Icon))
                {
                    volIcon = "data:" + b64Icon;
                }
                else
                {
                    volIcon = $"https://www.google.com/s2/favicons?sz=128&domain={GetCleanDomain(rawUrl)}";
                }

                SaveWebsiteToSavedList(name, rawUrl, actionData, volIcon);
                AssignVolAction("URL", actionData, name, volIcon);
                UrlConfigModal.Visibility = Visibility.Collapsed;
                return;
            }

            string icon = "url";
            var b64 = await FetchFaviconAsBase64Async(rawUrl);
            if (!string.IsNullOrEmpty(b64))
            {
                icon = "data:" + b64;
            }

            if (_selectedButton != null)
            {
                _selectedButton.ActionType = "URL";
                _selectedButton.ActionData = actionData;
                _selectedButton.Title = name;
                _selectedButton.Icon = icon;
            }

            SaveWebsiteToSavedList(name, rawUrl, actionData, icon);

            UrlConfigModal.Visibility = Visibility.Collapsed;
            RefreshUrlLinksLayout();
            if (_selectedButton != null)
            {
                TriggerConfigSync();
            }
            else
            {
                ConfigManager.Save();
                _server.SyncButtons();
            }
        }

        private void SaveWebsiteToSavedList(string name, string rawUrl, string actionData, string icon = "url")
        {
            if (ConfigManager.Current.SavedWebsites == null)
            {
                ConfigManager.Current.SavedWebsites = new List<SavedWebsiteItem>();
            }

            var existing = ConfigManager.Current.SavedWebsites.Find(w => w.Url.Equals(rawUrl, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                ConfigManager.Current.SavedWebsites.Add(new SavedWebsiteItem
                {
                    Title = name,
                    Url = rawUrl,
                    Icon = icon,
                    ActionData = actionData
                });
            }
            else
            {
                existing.Title = name;
                existing.Icon = icon;
                existing.ActionData = actionData;
            }
            ConfigManager.Save();
        }

        private void BtnCloseUrlModal_Click(object sender, RoutedEventArgs e)
        {
            UrlConfigModal.Visibility = Visibility.Collapsed;
            if (TxtUrlSearch != null) TxtUrlSearch.Text = "";
            if (_urlSearchPopup != null) _urlSearchPopup.IsOpen = false;
        }

        private async Task UpdateSingleUrlAsync(string newUrlValue)
        {
            if (_selectedButton == null) return;
            string cleanUrl = (newUrlValue ?? "").Trim();
            _selectedButton.ActionData = cleanUrl;

            if (string.IsNullOrWhiteSpace(cleanUrl))
            {
                _selectedButton.Icon = "url";
                _selectedButton.Title = "Open URL";
            }
            else
            {
                var b64 = await FetchFaviconAsBase64Async(cleanUrl);
                if (!string.IsNullOrEmpty(b64))
                {
                    _selectedButton.Icon = "data:" + b64;
                }
                else
                {
                    _selectedButton.Icon = "url";
                }

                string domainTitle = GetCleanDomain(cleanUrl);
                if (!string.IsNullOrWhiteSpace(domainTitle))
                {
                    _selectedButton.Title = domainTitle;
                }
                else
                {
                    _selectedButton.Title = "Open URL";
                }
            }

            _selectedButton.Color = GetDefaultColorForType("url");

            ConfigManager.Save();
            RefreshGridPreview();
            TriggerConfigSync();
        }

        public class WebsiteSuggestion
        {
            public string Title { get; set; } = "";
            public string Url { get; set; } = "";
            public string Domain { get; set; } = "";
        }

        private static readonly List<WebsiteSuggestion> PresetWebsiteDatabase = new List<WebsiteSuggestion>
        {
            new WebsiteSuggestion { Title = "YouTube", Url = "https://youtube.com", Domain = "youtube.com" },
            new WebsiteSuggestion { Title = "YouTube Music", Url = "https://music.youtube.com", Domain = "music.youtube.com" },
            new WebsiteSuggestion { Title = "Instagram", Url = "https://instagram.com", Domain = "instagram.com" },
            new WebsiteSuggestion { Title = "ChatGPT", Url = "https://chatgpt.com", Domain = "chatgpt.com" },
            new WebsiteSuggestion { Title = "Google Gemini", Url = "https://gemini.google.com", Domain = "gemini.google.com" },
            new WebsiteSuggestion { Title = "Claude AI", Url = "https://claude.ai", Domain = "claude.ai" },
            new WebsiteSuggestion { Title = "GitHub", Url = "https://github.com", Domain = "github.com" },
            new WebsiteSuggestion { Title = "X (Twitter)", Url = "https://x.com", Domain = "x.com" },
            new WebsiteSuggestion { Title = "Figma", Url = "https://figma.com", Domain = "figma.com" },
            new WebsiteSuggestion { Title = "Notion", Url = "https://notion.so", Domain = "notion.so" },
            new WebsiteSuggestion { Title = "Spotify", Url = "https://spotify.com", Domain = "spotify.com" },
            new WebsiteSuggestion { Title = "LinkedIn", Url = "https://linkedin.com", Domain = "linkedin.com" },
            new WebsiteSuggestion { Title = "Reddit", Url = "https://reddit.com", Domain = "reddit.com" },
            new WebsiteSuggestion { Title = "WhatsApp Web", Url = "https://web.whatsapp.com", Domain = "web.whatsapp.com" },
            new WebsiteSuggestion { Title = "Gmail", Url = "https://mail.google.com", Domain = "mail.google.com" },
            new WebsiteSuggestion { Title = "Google", Url = "https://google.com", Domain = "google.com" },
            new WebsiteSuggestion { Title = "Amazon", Url = "https://amazon.com", Domain = "amazon.com" },
            new WebsiteSuggestion { Title = "Netflix", Url = "https://netflix.com", Domain = "netflix.com" },
            new WebsiteSuggestion { Title = "Twitch", Url = "https://twitch.tv", Domain = "twitch.tv" },
            new WebsiteSuggestion { Title = "Discord", Url = "https://discord.com", Domain = "discord.com" },
            new WebsiteSuggestion { Title = "Canva", Url = "https://canva.com", Domain = "canva.com" },
            new WebsiteSuggestion { Title = "Pinterest", Url = "https://pinterest.com", Domain = "pinterest.com" },
            new WebsiteSuggestion { Title = "Stack Overflow", Url = "https://stackoverflow.com", Domain = "stackoverflow.com" },
            new WebsiteSuggestion { Title = "Wikipedia", Url = "https://wikipedia.org", Domain = "wikipedia.org" },
            new WebsiteSuggestion { Title = "JioHotstar", Url = "https://jiohotstar.com", Domain = "jiohotstar.com" }
        };

        private void TxtUrlSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtUrlSearchWatermark != null && TxtUrlSearch != null)
            {
                TxtUrlSearchWatermark.Visibility = string.IsNullOrEmpty(TxtUrlSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void RefreshUrlLinksLayout()
        {
            if (ScrollUrlLinks != null) ScrollUrlLinks.Visibility = Visibility.Visible;
            RenderYourWebsitesUI();

            if (TxtUrlSearch == null) return;

            // Wire up TxtUrlSearch watermark
            TxtUrlSearchWatermark.Visibility = string.IsNullOrEmpty(TxtUrlSearch.Text) ? Visibility.Visible : Visibility.Collapsed;

            // Reuse existing popup if already created
            if (_urlSearchPopup != null)
            {
                _urlSearchPopup.IsOpen = false;
                return;
            }

            var popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = TxtUrlSearch,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Slide
            };
            _urlSearchPopup = popup;

            var border = new System.Windows.Controls.Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16161A")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252535")),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(6),
                Margin = new Thickness(0, 4, 0, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 20,
                    ShadowDepth = 6,
                    Opacity = 0.5,
                    Color = Colors.Black
                }
            };

            var listBox = new System.Windows.Controls.ListBox
            {
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                MaxHeight = 260
            };

            Style? itemStyle = FindResource("SearchSuggestionItemStyle") as Style;
            if (itemStyle != null)
            {
                listBox.ItemContainerStyle = itemStyle;
            }

            listBox.SelectionChanged += (s, e) =>
            {
                string targetUrl = "";
                string targetTitle = "";

                if (listBox.SelectedItem is ListBoxItem lbi && lbi.Tag is WebsiteSuggestion item)
                {
                    targetUrl = item.Url;
                    targetTitle = item.Title;
                }
                else if (listBox.SelectedItem is WebsiteSuggestion itemDirect)
                {
                    targetUrl = itemDirect.Url;
                    targetTitle = itemDirect.Title;
                }

                if (!string.IsNullOrEmpty(targetUrl))
                {
                    popup.IsOpen = false;
                    TxtUrlSearch.Text = "";
                    OpenUrlConfigModal(targetUrl, targetTitle);
                }
            };

            border.Child = listBox;
            popup.Child = border;

            TxtUrlSearch.TextChanged -= TxtUrlSearch_TextChanged;
            TxtUrlSearch.TextChanged += TxtUrlSearch_TextChanged;
            TxtUrlSearch.TextChanged += (s, e) =>
            {
                string query = TxtUrlSearch.Text.Trim().ToLower();
                if (string.IsNullOrWhiteSpace(query))
                {
                    popup.IsOpen = false;
                    return;
                }

                var matches = PresetWebsiteDatabase.Where(w =>
                    w.Title.ToLower().Contains(query) ||
                    w.Domain.ToLower().Contains(query)).ToList();

                if (query.Contains(".") && !matches.Any(m => m.Domain.Equals(query, StringComparison.OrdinalIgnoreCase)))
                {
                    matches.Insert(0, new WebsiteSuggestion
                    {
                        Title = GetCleanDomain(query),
                        Url = query.StartsWith("http") ? query : "https://" + query,
                        Domain = GetCleanDomain(query)
                    });
                }

                if (matches.Count > 0)
                {
                    listBox.Items.Clear();
                    foreach (var m in matches)
                    {
                        var itemGrid = new Grid { Margin = new Thickness(6, 4, 6, 4) };
                        itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                        var img = new System.Windows.Controls.Image
                        {
                            Width = 24,
                            Height = 24,
                            Margin = new Thickness(0, 0, 12, 0),
                            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri($"https://www.google.com/s2/favicons?sz=128&domain={m.Domain}")),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                        Grid.SetColumn(img, 0);

                        var labels = new StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
                        var titleTxt = new TextBlock { Text = m.Title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.White };
                        var urlTxt = new TextBlock { Text = m.Url, FontSize = 10.5, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF")), Margin = new Thickness(0, 2, 0, 0) };

                        labels.Children.Add(titleTxt);
                        labels.Children.Add(urlTxt);
                        Grid.SetColumn(labels, 1);

                        itemGrid.Children.Add(img);
                        itemGrid.Children.Add(labels);

                        var lbi = new System.Windows.Controls.ListBoxItem
                        {
                            Content = itemGrid,
                            Tag = m
                        };
                        if (itemStyle != null) lbi.Style = itemStyle;

                        listBox.Items.Add(lbi);
                    }

                    border.Width = Math.Max(340, TxtUrlSearch.ActualWidth > 0 ? TxtUrlSearch.ActualWidth : 340);
                    popup.IsOpen = true;
                }
                else
                {
                    popup.IsOpen = false;
                }
            };

            TxtUrlSearch.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    popup.IsOpen = false;
                    string searchVal = TxtUrlSearch.Text;
                    TxtUrlSearch.Text = "";
                    if (!string.IsNullOrWhiteSpace(searchVal))
                    {
                        OpenUrlConfigModal(searchVal, GetCleanDomain(searchVal));
                    }
                }
            };
        }

        private void BtnPopularWebsite_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                var parts = tag.Split('|');
                if (parts.Length >= 2)
                {
                    string url = parts[0];
                    string siteName = parts[1];
                    OpenUrlConfigModal(url, siteName);
                }
            }
        }

        private void ListSystemActions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Legacy handler kept for XAML compatibility — no longer used
        }

        /// <summary>
        /// Builds the system action sections programmatically, matching the URL page's
        /// section layout pattern. Each category gets: divider → header → WrapPanel of icon buttons.
        /// </summary>
        private void BuildSystemActionSections(List<SystemActionItem> systemActions, string? selectedActionId)
        {
            PanelSystemActionsContainer.Children.Clear();

            var categories = systemActions.Select(a => a.Category).Distinct().ToList();
            bool isFirst = true;

            foreach (var category in categories)
            {
                var items = systemActions.Where(a => a.Category == category).ToList();

                // Section divider (skip for first category)
                if (!isFirst)
                {
                    var divider = new Border
                    {
                        BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C1C24")),
                        BorderThickness = new Thickness(0, 1, 0, 0),
                        Margin = new Thickness(0, 4, 0, 14)
                    };
                    PanelSystemActionsContainer.Children.Add(divider);
                }
                isFirst = false;

                // Section header
                var header = new TextBlock
                {
                    Text = category,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF")),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 12)
                };
                PanelSystemActionsContainer.Children.Add(header);

                // WrapPanel of icon buttons
                var wrapPanel = new WrapPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 12)
                };

                foreach (var item in items)
                {
                    bool isSelected = item.ActionId == selectedActionId;

                    var itemPanel = new StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Vertical,
                        Width = 76,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        Margin = new Thickness(4, 0, 4, 12)
                    };

                    var border = new Border
                    {
                        Width = 76,
                        Height = 76,
                        CornerRadius = new CornerRadius(16),
                        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isSelected ? "#252535" : "#111116")),
                        BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isSelected ? "#FFFFFF" : "#1C1C24")),
                        BorderThickness = new Thickness(isSelected ? 2 : 1.5),
                        SnapsToDevicePixels = true,
                        Tag = item,
                        Cursor = System.Windows.Input.Cursors.Hand
                    };

                    var iconConverter = (IValueConverter)FindResource("IconElementConverter");
                    var iconElement = iconConverter.Convert(item.ActionId, typeof(object), null, System.Globalization.CultureInfo.CurrentCulture) as UIElement;
                    if (iconElement is FrameworkElement fe)
                    {
                        fe.Width = 34;
                        fe.Height = 34;
                        fe.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
                        fe.VerticalAlignment = VerticalAlignment.Center;
                    }
                    border.Child = iconElement;

                    border.MouseEnter += (s, ev) =>
                    {
                        if (border.Tag is SystemActionItem si && si.ActionId != _selectedButton?.ActionData)
                        {
                            border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E28"));
                            border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B82F6"));
                        }
                    };
                    border.MouseLeave += (s, ev) =>
                    {
                        if (border.Tag is SystemActionItem si && si.ActionId != _selectedButton?.ActionData)
                        {
                            border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111116"));
                            border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C1C24"));
                        }
                    };

                    border.MouseLeftButtonDown += SystemActionItem_Click;
                    itemPanel.Children.Add(border);

                    var label = new TextBlock
                    {
                        Text = item.Label,
                        FontSize = 10.5,
                        FontWeight = FontWeights.Medium,
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF")),
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        Margin = new Thickness(0, 6, 0, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 76
                    };
                    itemPanel.Children.Add(label);

                    wrapPanel.Children.Add(itemPanel);
                }

                PanelSystemActionsContainer.Children.Add(wrapPanel);
            }
        }

        private void SystemActionItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_selectedButton == null) return;
            if (sender is Border border && border.Tag is SystemActionItem selectedItem)
            {
                _selectedButton.ActionData = selectedItem.ActionId;
                _selectedButton.Title = GetSystemActionTitle(selectedItem.ActionId);
                _selectedButton.Icon = GetDefaultIconForType("system", selectedItem.ActionId);
                _selectedButton.Color = GetDefaultColorForType("system");
                TriggerConfigSync();
                LoadActionDetails();
            }
        }


        // Button management
        private void BtnAddButton_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureCurrentProfileUnlocked(() => BtnAddButton_Click(sender, e))) return;

            if (ConfigManager.CurrentButtons.Count >= 63)
            {
                MessageBox.Show("Maximum limit of 63 deck shortcuts reached (8 pages).", "Limit Reached", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var newBtn = new ShortcutButton
            {
                Title = "Select App",
                Color = "#FFFFFF", // Normal White
                ActionType = "App",
                ActionData = "",
                Icon = "app_default"
            };

            ConfigManager.CurrentButtons.Add(newBtn);
            SelectShortcutButton(newBtn);
            TriggerConfigSync();
        }




        private void BtnAddProfile_Click(object sender, RoutedEventArgs e)
        {
            var config = ConfigManager.Current;
            int newProfileIndex = config.Profiles.Count + 1;
            var newProfile = new Profile
            {
                Name = $"Profile {newProfileIndex}",
                Buttons = new List<ShortcutButton>()
            };

            config.Profiles.Add(newProfile);
            config.CurrentProfileId = newProfile.Id;
            ConfigManager.Save();

            RefreshProfilesList();
            SelectShortcutButton(null);
            _selectedBulkButtons.Clear();

            _server.SyncButtons();
            _server.SyncProfiles();
        }

        private void MenuItemRenameProfile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.MenuItem menuItem && menuItem.DataContext is Profile profile)
            {
                if (profile.IsLocked && !_unlockedProfilesForSession.Contains(profile.Id))
                {
                    PromptProfilePinUnlock(profile, () => RenameProfileWithDialog(profile));
                    return;
                }
                ListProfiles.SelectedItem = profile;
                RenameProfileWithDialog(profile);
            }
        }

        private void MenuItemToggleLockProfile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.MenuItem menuItem && menuItem.DataContext is Profile profile)
            {
                if (profile.IsLocked && !_unlockedProfilesForSession.Contains(profile.Id))
                {
                    PromptProfilePinUnlock(profile, () => {
                        profile.IsLocked = false;
                        ConfigManager.Save();
                        RefreshProfilesList();
                        _server.SyncProfiles();
                    });
                    return;
                }

                if (!profile.IsLocked)
                {
                    if (string.IsNullOrEmpty(ConfigManager.Current.ProfilePin))
                    {
                        MessageBox.Show("Please set a Profile PIN in Settings before locking profiles.", "Set Profile PIN", MessageBoxButton.OK, MessageBoxImage.Information);
                        BtnOpenSettings_Click(sender, e);
                        return;
                    }
                    profile.IsLocked = true;
                }
                else
                {
                    profile.IsLocked = false;
                }

                ConfigManager.Save();
                RefreshProfilesList();
                _server.SyncProfiles();
            }
        }

        private void UpdateProfilePinSettingsUI()
        {
            bool hasPin = !string.IsNullOrEmpty(ConfigManager.Current.ProfilePin);
            if (LblCurrentPinStatus != null)
            {
                LblCurrentPinStatus.Text = hasPin ? "Status: Profile PIN is Active 🔒" : "Status: No PIN set (Profiles unlocked)";
                LblCurrentPinStatus.Foreground = hasPin ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0xA3, 0xAF));
            }
            if (LblOldPin != null) LblOldPin.Visibility = hasPin ? Visibility.Visible : Visibility.Collapsed;
            if (TxtOldPin != null) TxtOldPin.Visibility = hasPin ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnSaveProfilePin_Click(object sender, RoutedEventArgs e)
        {
            bool hasPin = !string.IsNullOrEmpty(ConfigManager.Current.ProfilePin);
            if (hasPin)
            {
                string oldPinInput = TxtOldPin.Password.Trim();
                if (oldPinInput != ConfigManager.Current.ProfilePin)
                {
                    MessageBox.Show("Current PIN is incorrect.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            string newPin = TxtNewPin.Password.Trim();
            string confirmPin = TxtConfirmPin.Password.Trim();

            if (string.IsNullOrEmpty(newPin) || newPin.Length < 4 || newPin.Length > 6 || !newPin.All(char.IsDigit))
            {
                MessageBox.Show("PIN must be 4 to 6 digits long.", "Invalid PIN", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (newPin != confirmPin)
            {
                MessageBox.Show("New PIN and Confirm PIN do not match.", "PIN Mismatch", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ConfigManager.Current.ProfilePin = newPin;
            ConfigManager.Save();
            if (TxtOldPin != null) TxtOldPin.Password = "";
            if (TxtNewPin != null) TxtNewPin.Password = "";
            if (TxtConfirmPin != null) TxtConfirmPin.Password = "";

            UpdateProfilePinSettingsUI();
            MessageBox.Show("Profile PIN saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnCancelProfilePinModal_Click(object? sender, RoutedEventArgs? e)
        {
            _pendingUnlockProfileTarget = null;
            if (ModalProfilePin != null) ModalProfilePin.Visibility = Visibility.Collapsed;
        }

        private void TxtEntryProfilePin_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                BtnSubmitProfilePinModal_Click(sender, e);
            }
        }

        private void BtnSubmitProfilePinModal_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingUnlockProfileTarget == null)
            {
                if (ModalProfilePin != null) ModalProfilePin.Visibility = Visibility.Collapsed;
                return;
            }

            string enteredPin = TxtEntryProfilePin.Password.Trim();
            if (enteredPin == ConfigManager.Current.ProfilePin)
            {
                _unlockedProfilesForSession.Add(_pendingUnlockProfileTarget.Id);
                var target = _pendingUnlockProfileTarget;
                var pendingAction = _pendingUnlockAction;
                _pendingUnlockProfileTarget = null;
                _pendingUnlockAction = null;
                if (ModalProfilePin != null) ModalProfilePin.Visibility = Visibility.Collapsed;

                pendingAction?.Invoke();

                RefreshGridPreview();
                RefreshProfilesList();
                _server.SyncButtons();
                _server.SyncProfiles();
            }
            else
            {
                if (TxtProfilePinError != null)
                {
                    TxtProfilePinError.Text = "Incorrect PIN. Please try again.";
                    TxtProfilePinError.Visibility = Visibility.Visible;
                }
            }
        }

        private void MenuItemDeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.MenuItem menuItem && menuItem.DataContext is Profile profile)
            {
                ListProfiles.SelectedItem = profile;
                DeleteProfile(profile);
            }
        }

        private void MenuItemExportProfile_Click(object sender, RoutedEventArgs e)
        {
            Profile? profileToExport = null;
            if (sender is System.Windows.Controls.MenuItem menuItem && menuItem.DataContext is Profile profile)
            {
                profileToExport = profile;
            }
            else
            {
                profileToExport = ListProfiles.SelectedItem as Profile;
            }

            if (profileToExport == null)
            {
                MessageBox.Show("Please select a profile to export.", "Export Profile", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var saveFileDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Export SwiftDock Profile",
                    Filter = "SwiftDock Profile (*.swiftdock)|*.swiftdock|JSON File (*.json)|*.json",
                    FileName = $"{profileToExport.Name.Replace(" ", "_")}.swiftdock"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                    string json = System.Text.Json.JsonSerializer.Serialize(profileToExport, options);
                    System.IO.File.WriteAllText(saveFileDialog.FileName, json);
                    MessageBox.Show($"Profile '{profileToExport.Name}' was successfully exported to:\n\n{saveFileDialog.FileName}", "Export Successful", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export profile: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnImportProfile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openFileDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Import SwiftDock Profiles",
                    Filter = "SwiftDock Profile (*.swiftdock;*.json)|*.swiftdock;*.json|All Files (*.*)|*.*",
                    Multiselect = true
                };

                if (openFileDialog.ShowDialog() == true && openFileDialog.FileNames.Length > 0)
                {
                    ImportProfileFiles(openFileDialog.FileNames);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to import profiles: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImportProfileFiles(string[] filePaths)
        {
            if (filePaths == null || filePaths.Length == 0) return;

            int successCount = 0;
            int failCount = 0;
            int totalMissingAppCount = 0;
            int totalMissingProfileCount = 0;
            List<string> importedNames = new List<string>();
            Profile? lastImportedProfile = null;

            foreach (string filePath in filePaths)
            {
                try
                {
                    if (!System.IO.File.Exists(filePath))
                    {
                        failCount++;
                        continue;
                    }

                    string json = System.IO.File.ReadAllText(filePath);
                    var importedProfile = System.Text.Json.JsonSerializer.Deserialize<Profile>(json);
                    if (importedProfile == null || importedProfile.Buttons == null)
                    {
                        failCount++;
                        continue;
                    }

                    // Generate a new ID for the imported profile
                    importedProfile.Id = Guid.NewGuid().ToString();

                    // Check for duplicate profile names
                    string originalName = string.IsNullOrWhiteSpace(importedProfile.Name) ? "Imported Profile" : importedProfile.Name;
                    string targetName = originalName;
                    int count = 1;
                    while (ConfigManager.Current.Profiles.Exists(p => p.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase)))
                    {
                        targetName = $"{originalName} ({count++})";
                    }
                    importedProfile.Name = targetName;

                    // Re-ID buttons and check application / profile paths
                    int missingAppCount = 0;
                    int missingProfileCount = 0;
                    foreach (var btn in importedProfile.Buttons)
                    {
                        btn.Id = Guid.NewGuid().ToString();
                        if (btn.ActionType == "App" && !string.IsNullOrWhiteSpace(btn.ActionData))
                        {
                            if (!System.IO.File.Exists(btn.ActionData) && !btn.ActionData.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
                            {
                                // Attempt auto-repair against installed apps by EXE name
                                string exeName = System.IO.Path.GetFileName(btn.ActionData);
                                var installedApps = ListInstalledApps?.ItemsSource as List<InstalledApp>;
                                var matchedApp = installedApps?.FirstOrDefault(app => 
                                    !string.IsNullOrEmpty(app.ShortcutPath) && 
                                    System.IO.Path.GetFileName(app.ShortcutPath).Equals(exeName, StringComparison.OrdinalIgnoreCase));
                                
                                if (matchedApp != null && !string.IsNullOrEmpty(matchedApp.ShortcutPath))
                                {
                                    btn.ActionData = matchedApp.ShortcutPath;
                                }
                                else
                                {
                                    missingAppCount++;
                                }
                            }
                        }
                        else if ("Profile".Equals(btn.ActionType, StringComparison.OrdinalIgnoreCase))
                        {
                            // Instantly detect if target profile ID exists on this PC
                            if (!ConfigManager.Current.Profiles.Exists(p => p.Id == btn.ActionData))
                            {
                                btn.ActionData = importedProfile.Id;
                                missingProfileCount++;
                            }
                        }
                    }

                    ConfigManager.Current.Profiles.Add(importedProfile);
                    successCount++;
                    importedNames.Add(importedProfile.Name);
                    lastImportedProfile = importedProfile;
                    totalMissingAppCount += missingAppCount;
                    totalMissingProfileCount += missingProfileCount;
                }
                catch
                {
                    failCount++;
                }
            }

            if (successCount > 0 && lastImportedProfile != null)
            {
                ConfigManager.Current.CurrentProfileId = lastImportedProfile.Id;
                ConfigManager.SanitizeProfileButtons();
                ConfigManager.Save();

                RefreshProfilesList();

                // Select missing or default shortcut button
                var missingProfileBtn = lastImportedProfile.Buttons.FirstOrDefault(b => "Profile".Equals(b.ActionType, StringComparison.OrdinalIgnoreCase));
                var missingAppBtn = lastImportedProfile.Buttons.FirstOrDefault(b => 
                    "App".Equals(b.ActionType, StringComparison.OrdinalIgnoreCase) && 
                    !string.IsNullOrWhiteSpace(b.ActionData) && 
                    !System.IO.File.Exists(b.ActionData) && 
                    !b.ActionData.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase));

                if (missingProfileBtn != null)
                {
                    SelectShortcutButton(missingProfileBtn);
                }
                else if (missingAppBtn != null)
                {
                    SelectShortcutButton(missingAppBtn);
                }
                else if (lastImportedProfile.Buttons.Count > 0)
                {
                    SelectShortcutButton(lastImportedProfile.Buttons[0]);
                }
                else
                {
                    SelectShortcutButton(null);
                }

                _selectedBulkButtons.Clear();
                _server.SyncButtons();
                _server.SyncProfiles();

                // Format summary message
                string summaryMsg;
                if (successCount == 1)
                {
                    summaryMsg = $"Profile '{importedNames[0]}' imported successfully!";
                }
                else
                {
                    summaryMsg = $"{successCount} profiles imported successfully:\n• " + string.Join("\n• ", importedNames);
                }

                if (failCount > 0)
                {
                    summaryMsg += $"\n\n- {failCount} file(s) failed to import due to invalid format.";
                }
                if (totalMissingAppCount > 0)
                {
                    summaryMsg += $"\n\n- {totalMissingAppCount} app shortcut(s) were not found on this PC.";
                }
                if (totalMissingProfileCount > 0)
                {
                    summaryMsg += $"\n\n- {totalMissingProfileCount} profile button(s) pointed to external profiles and were automatically remapped.";
                }

                MessageBox.Show(summaryMsg, "Profiles Imported", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (failCount > 0)
            {
                MessageBox.Show($"Failed to import {failCount} selected profile file(s). Please ensure files are valid SwiftDock profile formats.", "Import Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void RenameProfileWithDialog(Profile profile)
        {
            var dialog = new Window
            {
                Title = "Rename Page",
                Width = 400,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x14)),
                Foreground = System.Windows.Media.Brushes.White,
                WindowStyle = WindowStyle.ToolWindow
            };

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = "Enter new name for the page:",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA))
            };
            Grid.SetRow(label, 0);
            grid.Children.Add(label);

            var buttonGrid = new Grid();
            buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textBox = new System.Windows.Controls.TextBox
            {
                Text = profile.Name,
                Height = 36,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x24)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
                CaretBrush = System.Windows.Media.Brushes.White
            };
            Grid.SetColumn(textBox, 0);
            buttonGrid.Children.Add(textBox);

            var btnCancel = new Button
            {
                Content = "Cancel",
                Width = 80,
                Height = 36,
                Style = this.FindResource("SecondaryButton") as Style,
                IsCancel = true
            };
            btnCancel.Click += (s, e) => dialog.DialogResult = false;
            Grid.SetColumn(btnCancel, 2);
            buttonGrid.Children.Add(btnCancel);

            var btnOk = new Button
            {
                Content = "Save",
                Width = 80,
                Height = 36,
                Style = this.FindResource("PrimaryButton") as Style,
                IsDefault = true
            };
            btnOk.Click += (s, e) => dialog.DialogResult = true;
            Grid.SetColumn(btnOk, 4);
            buttonGrid.Children.Add(btnOk);

            Grid.SetRow(buttonGrid, 2);
            grid.Children.Add(buttonGrid);

            dialog.Content = grid;

            dialog.Loaded += (s, e) =>
            {
                textBox.Focus();
                textBox.SelectAll();
            };

            if (dialog.ShowDialog() == true)
            {
                string newName = textBox.Text.Trim();
                if (!string.IsNullOrEmpty(newName))
                {
                    profile.Name = newName;
                    ConfigManager.Save();
                    RefreshProfilesList();
                    _server.SyncProfiles();
                }
            }
        }

        private void BtnDeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null || button.Tag == null) return;

            var profileToDelete = button.Tag as Profile;
            if (profileToDelete == null) return;

            DeleteProfile(profileToDelete);
        }

        private void DeleteProfile(Profile profileToDelete)
        {
            var config = ConfigManager.Current;
            if (config.Profiles.Count <= 1)
            {
                MessageBox.Show("At least one profile must be kept.", "Cannot Delete", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirmResult = MessageBox.Show($"Are you sure you want to delete profile '{profileToDelete.Name}'?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirmResult != MessageBoxResult.Yes) return;

            config.Profiles.Remove(profileToDelete);
            
            // If the deleted profile was the current one, select another one
            if (config.CurrentProfileId == profileToDelete.Id)
            {
                config.CurrentProfileId = config.Profiles[0].Id;
            }

            ConfigManager.Save();

            RefreshProfilesList();
            SelectShortcutButton(null);
            _selectedBulkButtons.Clear();
            _server.SyncButtons();
            _server.SyncProfiles();
        }







        // Visual helper method
        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
        {
            if (depObj != null)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(depObj, i);
                    if (child != null && child is T)
                    {
                        yield return (T)child;
                    }

                    foreach (T childOfChild in FindVisualChildren<T>(child!))
                    {
                        yield return childOfChild;
                    }
                }
            }
        }

        // Settings View Handlers
        private void AnimateSidebarTransition(bool showSettings)
        {
            if (GridSidebarProfiles == null || GridSidebarSettings == null) return;

            var transformProfiles = GridSidebarProfiles.RenderTransform as TranslateTransform;
            if (transformProfiles == null)
            {
                transformProfiles = new TranslateTransform();
                GridSidebarProfiles.RenderTransform = transformProfiles;
                GridSidebarProfiles.RenderTransformOrigin = new Point(0.5, 0.5);
            }

            var transformSettings = GridSidebarSettings.RenderTransform as TranslateTransform;
            if (transformSettings == null)
            {
                transformSettings = new TranslateTransform();
                GridSidebarSettings.RenderTransform = transformSettings;
                GridSidebarSettings.RenderTransformOrigin = new Point(0.5, 0.5);
            }

            GridSidebarProfiles.BeginAnimation(UIElement.OpacityProperty, null);
            transformProfiles.BeginAnimation(TranslateTransform.XProperty, null);
            GridSidebarSettings.BeginAnimation(UIElement.OpacityProperty, null);
            transformSettings.BeginAnimation(TranslateTransform.XProperty, null);

            var duration = TimeSpan.FromMilliseconds(220);
            var cubicEase = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };

            if (showSettings)
            {
                GridSidebarSettings.Visibility = Visibility.Visible;
                transformSettings.X = 40.0;
                GridSidebarSettings.Opacity = 0.0;

                var settingsFadeIn = new System.Windows.Media.Animation.DoubleAnimation { From = 0.0, To = 1.0, Duration = duration, EasingFunction = cubicEase };
                var settingsSlideIn = new System.Windows.Media.Animation.DoubleAnimation { From = 40.0, To = 0.0, Duration = duration, EasingFunction = cubicEase };

                GridSidebarSettings.BeginAnimation(UIElement.OpacityProperty, settingsFadeIn);
                transformSettings.BeginAnimation(TranslateTransform.XProperty, settingsSlideIn);

                var profilesFadeOut = new System.Windows.Media.Animation.DoubleAnimation { From = 1.0, To = 0.0, Duration = duration, EasingFunction = cubicEase };
                var profilesSlideOut = new System.Windows.Media.Animation.DoubleAnimation { From = 0.0, To = -40.0, Duration = duration, EasingFunction = cubicEase };

                profilesFadeOut.Completed += (s, e) => GridSidebarProfiles.Visibility = Visibility.Collapsed;

                GridSidebarProfiles.BeginAnimation(UIElement.OpacityProperty, profilesFadeOut);
                transformProfiles.BeginAnimation(TranslateTransform.XProperty, profilesSlideOut);
            }
            else
            {
                GridSidebarProfiles.Visibility = Visibility.Visible;
                transformProfiles.X = -40.0;
                GridSidebarProfiles.Opacity = 0.0;

                var profilesFadeIn = new System.Windows.Media.Animation.DoubleAnimation { From = 0.0, To = 1.0, Duration = duration, EasingFunction = cubicEase };
                var profilesSlideIn = new System.Windows.Media.Animation.DoubleAnimation { From = -40.0, To = 0.0, Duration = duration, EasingFunction = cubicEase };

                GridSidebarProfiles.BeginAnimation(UIElement.OpacityProperty, profilesFadeIn);
                transformProfiles.BeginAnimation(TranslateTransform.XProperty, profilesSlideIn);

                var settingsFadeOut = new System.Windows.Media.Animation.DoubleAnimation { From = 1.0, To = 0.0, Duration = duration, EasingFunction = cubicEase };
                var settingsSlideOut = new System.Windows.Media.Animation.DoubleAnimation { From = 0.0, To = 40.0, Duration = duration, EasingFunction = cubicEase };

                settingsFadeOut.Completed += (s, e) => GridSidebarSettings.Visibility = Visibility.Collapsed;

                GridSidebarSettings.BeginAnimation(UIElement.OpacityProperty, settingsFadeOut);
                transformSettings.BeginAnimation(TranslateTransform.XProperty, settingsSlideOut);
            }
        }

        private void BtnOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            if (GridSidebarSettings != null && GridSidebarSettings.Visibility != Visibility.Visible)
            {
                AnimateSidebarTransition(true);
            }
            TxtSettingsDeviceName.Text = ConfigManager.Current.DeviceName;
            var chk = FindName("ChkAutoStart") as System.Windows.Controls.Primitives.ToggleButton;
            if (chk != null) chk.IsChecked = ConfigManager.IsAutoStartEnabled();
            UpdateProfilePinSettingsUI();
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            BtnOpenSettings_Click(sender, e);
        }

        private void BtnBackToDashboard_Click(object sender, RoutedEventArgs e)
        {
            HideSidebarSettings();
        }

        private void ChkAutoStart_Click(object sender, RoutedEventArgs e)
        {
            bool enable = sender is System.Windows.Controls.Primitives.ToggleButton chk ? chk.IsChecked == true : ConfigManager.IsAutoStartEnabled();
            ConfigManager.SetAutoStart(enable);
            ConfigManager.Current.AutoStartOnBoot = enable;
            ConfigManager.Save();
        }

        private void BtnRemoveAllAccents_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to remove accent glows from all keycaps across all profiles?",
                "Remove All Accents",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );

            if (result != MessageBoxResult.Yes) return;

            if (ConfigManager.Current != null && ConfigManager.Current.Profiles != null)
            {
                foreach (var profile in ConfigManager.Current.Profiles)
                {
                    if (profile.Buttons != null)
                    {
                        foreach (var button in profile.Buttons)
                        {
                            if (button != null)
                            {
                                button.Color = "#FFFFFF";
                            }
                        }
                    }
                }
            }

            if (ConfigManager.CurrentButtons != null)
            {
                foreach (var button in ConfigManager.CurrentButtons)
                {
                    if (button != null)
                    {
                        button.Color = "#FFFFFF";
                    }
                }
            }

            RefreshGridPreview();
            UpdateKeycapLivePreviews();
            TriggerConfigSync();

            if (LblSettingsAccentStatus != null)
            {
                LblSettingsAccentStatus.Text = "✓ All keycap accents removed successfully!";
                LblSettingsAccentStatus.Margin = new Thickness(0, 6, 0, 0);
                LblSettingsAccentStatus.Visibility = Visibility.Visible;
            }
        }

        private void BtnSaveSettingsDeviceName_Click(object sender, RoutedEventArgs e)
        {
            string newName = TxtSettingsDeviceName.Text.Trim();
            if (string.IsNullOrEmpty(newName))
            {
                MessageBox.Show("Device name cannot be empty.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            ConfigManager.Current.DeviceName = newName;
            ConfigManager.Save();

            // Restart server to broadcast under new name
            _server.Start(newName);
            MessageBox.Show($"Device name updated to '{newName}'. Server restarted.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private string? _editingVolumeKeyTarget = null;
        private string _activeVolPickerTab = "App";

        private void BtnVolumeButtons_Click(object sender, RoutedEventArgs e)
        {
            if (BorderVolumeButtonsModal != null)
            {
                BorderVolumeButtonsModal.Width = 660;
                BorderVolumeButtonsModal.Height = double.NaN;
            }
            PanelVolumeKeyPicker.Visibility = Visibility.Collapsed;
            PanelVolumeKeysList.Visibility = Visibility.Visible;
            UpdateVolumeButtonsModalLabels();
            ModalVolumeButtons.Visibility = Visibility.Visible;
        }

        private void BtnCloseVolumeButtonsModal_Click(object sender, RoutedEventArgs e)
        {
            ModalVolumeButtons.Visibility = Visibility.Collapsed;
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                // 0. Close ModalProfilePin if open
                if (ModalProfilePin != null && ModalProfilePin.Visibility == Visibility.Visible)
                {
                    BtnCancelProfilePinModal_Click(null, null);
                    e.Handled = true;
                    return;
                }

                // 1. Close UrlConfigModal if open
                if (UrlConfigModal != null && UrlConfigModal.Visibility == Visibility.Visible)
                {
                    UrlConfigModal.Visibility = Visibility.Collapsed;
                    _isVolKeyUrlConfigMode = false;
                    e.Handled = true;
                    return;
                }

                // 2. If ModalVolumeButtons is open
                if (ModalVolumeButtons != null && ModalVolumeButtons.Visibility == Visibility.Visible)
                {
                    if (PanelVolumeKeyPicker != null && PanelVolumeKeyPicker.Visibility == Visibility.Visible)
                    {
                        BtnBackToVolumeList_Click(null, null);
                    }
                    else
                    {
                        ModalVolumeButtons.Visibility = Visibility.Collapsed;
                    }
                    e.Handled = true;
                    return;
                }
            }
        }

        private void BtnBackToVolumeList_Click(object? sender, RoutedEventArgs? e)
        {
            if (BorderVolumeButtonsModal != null)
            {
                BorderVolumeButtonsModal.Width = 660;
                BorderVolumeButtonsModal.Height = double.NaN;
            }
            PanelVolumeKeyPicker.Visibility = Visibility.Collapsed;
            PanelVolumeKeysList.Visibility = Visibility.Visible;
            UpdateVolumeButtonsModalLabels();
        }

        private void UpdateVolumeButtonsModalLabels()
        {
            var upBtn = ConfigManager.Current.VolumeUpButton;
            var downBtn = ConfigManager.Current.VolumeDownButton;

            if (LblVolumeUpAction != null) LblVolumeUpAction.Text = GetActionDisplayText(upBtn);
            if (LblVolumeDownAction != null) LblVolumeDownAction.Text = GetActionDisplayText(downBtn);
        }

        private string GetActionDisplayText(ShortcutButton? button)
        {
            if (button == null || string.IsNullOrWhiteSpace(button.ActionData))
                return "Not Configured (Default)";

            string type = string.IsNullOrWhiteSpace(button.ActionType) ? "System" : button.ActionType;

            if (!string.IsNullOrWhiteSpace(button.Title) &&
                !button.Title.Equals("New Button", StringComparison.OrdinalIgnoreCase))
            {
                return $"{button.Title} [{type}]";
            }

            return $"{button.ActionData} [{type}]";
        }

        private void BtnConfigureVolumeUp_Click(object sender, RoutedEventArgs e)
        {
            _editingVolumeKeyTarget = "VolumeUp";
            OpenInModalVolPicker("Configure Action: Volume Up Key");
        }

        private void BtnConfigureVolumeDown_Click(object sender, RoutedEventArgs e)
        {
            _editingVolumeKeyTarget = "VolumeDown";
            OpenInModalVolPicker("Configure Action: Volume Down Key");
        }

        private void OpenInModalVolPicker(string headerTitle)
        {
            if (BorderVolumeButtonsModal != null)
            {
                BorderVolumeButtonsModal.Width = 660;
                BorderVolumeButtonsModal.Height = double.NaN;
            }
            if (TxtVolPickerHeader != null) TxtVolPickerHeader.Text = headerTitle;
            PanelVolumeKeysList.Visibility = Visibility.Collapsed;
            PanelVolumeKeyPicker.Visibility = Visibility.Visible;
            SwitchVolPickerTab("App");
        }

        private void VolTabBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                SwitchVolPickerTab(tag);
            }
        }

        private void SetVolTabActive(Button activeBtn, params Button[] inactiveBtns)
        {
            try
            {
                activeBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#272738"));
                activeBtn.Foreground = System.Windows.Media.Brushes.White;

                foreach (var btn in inactiveBtns)
                {
                    btn.Background = System.Windows.Media.Brushes.Transparent;
                    btn.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#71717A"));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting vol tab active: {ex.Message}");
            }
        }

        private void SwitchVolPickerTab(string tab)
        {
            try
            {
                _activeVolPickerTab = tab;

                VolPickerAppsPanel.Visibility = Visibility.Collapsed;
                VolPickerControlsPanel.Visibility = Visibility.Collapsed;
                VolPickerUrlPanel.Visibility = Visibility.Collapsed;
                ListVolProfiles.Visibility = Visibility.Collapsed;
                if (PanelVolAppSearch != null) PanelVolAppSearch.Visibility = Visibility.Collapsed;
                if (PanelVolUrlSearch != null) PanelVolUrlSearch.Visibility = Visibility.Collapsed;

                if (tab.Equals("App", StringComparison.OrdinalIgnoreCase))
                {
                    SetVolTabActive(VolTabBtnApp, VolTabBtnSetting, VolTabBtnCtrl, VolTabBtnProfile);
                    VolPickerAppsPanel.Visibility = Visibility.Visible;
                    if (PanelVolAppSearch != null) PanelVolAppSearch.Visibility = Visibility.Visible;
                    PopulateVolAppsList(TxtVolAppSearch?.Text ?? "");
                }
                else if (tab.Equals("System", StringComparison.OrdinalIgnoreCase))
                {
                    SetVolTabActive(VolTabBtnSetting, VolTabBtnApp, VolTabBtnCtrl, VolTabBtnProfile);
                    VolPickerControlsPanel.Visibility = Visibility.Visible;
                    PopulateVolControlsList();
                }
                else if (tab.Equals("URL", StringComparison.OrdinalIgnoreCase))
                {
                    SetVolTabActive(VolTabBtnCtrl, VolTabBtnApp, VolTabBtnSetting, VolTabBtnProfile);
                    VolPickerUrlPanel.Visibility = Visibility.Visible;
                    if (PanelVolUrlSearch != null) PanelVolUrlSearch.Visibility = Visibility.Visible;
                    if (TxtVolUrlSearch != null) TxtVolUrlSearch.Text = "";
                    PopulateVolUrlTab();
                }
                else if (tab.Equals("Profile", StringComparison.OrdinalIgnoreCase))
                {
                    SetVolTabActive(VolTabBtnProfile, VolTabBtnApp, VolTabBtnSetting, VolTabBtnCtrl);
                    ListVolProfiles.Visibility = Visibility.Visible;
                    PopulateVolProfilesList();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error switching vol picker tab: {ex.Message}");
            }
        }

        public class VolUrlSuggestionItem
        {
            public string Title { get; set; } = "";
            public string Url { get; set; } = "";
            public string Icon { get; set; } = "";
        }

        private bool _isVolKeyUrlConfigMode = false;

        private void ListVolUrlSuggestions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (ListVolUrlSuggestions.SelectedItem is VolUrlSuggestionItem item)
                {
                    if (PopupVolUrlSearch != null) PopupVolUrlSearch.IsOpen = false;
                    ListVolUrlSuggestions.SelectedIndex = -1;
                    _isVolKeyUrlConfigMode = true;
                    OpenUrlConfigModal(item.Url, item.Title);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in ListVolUrlSuggestions_SelectionChanged: {ex.Message}");
            }
        }

        private void TxtVolAppSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            PopulateVolAppsList(TxtVolAppSearch.Text);
        }

        private void TxtVolUrlSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtVolUrlSearchWatermark != null)
            {
                TxtVolUrlSearchWatermark.Visibility = string.IsNullOrEmpty(TxtVolUrlSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
            }

            string query = TxtVolUrlSearch?.Text.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(query))
            {
                if (PopupVolUrlSearch != null) PopupVolUrlSearch.IsOpen = false;
                return;
            }

            var suggestions = new List<VolUrlSuggestionItem>();
            var (sUrl, sTitle, sDomain) = GetSuggestedWebsiteInfo(query);
            if (!string.IsNullOrEmpty(sUrl))
            {
                suggestions.Add(new VolUrlSuggestionItem
                {
                    Title = sTitle,
                    Url = sUrl,
                    Icon = $"https://www.google.com/s2/favicons?sz=128&domain={sDomain}"
                });
            }

            var popular = new List<(string name, string domain, string url)>
            {
                ("YouTube", "youtube.com", "https://youtube.com"),
                ("Instagram", "instagram.com", "https://instagram.com"),
                ("ChatGPT", "chatgpt.com", "https://chatgpt.com"),
                ("Gemini", "gemini.google.com", "https://gemini.google.com"),
                ("Claude", "claude.ai", "https://claude.ai"),
                ("GitHub", "github.com", "https://github.com"),
                ("X", "x.com", "https://x.com"),
                ("Figma", "figma.com", "https://figma.com"),
                ("Notion", "notion.so", "https://notion.so"),
                ("Spotify", "spotify.com", "https://spotify.com"),
                ("WhatsApp", "whatsapp.com", "https://web.whatsapp.com"),
                ("Gmail", "mail.google.com", "https://mail.google.com")
            };

            foreach (var pop in popular)
            {
                if (pop.name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    pop.domain.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    if (!suggestions.Any(s => s.Url.Equals(pop.url, StringComparison.OrdinalIgnoreCase)))
                    {
                        suggestions.Add(new VolUrlSuggestionItem
                        {
                            Title = pop.name,
                            Url = pop.url,
                            Icon = $"https://www.google.com/s2/favicons?sz=128&domain={pop.domain}"
                        });
                    }
                }
            }

            if (ListVolUrlSuggestions != null)
            {
                ListVolUrlSuggestions.ItemsSource = suggestions;
            }

            if (PopupVolUrlSearch != null)
            {
                PopupVolUrlSearch.IsOpen = suggestions.Count > 0;
            }
        }

        private void TxtVolUrlSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                string text = TxtVolUrlSearch.Text.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (PopupVolUrlSearch != null) PopupVolUrlSearch.IsOpen = false;
                    var (sUrl, sTitle, _) = GetSuggestedWebsiteInfo(text);
                    _isVolKeyUrlConfigMode = true;
                    OpenUrlConfigModal(sUrl, sTitle);
                }
            }
        }

        private (string url, string title, string domain) GetSuggestedWebsiteInfo(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return ("", "", "");

            string clean = query.Trim();
            string url = clean;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            if (!url.Contains(".") && !clean.Contains("."))
            {
                url = "https://" + clean + ".com";
            }

            string domain = GetCleanDomain(url);
            string title = domain;

            if (!string.IsNullOrEmpty(domain))
            {
                string baseName = domain;
                int dotIdx = domain.IndexOf('.');
                if (dotIdx > 0) baseName = domain.Substring(0, dotIdx);
                if (baseName.Length > 0)
                {
                    title = char.ToUpper(baseName[0]) + (baseName.Length > 1 ? baseName.Substring(1) : "");
                }
            }

            return (url, title, domain);
        }

        private void PopulateVolUrlTab(string filter = "")
        {
            try
            {
                // 1. Render Your Saved Websites at the TOP
                if (WrapVolYourWebsites != null && PanelVolYourWebsites != null)
                {
                    WrapVolYourWebsites.Children.Clear();
                    var saved = ConfigManager.Current.SavedWebsites ?? new List<SavedWebsiteItem>();

                    if (saved.Count > 0)
                    {
                        PanelVolYourWebsites.Visibility = Visibility.Visible;
                        foreach (var site in saved)
                        {
                            string url = site.Url;
                            string title = site.Title;
                            string actionData = site.ActionData;
                            var element = CreateVolWebsiteKeycap(url, title, site.Icon, (u, t, a) =>
                            {
                                _isVolKeyUrlConfigMode = true;
                                OpenUrlConfigModal(u, t, actionData);
                            });
                            WrapVolYourWebsites.Children.Add(element);
                        }
                    }
                    else
                    {
                        PanelVolYourWebsites.Visibility = Visibility.Collapsed;
                    }
                }

                // 2. Render Popular Websites Below
                if (WrapVolPopularWebsites != null && PanelVolPopularWebsites != null)
                {
                    WrapVolPopularWebsites.Children.Clear();
                    PanelVolPopularWebsites.Visibility = Visibility.Visible;

                    var popular = new List<(string name, string domain, string url)>
                    {
                        ("YouTube", "youtube.com", "https://youtube.com"),
                        ("Instagram", "instagram.com", "https://instagram.com"),
                        ("ChatGPT", "chatgpt.com", "https://chatgpt.com"),
                        ("Gemini", "gemini.google.com", "https://gemini.google.com"),
                        ("Claude", "claude.ai", "https://claude.ai"),
                        ("GitHub", "github.com", "https://github.com"),
                        ("X", "x.com", "https://x.com"),
                        ("Figma", "figma.com", "https://figma.com"),
                        ("Notion", "notion.so", "https://notion.so"),
                        ("Spotify", "spotify.com", "https://spotify.com"),
                        ("WhatsApp", "whatsapp.com", "https://web.whatsapp.com"),
                        ("Gmail", "mail.google.com", "https://mail.google.com")
                    };

                    foreach (var pop in popular)
                    {
                        string iconUrl = $"https://www.google.com/s2/favicons?sz=128&domain={pop.domain}";
                        var element = CreateVolWebsiteKeycap(pop.url, pop.name, iconUrl, (u, t, a) =>
                        {
                            _isVolKeyUrlConfigMode = true;
                            OpenUrlConfigModal(u, t);
                        });
                        WrapVolPopularWebsites.Children.Add(element);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating VolUrlTab: {ex.Message}");
            }
        }

        private FrameworkElement CreateVolWebsiteKeycap(string url, string title, string iconSource, Action<string, string, string>? onSelect = null)
        {
            var stack = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Vertical,
                Margin = new Thickness(4, 0, 4, 12),
                Width = 76,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var btn = new Button
            {
                ToolTip = title,
                Style = FindResource("PopularWebsiteBtnStyle") as Style
            };

            var img = new System.Windows.Controls.Image
            {
                Width = 44,
                Height = 44,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            if (!string.IsNullOrEmpty(iconSource) && iconSource.StartsWith("data:"))
            {
                try
                {
                    string b64 = iconSource.Substring(5);
                    byte[] bytes = Convert.FromBase64String(b64);
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = new MemoryStream(bytes);
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    img.Source = bmp;
                }
                catch
                {
                    img.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconSource.StartsWith("http") ? iconSource : $"https://www.google.com/s2/favicons?sz=128&domain={GetCleanDomain(url)}"));
                }
            }
            else if (!string.IsNullOrEmpty(iconSource) && iconSource.StartsWith("http"))
            {
                img.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconSource));
            }
            else
            {
                img.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri($"https://www.google.com/s2/favicons?sz=128&domain={GetCleanDomain(url)}"));
            }

            btn.Content = img;

            var txt = new TextBlock
            {
                Text = title,
                FontSize = 10.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF")),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 76
            };

            if (onSelect != null)
            {
                btn.Click += (s, e) => onSelect(url, title, iconSource);
                txt.PreviewMouseLeftButtonDown += (s, e) => onSelect(url, title, iconSource);
                stack.PreviewMouseLeftButtonDown += (s, e) => onSelect(url, title, iconSource);
            }

            stack.Children.Add(btn);
            stack.Children.Add(txt);
            return stack;
        }

        private void PopulateVolAppsList(string filter)
        {
            try
            {
                if (ListVolApps == null) return;
                var apps = (_allInstalledApps ?? new List<InstalledApp>()).ToList();
                if (!string.IsNullOrWhiteSpace(filter))
                {
                    apps = apps.Where(a => a.DisplayName != null && a.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
                }
                ListVolApps.ItemsSource = apps;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating VolApps: {ex.Message}");
            }
        }

        private void ListVolApps_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (ListVolApps.SelectedItem is InstalledApp app)
                {
                    AssignVolAction("App", app.ShortcutPath, app.DisplayName, "app");
                    ListVolApps.SelectedIndex = -1;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in ListVolApps_SelectionChanged: {ex.Message}");
            }
        }

        private void BtnVolPopularWebsite_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button btn && btn.Tag is string tag)
                {
                    string[] parts = tag.Split('|');
                    if (parts.Length == 2)
                    {
                        string url = parts[0];
                        string name = parts[1];
                        AssignVolAction("URL", url, name, "url");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in BtnVolPopularWebsite_Click: {ex.Message}");
            }
        }

        private void PopulateVolControlsList()
        {
            try
            {
                if (PanelVolControlsContainer == null) return;
                PanelVolControlsContainer.Children.Clear();

                var systemActions = new List<SystemActionItem>
                {
                    // Media Controls
                    new SystemActionItem { Category = "Media", ActionId = "media_play_pause", Label = "Play/Pause" },
                    new SystemActionItem { Category = "Media", ActionId = "media_next", Label = "Next" },
                    new SystemActionItem { Category = "Media", ActionId = "media_prev", Label = "Previous" },
                    new SystemActionItem { Category = "Media", ActionId = "media_forward_10", Label = "Skip 10s" },
                    new SystemActionItem { Category = "Media", ActionId = "media_backward_10", Label = "Back 10s" },
                    new SystemActionItem { Category = "Media", ActionId = "volume_up", Label = "Vol Up" },
                    new SystemActionItem { Category = "Media", ActionId = "volume_down", Label = "Vol Down" },
                    new SystemActionItem { Category = "Media", ActionId = "volume_mute", Label = "Mute" },

                    // System Actions
                    new SystemActionItem { Category = "System", ActionId = "pc_lock", Label = "Lock PC" },
                    new SystemActionItem { Category = "System", ActionId = "pc_shutdown", Label = "Power Off" },
                    new SystemActionItem { Category = "System", ActionId = "pc_restart", Label = "Restart PC" },
                    new SystemActionItem { Category = "System", ActionId = "pc_sleep", Label = "Hibernate PC" },
                    new SystemActionItem { Category = "System", ActionId = "home_screen", Label = "Home Screen" },
                    new SystemActionItem { Category = "System", ActionId = "close_all_apps", Label = "Close All Apps" },

                    // Presentation Mode
                    new SystemActionItem { Category = "Presentation Mode", ActionId = "presentation_mode", Label = "Presentation" },

                    // Display & Connectivity
                    new SystemActionItem { Category = "Display", ActionId = "brightness_up", Label = "Bright Up" },
                    new SystemActionItem { Category = "Display", ActionId = "brightness_down", Label = "Bright Down" },
                    new SystemActionItem { Category = "Connectivity", ActionId = "wifi_toggle", Label = "Toggle Wi-Fi" },
                    new SystemActionItem { Category = "Connectivity", ActionId = "bluetooth_toggle", Label = "Toggle Bluetooth" },

                    // Audio & Input
                    new SystemActionItem { Category = "Audio & Input", ActionId = "mic_toggle", Label = "Mic" },
                    new SystemActionItem { Category = "Audio & Input", ActionId = "screen_record", Label = "Screen Record" },
                    new SystemActionItem { Category = "Audio & Input", ActionId = "screenshot", Label = "Screenshot" },

                    // Monitoring
                    new SystemActionItem { Category = "Monitoring", ActionId = "perf_cpu", Label = "CPU Usage" },
                    new SystemActionItem { Category = "Monitoring", ActionId = "perf_gpu", Label = "GPU Usage" },
                    new SystemActionItem { Category = "Monitoring", ActionId = "perf_ram", Label = "RAM Usage" },
                    new SystemActionItem { Category = "Monitoring", ActionId = "perf_temp", Label = "PC Temp" },
                    new SystemActionItem { Category = "Monitoring", ActionId = "perf_wifi", Label = "WiFi Speed" }
                };

                var categories = systemActions.Select(a => a.Category).Distinct().ToList();
                bool isFirst = true;

                var iconConverter = (IValueConverter)FindResource("IconElementConverter");

                foreach (var category in categories)
                {
                    var items = systemActions.Where(a => a.Category == category).ToList();

                    if (!isFirst)
                    {
                        var divider = new Border
                        {
                            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C1C24")),
                            BorderThickness = new Thickness(0, 1, 0, 0),
                            Margin = new Thickness(0, 4, 0, 14)
                        };
                        PanelVolControlsContainer.Children.Add(divider);
                    }
                    isFirst = false;

                    var header = new TextBlock
                    {
                        Text = category,
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF")),
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                        Margin = new Thickness(0, 0, 0, 12)
                    };
                    PanelVolControlsContainer.Children.Add(header);

                    var wrapPanel = new WrapPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Horizontal,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                        Margin = new Thickness(0, 0, 0, 12)
                    };

                    foreach (var item in items)
                    {
                        var itemPanel = new StackPanel
                        {
                            Orientation = System.Windows.Controls.Orientation.Vertical,
                            Width = 76,
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                            Margin = new Thickness(4, 0, 4, 12)
                        };

                        var border = new Border
                        {
                            Width = 76,
                            Height = 76,
                            CornerRadius = new CornerRadius(16),
                            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111116")),
                            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C1C24")),
                            BorderThickness = new Thickness(1.5),
                            SnapsToDevicePixels = true,
                            Tag = item,
                            Cursor = System.Windows.Input.Cursors.Hand
                        };

                        if (iconConverter != null)
                        {
                            var iconElement = iconConverter.Convert(item.ActionId, typeof(object), null, System.Globalization.CultureInfo.CurrentCulture) as UIElement;
                            if (iconElement is FrameworkElement fe)
                            {
                                fe.Width = 34;
                                fe.Height = 34;
                                fe.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
                                fe.VerticalAlignment = VerticalAlignment.Center;
                            }
                            border.Child = iconElement;
                        }

                        border.MouseEnter += (s, ev) =>
                        {
                            border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E28"));
                            border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B82F6"));
                        };
                        border.MouseLeave += (s, ev) =>
                        {
                            border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111116"));
                            border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C1C24"));
                        };

                        string actionId = item.ActionId;
                        string labelText = item.Label;

                        border.MouseLeftButtonDown += (s, ev) =>
                        {
                            AssignVolAction("System", actionId, labelText, actionId);
                        };
                        itemPanel.MouseLeftButtonDown += (s, ev) =>
                        {
                            AssignVolAction("System", actionId, labelText, actionId);
                        };

                        var label = new TextBlock
                        {
                            Text = item.Label,
                            FontSize = 10.5,
                            FontWeight = FontWeights.Medium,
                            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF")),
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                            Margin = new Thickness(0, 6, 0, 0),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            MaxWidth = 76
                        };
                        itemPanel.Children.Add(border);
                        itemPanel.Children.Add(label);

                        wrapPanel.Children.Add(itemPanel);
                    }

                    PanelVolControlsContainer.Children.Add(wrapPanel);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating VolControls: {ex.Message}");
            }
        }

        private void BtnAssignVolUrl_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string url = TxtVolUrlSearch?.Text.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(url))
                {
                    _isVolKeyUrlConfigMode = true;
                    var (sUrl, sTitle, _) = GetSuggestedWebsiteInfo(url);
                    OpenUrlConfigModal(sUrl, sTitle);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error assigning VolUrl: {ex.Message}");
            }
        }

        private void PopulateVolProfilesList()
        {
            try
            {
                if (ListVolProfiles == null) return;
                ListVolProfiles.ItemsSource = (ConfigManager.Current.Profiles ?? new List<Profile>()).ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating VolProfiles: {ex.Message}");
            }
        }

        private void ListVolProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (ListVolProfiles.SelectedItem is Profile p)
                {
                    AssignVolAction("Profile", p.Id, $"Switch to {p.Name}", "profile");
                    ListVolProfiles.SelectedIndex = -1;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in ListVolProfiles_SelectionChanged: {ex.Message}");
            }
        }

        private void AssignVolAction(string actionType, string actionData, string title, string icon)
        {
            try
            {
                var btn = new ShortcutButton
                {
                    ActionType = actionType,
                    ActionData = actionData,
                    Title = title,
                    Icon = icon
                };

                if (_editingVolumeKeyTarget == "VolumeUp")
                {
                    ConfigManager.Current.VolumeUpButton = btn;
                }
                else if (_editingVolumeKeyTarget == "VolumeDown")
                {
                    ConfigManager.Current.VolumeDownButton = btn;
                }

                ConfigManager.Save();
                TriggerConfigSync();
                BtnBackToVolumeList_Click(null, null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error assigning VolAction: {ex.Message}");
            }
        }

        private void BtnResetVolumeUp_Click(object sender, RoutedEventArgs e)
        {
            ConfigManager.Current.VolumeUpButton = new ShortcutButton { Title = "Volume Up", ActionType = "System", ActionData = "volume_up" };
            ConfigManager.Save();
            TriggerConfigSync();
            UpdateVolumeButtonsModalLabels();
        }

        private void BtnResetVolumeDown_Click(object sender, RoutedEventArgs e)
        {
            ConfigManager.Current.VolumeDownButton = new ShortcutButton { Title = "Volume Down", ActionType = "System", ActionData = "volume_down" };
            ConfigManager.Save();
            TriggerConfigSync();
            UpdateVolumeButtonsModalLabels();
        }



        private void InitializePerformanceMonitoring()
        {
            _perfTimer = new DispatcherTimer();
            _perfTimer.Interval = TimeSpan.FromSeconds(2);
            _perfTimer.Tick += PerfTimer_Tick;
            _perfTimer.Start();
        }

        private bool _isQueryingMetrics = false;

        private void PerfTimer_Tick(object? sender, EventArgs e)
        {
            if (_isQueryingMetrics) return;
            _isQueryingMetrics = true;

            Task.Run(() =>
            {
                try
                {
                    int cpu = SystemMetrics.GetCpuUsage();
                    int ram = SystemMetrics.GetRamUsage();
                    int gpu = SystemMetrics.GetGpuUsage();
                    int temp = SystemMetrics.GetTemperature(cpu);
                    string wifi = SystemMetrics.GetNetworkSpeed();

                    Dispatcher.Invoke(() =>
                    {
                        _currentCpu = cpu;
                        _currentRam = ram;
                        _currentGpu = gpu;
                        _currentTemp = temp;
                        _currentWifi = wifi;

                        if (_server.IsClientConnected)
                        {
                            _server.SendPerformanceUpdate(cpu, gpu, ram, temp, wifi);
                        }
                    });
                }
                catch { }
                finally
                {
                    _isQueryingMetrics = false;
                }
            });
        }



        private async Task UpdateProfileButtonIconGridAsync(bool triggerSyncAfter = true)
        {
            if (_selectedButton == null) return;

            string targetProfileId = _selectedButton.ActionData;
            var targetProfile = ConfigManager.Current.Profiles.Find(p => p.Id == targetProfileId);
            if (targetProfile == null) return;

            var profileButtons = targetProfile.Buttons;
            var targetIcons = new List<string>();

            int count = 0;
            foreach (var btn in profileButtons)
            {
                if (count >= 4) break;

                // Skip profile-switching buttons (leaving the profile button in that particular profile)
                if (btn.ActionType.Equals("Profile", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Check if button is configured
                bool isConfigured = !string.IsNullOrEmpty(btn.ActionData);

                if (!isConfigured)
                    continue;

                // Determine the icon
                string btnIcon = btn.Icon;
                if (string.IsNullOrEmpty(btnIcon) || btnIcon == "default" || btnIcon == "folder")
                {
                    btnIcon = GetDefaultIconForType(btn.ActionType, btn.ActionData);
                }

                targetIcons.Add(btnIcon);
                count++;
            }

            string newIcon;
            if (targetIcons.Count == 0)
            {
                newIcon = "folder";
            }
            else
            {
                newIcon = string.Join("|", targetIcons);
            }

            if (_selectedButton.Icon != newIcon)
            {
                _selectedButton.Icon = newIcon;
                if (triggerSyncAfter)
                {
                    Dispatcher.Invoke(() => TriggerConfigSync());
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        ConfigManager.Save();
                        _isUpdatingUi = true;
                        try
                        {
                            RefreshGridPreview();
                        }
                        finally
                        {
                            _isUpdatingUi = false;
                        }
                        _server.SyncButtons();
                    });
                }
            }
        }

        private void UpdateAllProfileGridIcons()
        {
            var config = ConfigManager.Current;
            if (config.Profiles == null) return;

            foreach (var profile in config.Profiles)
            {
                if (profile.Buttons == null) continue;

                foreach (var btn in profile.Buttons)
                {
                    if (btn.ActionType.Equals("Profile", StringComparison.OrdinalIgnoreCase) && 
                        btn.Icon != null && btn.Icon.Contains("|"))
                    {
                        var targetProfile = config.Profiles.Find(p => p.Id == btn.ActionData);
                        if (targetProfile != null && targetProfile.Buttons != null)
                        {
                            var targetIcons = new List<string>();
                            int count = 0;
                            foreach (var targetBtn in targetProfile.Buttons)
                            {
                                if (count >= 4) break;
                                if (targetBtn.ActionType.Equals("Profile", StringComparison.OrdinalIgnoreCase)) continue;

                                bool isConfigured = !string.IsNullOrEmpty(targetBtn.ActionData);
                                if (!isConfigured) continue;

                                string btnIcon = targetBtn.Icon;
                                if (string.IsNullOrEmpty(btnIcon) || btnIcon == "default" || btnIcon == "folder")
                                {
                                    btnIcon = GetDefaultIconForType(targetBtn.ActionType, targetBtn.ActionData);
                                }
                                targetIcons.Add(btnIcon);
                                count++;
                            }

                            string newIcon = targetIcons.Count == 0 ? "folder" : string.Join("|", targetIcons);
                            btn.Icon = newIcon;
                        }
                    }
                }
            }
        }

        private void SelectProfileTab(string tab)
        {
            if (BtnProfileTabSelect == null || BtnProfileTabKeycap == null) return;
            if (PanelProfileSelectContainer == null || PanelProfileKeycapCustomizer == null) return;

            if (tab == "SelectConfig")
            {
                BtnProfileTabSelect.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x24));
                BtnProfileTabSelect.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
                BtnProfileTabSelect.Foreground = new SolidColorBrush(Colors.White);

                BtnProfileTabKeycap.Background = System.Windows.Media.Brushes.Transparent;
                BtnProfileTabKeycap.BorderBrush = System.Windows.Media.Brushes.Transparent;
                BtnProfileTabKeycap.Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));

                PanelProfileSelectContainer.Visibility = Visibility.Visible;
                PanelProfileKeycapCustomizer.Visibility = Visibility.Collapsed;
            }
            else if (tab == "ButtonConfig")
            {
                BtnProfileTabKeycap.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x24));
                BtnProfileTabKeycap.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
                BtnProfileTabKeycap.Foreground = new SolidColorBrush(Colors.White);

                BtnProfileTabSelect.Background = System.Windows.Media.Brushes.Transparent;
                BtnProfileTabSelect.BorderBrush = System.Windows.Media.Brushes.Transparent;
                BtnProfileTabSelect.Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));

                PanelProfileSelectContainer.Visibility = Visibility.Collapsed;
                PanelProfileKeycapCustomizer.Visibility = Visibility.Visible;
            }
        }

        private void BtnProfileTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tabName)
            {
                SelectProfileTab(tabName);
            }
        }

        private void SelectGeneralActionTab(string tab)
        {
            if (BtnActionTabSelect == null || BtnActionTabKeycap == null) return;
            if (PanelGeneralActionSelectContainer == null || PanelGeneralKeycapCustomizer == null) return;

            if (tab == "ActionSelect")
            {
                BtnActionTabSelect.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x24));
                BtnActionTabSelect.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
                BtnActionTabSelect.Foreground = new SolidColorBrush(Colors.White);

                BtnActionTabKeycap.Background = System.Windows.Media.Brushes.Transparent;
                BtnActionTabKeycap.BorderBrush = System.Windows.Media.Brushes.Transparent;
                BtnActionTabKeycap.Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));

                PanelGeneralActionSelectContainer.Visibility = Visibility.Visible;
                PanelGeneralKeycapCustomizer.Visibility = Visibility.Collapsed;
            }
            else if (tab == "ActionKeycap")
            {
                BtnActionTabKeycap.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x24));
                BtnActionTabKeycap.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
                BtnActionTabKeycap.Foreground = new SolidColorBrush(Colors.White);

                BtnActionTabSelect.Background = System.Windows.Media.Brushes.Transparent;
                BtnActionTabSelect.BorderBrush = System.Windows.Media.Brushes.Transparent;
                BtnActionTabSelect.Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));

                PanelGeneralActionSelectContainer.Visibility = Visibility.Collapsed;
                PanelGeneralKeycapCustomizer.Visibility = Visibility.Visible;

                UpdateGeneralActionKeycapPanel();
            }
        }

        private void BtnActionTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tabName)
            {
                SelectGeneralActionTab(tabName);
            }
        }

        private void SelectComboGeneralItemByTag(string tag)
        {
            if (ComboGeneralButtonIconType == null) return;
            foreach (var item in ComboGeneralButtonIconType.Items)
            {
                if (item is ComboBoxItem cbi && Equals(cbi.Tag, tag))
                {
                    ComboGeneralButtonIconType.SelectedItem = cbi;
                    return;
                }
            }
        }

        private void UpdateTargetActionInfoHeader()
        {
            if (_selectedButton == null) return;

            string actionType = _selectedButton.ActionType ?? "App";
            string actionData = _selectedButton.ActionData ?? "";
            string title = !string.IsNullOrEmpty(_selectedButton.Title) ? _selectedButton.Title : actionData;

            string badgeText = actionType.ToLowerInvariant() switch
            {
                "app" => "App",
                "url" => "Web",
                "system" => "Control",
                "profile" => "Profile",
                "hotkey" => "Hotkey",
                _ => "Action"
            };

            string detailText = !string.IsNullOrEmpty(actionData) ? actionData : "Configured action for this slot";

            if (LblGeneralTargetActionBadge != null) LblGeneralTargetActionBadge.Text = badgeText;
            if (LblGeneralTargetActionTitle != null) LblGeneralTargetActionTitle.Text = string.IsNullOrEmpty(title) ? "Selected Action" : title;
            if (LblGeneralTargetActionDetail != null) LblGeneralTargetActionDetail.Text = detailText;

            if (LblProfileTargetActionBadge != null) LblProfileTargetActionBadge.Text = badgeText;
            if (LblProfileTargetActionTitle != null) LblProfileTargetActionTitle.Text = string.IsNullOrEmpty(title) ? "Selected Action" : title;
            if (LblProfileTargetActionDetail != null) LblProfileTargetActionDetail.Text = detailText;
        }

        private void RenderLivePreviewIcon(Grid? containerGrid)
        {
            if (containerGrid == null || _selectedButton == null) return;
            containerGrid.Children.Clear();

            string iconToRender = _selectedButton.Icon;
            if (string.IsNullOrEmpty(iconToRender) || iconToRender == "default")
            {
                iconToRender = GetDefaultIconForType(_selectedButton.ActionType, _selectedButton.ActionData);
            }

            var iconElement = CreateIconElement(iconToRender, 42, System.Windows.HorizontalAlignment.Center, VerticalAlignment.Center, new Thickness(0));
            if (iconElement != null)
            {
                containerGrid.Children.Add(iconElement);
            }
        }

        private void UpdateKeycapLivePreviews()
        {
            if (_selectedButton == null) return;

            UpdateTargetActionInfoHeader();

            string hexColor = !string.IsNullOrEmpty(_selectedButton.Color) ? _selectedButton.Color : "#FFFFFF";
            try
            {
                bool isAccent = !string.IsNullOrEmpty(hexColor) 
                    && !hexColor.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase) 
                    && !hexColor.Equals("#000000", StringComparison.OrdinalIgnoreCase)
                    && !hexColor.Equals("None", StringComparison.OrdinalIgnoreCase);

                var brush = isAccent 
                    ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor))
                    : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252538"));

                if (BorderGeneralLivePreviewFrame != null) BorderGeneralLivePreviewFrame.BorderBrush = brush;
                if (BorderProfileLivePreviewFrame != null) BorderProfileLivePreviewFrame.BorderBrush = brush;
            }
            catch { }

            if (TxtGeneralKeycapTitleEdit != null && LblGeneralLivePreviewTitle != null)
            {
                if (TxtGeneralKeycapTitleEdit.Text != _selectedButton.Title) TxtGeneralKeycapTitleEdit.Text = _selectedButton.Title ?? "";
                LblGeneralLivePreviewTitle.Text = string.IsNullOrEmpty(_selectedButton.Title) ? "Keycap Label" : _selectedButton.Title;
            }

            if (TxtProfileKeycapTitleEdit != null && LblProfileLivePreviewTitle != null)
            {
                if (TxtProfileKeycapTitleEdit.Text != _selectedButton.Title) TxtProfileKeycapTitleEdit.Text = _selectedButton.Title ?? "";
                LblProfileLivePreviewTitle.Text = string.IsNullOrEmpty(_selectedButton.Title) ? "Keycap Label" : _selectedButton.Title;
            }

            RenderLivePreviewIcon(GridGeneralLivePreviewContainer);
            RenderLivePreviewIcon(GridProfileLivePreviewContainer);
        }

        private void BtnApplyKeycap_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedButton == null) return;

            RefreshGridPreview();
            TriggerConfigSync();

            if (LblGeneralApplyStatus != null) LblGeneralApplyStatus.Text = "✓ Changes Applied!";
            if (LblProfileApplyStatus != null) LblProfileApplyStatus.Text = "✓ Changes Applied!";
        }

        private void TxtGeneralKeycapTitleEdit_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_selectedButton == null || _isUpdatingUi) return;
            _selectedButton.Title = TxtGeneralKeycapTitleEdit.Text ?? "";
            if (LblGeneralLivePreviewTitle != null) LblGeneralLivePreviewTitle.Text = string.IsNullOrEmpty(_selectedButton.Title) ? "Keycap Label" : _selectedButton.Title;
            if (LblGeneralApplyStatus != null) LblGeneralApplyStatus.Text = "Draft pending (Click Apply)";
        }

        private void TxtProfileKeycapTitleEdit_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_selectedButton == null || _isUpdatingUi) return;
            _selectedButton.Title = TxtProfileKeycapTitleEdit.Text ?? "";
            if (LblProfileLivePreviewTitle != null) LblProfileLivePreviewTitle.Text = string.IsNullOrEmpty(_selectedButton.Title) ? "Keycap Label" : _selectedButton.Title;
            if (LblProfileApplyStatus != null) LblProfileApplyStatus.Text = "Draft pending (Click Apply)";
        }

        private void BtnEmojiPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content is string emojiStr)
            {
                if (PanelGeneralIconText != null && PanelGeneralIconText.Visibility == Visibility.Visible && TxtGeneralIconText != null)
                {
                    TxtGeneralIconText.Text = emojiStr;
                }
                else if (PanelProfileIconText != null && PanelProfileIconText.Visibility == Visibility.Visible && TxtProfileIconText != null)
                {
                    TxtProfileIconText.Text = emojiStr;
                }
            }
        }

        private void UpdateGeneralActionKeycapPanel()
        {
            if (_selectedButton == null) return;

            UpdateKeycapLivePreviews();

            string iconVal = _selectedButton.Icon ?? "";
            _isUpdatingUi = true;
            try
            {
                if (iconVal.StartsWith("text:"))
                {
                    SelectComboGeneralItemByTag("Text");
                    if (TxtGeneralIconText != null) TxtGeneralIconText.Text = iconVal.Substring(5);
                    if (PanelGeneralIconText != null) PanelGeneralIconText.Visibility = Visibility.Visible;
                    if (PanelGeneralIconFile != null) PanelGeneralIconFile.Visibility = Visibility.Collapsed;
                    if (PanelGeneralIconReicon != null) PanelGeneralIconReicon.Visibility = Visibility.Collapsed;
                }
                else if (iconVal.StartsWith("data:"))
                {
                    SelectComboGeneralItemByTag("File");
                    if (TxtGeneralIconFilePath != null) TxtGeneralIconFilePath.Text = "(custom image)";
                    if (PanelGeneralIconFile != null) PanelGeneralIconFile.Visibility = Visibility.Visible;
                    if (PanelGeneralIconText != null) PanelGeneralIconText.Visibility = Visibility.Collapsed;
                    if (PanelGeneralIconReicon != null) PanelGeneralIconReicon.Visibility = Visibility.Collapsed;
                }
                else
                {
                    SelectComboGeneralItemByTag("Reicon");
                    if (PanelGeneralIconText != null) PanelGeneralIconText.Visibility = Visibility.Collapsed;
                    if (PanelGeneralIconFile != null) PanelGeneralIconFile.Visibility = Visibility.Collapsed;
                    if (PanelGeneralIconReicon != null) PanelGeneralIconReicon.Visibility = Visibility.Visible;
                    RefreshGeneralReiconGrid();
                }
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void ComboGeneralButtonIconType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selectedButton == null) return;

            if (PanelGeneralIconFile != null) PanelGeneralIconFile.Visibility = Visibility.Collapsed;
            if (PanelGeneralIconText != null) PanelGeneralIconText.Visibility = Visibility.Collapsed;
            if (PanelGeneralIconReicon != null) PanelGeneralIconReicon.Visibility = Visibility.Collapsed;

            var selectedItem = ComboGeneralButtonIconType.SelectedItem as ComboBoxItem;
            if (selectedItem == null || selectedItem.Tag == null) return;

            string tag = selectedItem.Tag.ToString()!;
            switch (tag)
            {
                case "Reicon":
                    if (PanelGeneralIconReicon != null) PanelGeneralIconReicon.Visibility = Visibility.Visible;
                    if (TxtGeneralReiconSearch != null) TxtGeneralReiconSearch.Text = "";
                    RefreshGeneralReiconGrid();
                    break;

                case "File":
                    if (PanelGeneralIconFile != null) PanelGeneralIconFile.Visibility = Visibility.Visible;
                    break;

                case "Text":
                    if (PanelGeneralIconText != null) PanelGeneralIconText.Visibility = Visibility.Visible;
                    break;
            }
        }


        private void TxtGeneralReiconSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshGeneralReiconGrid();
        }

        private async void RefreshGeneralReiconGrid()
        {
            if (WrapGeneralReiconGrid == null) return;
            WrapGeneralReiconGrid.Children.Clear();

            string query = TxtGeneralReiconSearch?.Text ?? "";
            var items = await ReiconService.SearchAsync(query, "All");

            foreach (var item in items)
            {
                var itemPanel = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Vertical,
                    Width = 84,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    Margin = new Thickness(6, 6, 6, 12)
                };

                var border = new Border
                {
                    Width = 76,
                    Height = 76,
                    CornerRadius = new CornerRadius(14),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14141C")),
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#22222E")),
                    BorderThickness = new Thickness(1),
                    SnapsToDevicePixels = true,
                    Tag = item,
                    Cursor = System.Windows.Input.Cursors.Hand
                };

                UIElement iconVisual;
                if (!string.IsNullOrEmpty(item.PathData))
                {
                    try
                    {
                        var geom = Geometry.Parse(item.PathData);
                        var vectorPath = new System.Windows.Shapes.Path
                        {
                            Data = geom,
                            Fill = System.Windows.Media.Brushes.White,
                            Width = 28,
                            Height = 28,
                            Stretch = Stretch.Uniform,
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        iconVisual = vectorPath;
                    }
                    catch
                    {
                        iconVisual = new TextBlock
                        {
                            Text = "\uE8A9",
                            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                            FontSize = 26,
                            Foreground = System.Windows.Media.Brushes.White,
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                    }
                }
                else
                {
                    iconVisual = new TextBlock
                    {
                        Text = item.Glyph,
                        FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                        FontSize = 28,
                        Foreground = System.Windows.Media.Brushes.White,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }
                border.Child = iconVisual;

                border.MouseEnter += (s, ev) =>
                {
                    border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#20202C"));
                    border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B3B4E"));
                };
                border.MouseLeave += (s, ev) =>
                {
                    border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14141C"));
                    border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#22222E"));
                };

                border.MouseLeftButtonDown += (s, ev) =>
                {
                    if (border.Tag is ReiconItem selectedItem && _selectedButton != null)
                    {
                        _selectedButton.Icon = selectedItem.Id;
                        RefreshGridPreview();
                        TriggerConfigSync();
                    }
                };

                var lblTxt = new TextBlock
                {
                    Text = item.Name,
                    FontSize = 10.5,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF")),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    Margin = new Thickness(0, 6, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 76
                };

                itemPanel.Children.Add(border);
                itemPanel.Children.Add(lblTxt);

                WrapGeneralReiconGrid.Children.Add(itemPanel);
            }
        }

        private void BtnGeneralBrowseIconFile_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedButton == null) return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Image Files (*.png;*.jpg;*.jpeg;*.svg;*.ico)|*.png;*.jpg;*.jpeg;*.svg;*.ico|All Files (*.*)|*.*",
                Title = "Select Keycap Icon Image"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    byte[] imageBytes = System.IO.File.ReadAllBytes(dialog.FileName);
                    string base64 = Convert.ToBase64String(imageBytes);
                    string ext = System.IO.Path.GetExtension(dialog.FileName).ToLowerInvariant();
                    string mimeType = ext switch
                    {
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".svg" => "image/svg+xml",
                        ".ico" => "image/x-icon",
                        _ => "image/png"
                    };

                    _selectedButton.Icon = $"data:{mimeType};base64,{base64}";
                    if (TxtGeneralIconFilePath != null) TxtGeneralIconFilePath.Text = dialog.FileName;

                    UpdateKeycapLivePreviews();
                    if (LblGeneralApplyStatus != null) LblGeneralApplyStatus.Text = "Draft pending (Click Apply)";
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Failed to load image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void TxtGeneralIconText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_selectedButton == null || _isUpdatingUi) return;

            string text = TxtGeneralIconText.Text ?? "";
            _selectedButton.Icon = "text:" + text;
            UpdateKeycapLivePreviews();
            if (LblGeneralApplyStatus != null) LblGeneralApplyStatus.Text = "Draft pending (Click Apply)";
        }

        private void SelectComboProfileItemByTag(string tag)
        {
            if (ComboProfileButtonIconType == null) return;
            foreach (var item in ComboProfileButtonIconType.Items)
            {
                if (item is ComboBoxItem cbi && Equals(cbi.Tag, tag))
                {
                    ComboProfileButtonIconType.SelectedItem = cbi;
                    return;
                }
            }
        }

        private void ListActionProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selectedButton == null || _isUpdatingUi) return;

            if (ListActionProfiles.SelectedItem is Profile selectedProfile)
            {
                _selectedButton.ActionData = selectedProfile.Id;

                // Only set defaults if they are empty or default values
                if (string.IsNullOrEmpty(_selectedButton.Title) || _selectedButton.Title == "Switch Profile" || _selectedButton.Title == "New Profile" || _selectedButton.Title == "Default Profile" || _selectedButton.Title.StartsWith("Profile "))
                {
                    _selectedButton.Title = selectedProfile.Name;
                }

                if (string.IsNullOrEmpty(_selectedButton.Color))
                {
                    _selectedButton.Color = "#FFFFFF";
                }

                if (ComboProfileButtonIconType != null && ComboProfileButtonIconType.SelectedItem is ComboBoxItem cbi && Equals(cbi.Tag, "Grid"))
                {
                    _ = UpdateProfileButtonIconGridAsync(true);
                }
                else
                {
                    RefreshGridPreview();
                    TriggerConfigSync();
                }
            }
        }

        private void BtnProfileColorBadge_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedButton == null || sender is not Button btn || btn.Tag is not string colorHex) return;
            _selectedButton.Color = colorHex;
            TriggerConfigSync();
            UpdateKeycapLivePreviews();
        }

        private void BtnKeycapColorSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedButton == null || sender is not Button btn || btn.Tag is not string colorHex) return;
            _selectedButton.Color = (colorHex == "None" || string.IsNullOrEmpty(colorHex)) ? "#FFFFFF" : colorHex;
            UpdateKeycapLivePreviews();
            if (LblGeneralApplyStatus != null) LblGeneralApplyStatus.Text = "Draft pending (Click Apply)";
            if (LblProfileApplyStatus != null) LblProfileApplyStatus.Text = "Draft pending (Click Apply)";
        }

        private void ComboProfileButtonIconType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selectedButton == null) return;

            PanelProfileIconApp.Visibility = Visibility.Collapsed;
            PanelProfileIconFile.Visibility = Visibility.Collapsed;
            PanelProfileIconUrl.Visibility = Visibility.Collapsed;
            PanelProfileIconText.Visibility = Visibility.Collapsed;
            if (PanelProfileIconReicon != null) PanelProfileIconReicon.Visibility = Visibility.Collapsed;
            if (PanelProfileIconGrid != null) PanelProfileIconGrid.Visibility = Visibility.Collapsed;

            var selectedItem = ComboProfileButtonIconType.SelectedItem as ComboBoxItem;
            if (selectedItem == null || selectedItem.Tag == null) return;

            string tag = selectedItem.Tag.ToString()!;
            switch (tag)
            {
                case "Default":
                    if (!_isUpdatingUi)
                    {
                        _selectedButton.Icon = "folder";
                        TriggerConfigSync();
                    }
                    break;

                case "Grid":
                    if (PanelProfileIconGrid != null) PanelProfileIconGrid.Visibility = Visibility.Visible;
                    if (!_isUpdatingUi)
                    {
                        _ = UpdateProfileButtonIconGridAsync(true);
                    }
                    break;

                case "Reicon":
                    if (PanelProfileIconReicon != null) PanelProfileIconReicon.Visibility = Visibility.Visible;
                    if (TxtReiconSearch != null) TxtReiconSearch.Text = "";
                    _activeReiconCategory = "All";
                    PopulateReiconCategories();
                    RefreshReiconGrid();
                    break;

                case "App":
                    PanelProfileIconApp.Visibility = Visibility.Visible;
                    if (!_isUpdatingUi) ListProfileIconApps.SelectedIndex = -1;
                    break;

                case "File":
                    PanelProfileIconFile.Visibility = Visibility.Visible;
                    break;

                case "Url":
                    PanelProfileIconUrl.Visibility = Visibility.Visible;
                    break;

                case "Text":
                    PanelProfileIconText.Visibility = Visibility.Visible;
                    break;
            }
        }

        private void ListProfileIconApps_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selectedButton == null || _isUpdatingUi) return;

            if (ListProfileIconApps.SelectedItem is InstalledApp app)
            {
                string? iconBase64 = ImageSourceToBase64Png(app.Icon);
                if (!string.IsNullOrEmpty(iconBase64))
                {
                    _selectedButton.Icon = "data:" + iconBase64;
                }
                else
                {
                    _selectedButton.Icon = "rocket";
                }
                
                RefreshGridPreview();
                TriggerConfigSync();
            }
        }

        private void BtnProfileBrowseIconFile_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedButton == null) return;

            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Image Files (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All Files (*.*)|*.*",
                Title = "Select Keycap Icon Image"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(openFileDialog.FileName);
                    string base64 = Convert.ToBase64String(bytes);
                    
                    string ext = Path.GetExtension(openFileDialog.FileName).ToLowerInvariant();
                    string mimeType = ext switch
                    {
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".svg" => "image/svg+xml",
                        ".ico" => "image/x-icon",
                        _ => "image/png"
                    };

                    _selectedButton.Icon = $"data:{mimeType};base64,{base64}";
                    TxtProfileIconFilePath.Text = Path.GetFileName(openFileDialog.FileName);
                    
                    UpdateKeycapLivePreviews();
                    if (LblProfileApplyStatus != null) LblProfileApplyStatus.Text = "Draft pending (Click Apply)";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to load image file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnProfileDownloadIconUrl_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedButton == null) return;

            string url = TxtProfileIconUrl.Text.Trim();
            if (string.IsNullOrEmpty(url)) return;

            LblProfileIconUrlStatus.Foreground = System.Windows.Media.Brushes.Gray;
            LblProfileIconUrlStatus.Text = "Downloading image...";

            try
            {
                using var client = new System.Net.Http.HttpClient();
                byte[] bytes = await client.GetByteArrayAsync(url);
                string base64 = Convert.ToBase64String(bytes);
                
                _selectedButton.Icon = "data:" + base64;
                
                LblProfileIconUrlStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
                LblProfileIconUrlStatus.Text = "Image downloaded successfully!";
                
                RefreshGridPreview();
                TriggerConfigSync();
            }
            catch (Exception ex)
            {
                LblProfileIconUrlStatus.Foreground = System.Windows.Media.Brushes.Red;
                LblProfileIconUrlStatus.Text = $"Error: {ex.Message}";
            }
        }

        private void TxtProfileIconText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_selectedButton == null || _isUpdatingUi) return;
            string text = TxtProfileIconText.Text;
            _selectedButton.Icon = "text:" + text;
            RefreshGridPreview();
            TriggerConfigSync();
        }

    }



    public class IconElementConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            string actionId = value as string ?? "";
            
            // Map actions to their corresponding icons
            string iconName = actionId;
            if (actionId.Equals("media_play_pause", StringComparison.OrdinalIgnoreCase))
            {
                iconName = "media_play";
            }
            else if (actionId.Equals("wifi_toggle", StringComparison.OrdinalIgnoreCase))
            {
                iconName = "wifi";
            }
            else if (actionId.Contains("bluetooth"))
            {
                iconName = "bluetooth";
            }
            else if (actionId.Equals("mic_toggle", StringComparison.OrdinalIgnoreCase))
            {
                iconName = "mic";
            }
            
            var element = MainWindow.CreateIconElementStatic(iconName, 24, System.Windows.HorizontalAlignment.Center, System.Windows.VerticalAlignment.Center, new Thickness(0), true);
            return element;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class ProfileMiniKeycapConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            int index = 0;
            if (parameter != null)
            {
                int.TryParse(parameter.ToString(), out index);
            }

            var outerGrid = new Grid { Width = 14, Height = 14, Margin = new Thickness(0.5) };
            var bgBorder = new Border
            {
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1F1F28")),
                Width = 14,
                Height = 14
            };
            outerGrid.Children.Add(bgBorder);

            // Slot 0: Settings gear icon
            if (index == 0)
            {
                var icon = MainWindow.CreateIconElementStatic("settings", 10, System.Windows.HorizontalAlignment.Center, System.Windows.VerticalAlignment.Center, new Thickness(0), false);
                outerGrid.Children.Add(icon);
                return outerGrid;
            }

            var buttons = value as List<ShortcutButton>;
            int btnIndex = index - 1;

            if (buttons != null && btnIndex >= 0 && btnIndex < buttons.Count)
            {
                var btn = buttons[btnIndex];
                if (btn != null)
                {
                    UIElement? contentEl = CreateMiniIconContent(btn);
                    if (contentEl != null)
                    {
                        outerGrid.Children.Add(contentEl);
                        return outerGrid;
                    }
                }
            }

            // Empty slot (subtle visible keycap outline)
            bgBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C1C26"));
            bgBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A2A38"));
            bgBorder.BorderThickness = new Thickness(1);
            bgBorder.Opacity = 0.75;
            return outerGrid;
        }

        private UIElement? CreateMiniIconContent(ShortcutButton btn)
        {
            try
            {
                string iconStr = btn.Icon ?? "";
                string actionData = btn.ActionData ?? "";
                string actionType = btn.ActionType?.ToLower() ?? "";

                // 0. Check if target profile is locked
                if (actionType == "profile")
                {
                    var targetProf = ConfigManager.Current.Profiles.Find(p => p.Id.Equals(actionData, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(actionData, StringComparison.OrdinalIgnoreCase));
                    if (targetProf != null && targetProf.IsLocked)
                    {
                        return MainWindow.CreateIconElementStatic("locked_profile", 11, System.Windows.HorizontalAlignment.Center, VerticalAlignment.Center, new Thickness(0));
                    }
                }

                // 1. Check for pipe-separated 2x2 grid icon representation (e.g. Profile grid representation)
                if (!string.IsNullOrEmpty(iconStr) && iconStr.Contains("|"))
                {
                    var parts = iconStr.Split('|', StringSplitOptions.RemoveEmptyEntries);
                    var grid = new Grid
                    {
                        Width = 14,
                        Height = 14,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = System.Windows.VerticalAlignment.Center
                    };

                    int count = Math.Min(parts.Length, 4);
                    int rows = count > 2 ? 2 : 1;
                    int cols = count > 1 ? 2 : 1;

                    for (int r = 0; r < rows; r++)
                        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    for (int c = 0; c < cols; c++)
                        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    for (int idx = 0; idx < count; idx++)
                    {
                        var part = parts[idx].Trim();
                        UIElement subEl;
                        if (part.StartsWith("data:"))
                        {
                            string b64 = part.Substring(5);
                            var imgSource = Base64ToImageSourceStatic(b64);
                            if (imgSource != null)
                            {
                                subEl = new System.Windows.Controls.Image
                                {
                                    Source = imgSource,
                                    Stretch = Stretch.Uniform
                                };
                            }
                            else
                            {
                                subEl = MainWindow.CreateIconElementStatic("app_default", 6, System.Windows.HorizontalAlignment.Center, VerticalAlignment.Center, new Thickness(0));
                            }
                        }
                        else
                        {
                            subEl = MainWindow.CreateIconElementStatic(part, 6, System.Windows.HorizontalAlignment.Center, VerticalAlignment.Center, new Thickness(0));
                        }

                        int rowIdx = rows == 1 ? 0 : (idx / 2);
                        int colIdx = rows == 1 ? idx : (idx % 2);

                        Grid.SetRow(subEl, rowIdx);
                        Grid.SetColumn(subEl, colIdx);
                        grid.Children.Add(subEl);
                    }
                    return grid;
                }

                // 2. Check for Base64 Data PNG (e.g. custom app icon or uploaded icon)
                if (!string.IsNullOrEmpty(iconStr) && iconStr.StartsWith("data:"))
                {
                    string b64 = iconStr.Substring(5);
                    var imgSource = Base64ToImageSourceStatic(b64);
                    if (imgSource != null)
                    {
                        return new System.Windows.Controls.Image
                        {
                            Source = imgSource,
                            Width = 11,
                            Height = 11,
                            Stretch = Stretch.Uniform,
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                            VerticalAlignment = System.Windows.VerticalAlignment.Center
                        };
                    }
                }

                // 3. Check for local image / EXE file path in iconStr or actionData
                string fileCandidate = System.IO.File.Exists(iconStr) ? iconStr : (System.IO.File.Exists(actionData) ? actionData : "");
                if (!string.IsNullOrEmpty(fileCandidate))
                {
                    if (fileCandidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || fileCandidate.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) || fileCandidate.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        var ico = System.Drawing.Icon.ExtractAssociatedIcon(fileCandidate);
                        if (ico != null)
                        {
                            var bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                                ico.Handle, System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                            return new System.Windows.Controls.Image
                            {
                                Source = bitmap,
                                Width = 11,
                                Height = 11,
                                Stretch = Stretch.Uniform,
                                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                                VerticalAlignment = System.Windows.VerticalAlignment.Center
                            };
                        }
                    }
                    else
                    {
                        return new System.Windows.Controls.Image
                        {
                            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(fileCandidate)),
                            Width = 11,
                            Height = 11,
                            Stretch = Stretch.Uniform,
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                            VerticalAlignment = System.Windows.VerticalAlignment.Center
                        };
                    }
                }

                // 4. Try matching App by name in AllInstalledApps
                if (actionType == "app" && !string.IsNullOrEmpty(actionData) && MainWindow.AllInstalledApps != null)
                {
                    var matched = MainWindow.AllInstalledApps.FirstOrDefault(a =>
                        a.DisplayName.Equals(actionData, StringComparison.OrdinalIgnoreCase) ||
                        a.ShortcutPath.Equals(actionData, StringComparison.OrdinalIgnoreCase));

                    if (matched?.Icon != null)
                    {
                        return new System.Windows.Controls.Image
                        {
                            Source = matched.Icon,
                            Width = 11,
                            Height = 11,
                            Stretch = Stretch.Uniform,
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                            VerticalAlignment = System.Windows.VerticalAlignment.Center
                        };
                    }
                }

                // 5. Explicit action type icon fallbacks
                string iconToRender = iconStr;
                if (string.IsNullOrEmpty(iconToRender))
                {
                    if (actionType == "app") iconToRender = "app_default";
                    else if (actionType == "profile") iconToRender = "folder";
                    else if (actionType == "url") iconToRender = "url";
                    else iconToRender = actionData;
                }

                return MainWindow.CreateIconElementStatic(iconToRender, 10, System.Windows.HorizontalAlignment.Center, System.Windows.VerticalAlignment.Center, new Thickness(0), false);
            }
            catch
            {
                return MainWindow.CreateIconElementStatic("app_default", 10, System.Windows.HorizontalAlignment.Center, System.Windows.VerticalAlignment.Center, new Thickness(0), false);
            }
        }

        private static System.Windows.Media.ImageSource? Base64ToImageSourceStatic(string base64String)
        {
            try
            {
                byte[] bytes = System.Convert.FromBase64String(base64String);
                using (var ms = new System.IO.MemoryStream(bytes))
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            catch
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class CustomInputDialog : Window
    {
        private System.Windows.Controls.TextBox _inputBox;
        private System.Windows.Controls.TextBox? _inputBox2;
        public string Result1 { get; private set; } = "";
        public string Result2 { get; private set; } = "";
        private bool _isDouble;

        public CustomInputDialog(string title, string prompt1, string default1, string? prompt2 = null, string? default2 = null)
        {
            Title = title;
            Width = 400;
            Height = prompt2 != null ? 240 : 175;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = System.Windows.Media.Brushes.Transparent;

            // Define custom TextBox template for sleek styling
            var textBoxTemplate = new ControlTemplate(typeof(System.Windows.Controls.TextBox));
            var tbBorder = new FrameworkElementFactory(typeof(Border));
            tbBorder.Name = "Border";
            tbBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            tbBorder.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x28)));
            tbBorder.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3D)));
            tbBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
            var tbContent = new FrameworkElementFactory(typeof(ScrollViewer));
            tbContent.Name = "PART_ContentHost";
            tbBorder.AppendChild(tbContent);
            textBoxTemplate.VisualTree = tbBorder;
            var tbFocusTrigger = new Trigger { Property = System.Windows.Controls.TextBox.IsFocusedProperty, Value = true };
            tbFocusTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), "Border"));
            textBoxTemplate.Triggers.Add(tbFocusTrigger);

            // Define custom Button template
            ControlTemplate GetBtnTemplate(Color bg, Color hoverBg, Color borderCol)
            {
                var template = new ControlTemplate(typeof(Button));
                var btnBorder = new FrameworkElementFactory(typeof(Border));
                btnBorder.Name = "Border";
                btnBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
                btnBorder.SetValue(Border.BackgroundProperty, new SolidColorBrush(bg));
                btnBorder.SetValue(Border.BorderBrushProperty, new SolidColorBrush(borderCol));
                btnBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
                var btnContent = new FrameworkElementFactory(typeof(ContentPresenter));
                btnContent.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
                btnContent.SetValue(ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
                btnBorder.AppendChild(btnContent);
                template.VisualTree = btnBorder;
                var hoverTrigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
                hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(hoverBg), "Border"));
                hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), "Border"));
                template.Triggers.Add(hoverTrigger);
                return template;
            }

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Title bar
            var titleTxt = new TextBlock
            {
                Text = title.ToUpper(),
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
                Margin = new Thickness(0, 0, 0, 15),
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Inter")
            };
            Grid.SetRow(titleTxt, 0);
            grid.Children.Add(titleTxt);

            // Inputs Stack
            var stack = new StackPanel();
            
            var lbl1 = new TextBlock { Text = prompt1, Margin = new Thickness(0, 0, 0, 4), FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)), FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") };
            stack.Children.Add(lbl1);
            _inputBox = new System.Windows.Controls.TextBox 
            { 
                Text = default1, 
                Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)), 
                Padding = new Thickness(8, 6, 8, 6), 
                Margin = new Thickness(0, 0, 0, 12), 
                CaretBrush = System.Windows.Media.Brushes.White,
                Template = textBoxTemplate
            };
            stack.Children.Add(_inputBox);

            if (prompt2 != null)
            {
                _isDouble = true;
                var lbl2 = new TextBlock { Text = prompt2, Margin = new Thickness(0, 0, 0, 4), FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)), FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") };
                stack.Children.Add(lbl2);
                _inputBox2 = new System.Windows.Controls.TextBox 
                { 
                    Text = default2 ?? "", 
                    Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)), 
                    Padding = new Thickness(8, 6, 8, 6), 
                    CaretBrush = System.Windows.Media.Brushes.White,
                    Template = textBoxTemplate
                };
                stack.Children.Add(_inputBox2);
            }

            Grid.SetRow(stack, 1);
            grid.Children.Add(stack);

            // Buttons Bar
            var btnStack = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) };
            
            var okBtn = new Button 
            { 
                Content = "OK", 
                Width = 80, 
                Height = 30, 
                Margin = new Thickness(0, 0, 10, 0), 
                Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
                Cursor = System.Windows.Input.Cursors.Hand,
                Template = GetBtnTemplate(Color.FromRgb(0x25, 0x25, 0x35), Color.FromRgb(0x30, 0x30, 0x45), Color.FromRgb(0x3F, 0x3F, 0x55))
            };
            okBtn.Click += (s, e) =>
            {
                Result1 = _inputBox.Text;
                if (_isDouble && _inputBox2 != null) Result2 = _inputBox2.Text;
                try { DialogResult = true; } catch { }
                Close();
            };
            
            var cancelBtn = new Button 
            { 
                Content = "Cancel", 
                Width = 80, 
                Height = 30, 
                Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
                Cursor = System.Windows.Input.Cursors.Hand,
                Template = GetBtnTemplate(Color.FromRgb(0x1E, 0x1E, 0x28), Color.FromRgb(0x2A, 0x2A, 0x35), Color.FromRgb(0x2A, 0x2A, 0x3D))
            };
            cancelBtn.Click += (s, e) =>
            {
                try { DialogResult = false; } catch { }
                Close();
            };

            btnStack.Children.Add(okBtn);
            btnStack.Children.Add(cancelBtn);

            Grid.SetRow(btnStack, 2);
            grid.Children.Add(btnStack);

            // Outer Container with Drop Shadow
            var border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3D)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x16)),
                Child = grid
            };

            // Glow / Drop Shadow Effect
            var shadow = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(0x00, 0x00, 0x00),
                Direction = 270,
                ShadowDepth = 5,
                Opacity = 0.6,
                BlurRadius = 20
            };
            border.Effect = shadow;

            Content = border;
        }
    }

    public class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is bool b && b)
            {
                return Visibility.Collapsed;
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value is Visibility v && v != Visibility.Visible;
        }
    }

    public class LockedProfileIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var grid = new Grid { Width = 22, Height = 22, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            
            // Folder Outline
            var folder = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M3,4.2 C3,3.54 3.54,3 4.2,3 H8.4 C8.82,3 9.22,3.17 9.52,3.47 L11.7,5.7 H19.8 C20.46,5.7 21,6.24 21,6.9 V18.3 C21,18.96 20.46,19.5 19.8,19.5 H4.2 C3.54,19.5 3,18.96 3,18.3 V4.2 Z"),
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 1.6,
                StrokeLineJoin = PenLineJoin.Round,
                Stretch = Stretch.Uniform,
                Width = 20,
                Height = 20
            };

            // Lock Shackle
            var shackle = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M9.7,11.7 V9.9 C9.7,8.65 10.72,7.7 12,7.7 C13.28,7.7 14.3,8.65 14.3,9.9 V11.7"),
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 1.4,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeStartLineCap = PenLineCap.Round,
                Stretch = Stretch.Uniform,
                Width = 10,
                Height = 8,
                Margin = new Thickness(0, 2, 0, 0)
            };

            // Lock Body
            var body = new Border
            {
                Width = 9,
                Height = 8,
                CornerRadius = new CornerRadius(1.5),
                Background = new SolidColorBrush(Colors.White),
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };

            // Keyhole Cutout
            var keyhole = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M12,12.85 C12.39,12.85 12.7,13.16 12.7,13.55 C12.7,13.83 12.54,14.07 12.3,14.18 V15.35 H11.7 V14.18 C11.46,14.07 11.3,13.83 11.3,13.55 C11.3,13.16 11.61,12.85 12,12.85 Z"),
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111116")),
                Stretch = Stretch.Uniform,
                Width = 3,
                Height = 4,
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };

            grid.Children.Add(folder);
            grid.Children.Add(shackle);
            grid.Children.Add(body);
            grid.Children.Add(keyhole);
            return grid;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value;
        }
    }
}
