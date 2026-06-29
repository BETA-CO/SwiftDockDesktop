using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
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

        public MainWindow()
        {
            InitializeComponent();
            ConfigManager.Load();
            InitializeSystemTray();

            // Wire up server events
            _server.PinGenerated += OnPinGenerated;
            _server.ClientConnected += OnClientConnected;
            _server.ClientDisconnected += OnClientDisconnected;
            _server.PairingSuccessful += OnPairingSuccessful;
            _server.ProfileChangeRequested += OnProfileChangeRequested;
            _server.LayoutChanged += OnLayoutChanged;
            _server.PageChangeRequested += OnPageChangeRequested;

            LoadInstalledAppsAsync();
            ShowDisconnectedPanel();
            InitializeMediaMonitoring();
            InitializeRadioMonitoring();
            InitializePerformanceMonitoring();
            InitializeSingleInstanceListener();
            _ = CheckForUpdatesAsync(showUpToDatePrompt: false);
        }

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
            _notifyIcon.DoubleClick += (s, e) =>
            {
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();
            };

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Open Customizer", null, (s, e) =>
            {
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();
            });
            contextMenu.Items.Add("Exit", null, (s, e) =>
            {
                _isExiting = true;
                this.Close();
            });

            _notifyIcon.ContextMenuStrip = contextMenu;
        }

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
                RefreshConnectionHistory();

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
            if (GridSidebarProfiles != null) GridSidebarProfiles.Visibility = Visibility.Visible;
            if (GridSidebarSettings != null) GridSidebarSettings.Visibility = Visibility.Collapsed;
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
            RefreshGridPreview();
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
                        var currentVersion = new Version("1.1.2");
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
                    System.Windows.MessageBox.Show("You are currently running the latest version of SwiftDock (v1.1.2).", 
                        "Check for Updates", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
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
                    FileName = "https://github.com/",
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
                    FileName = "https://github.com/",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        // Shortcuts Editor Logic
        private void SelectShortcutButton(ShortcutButton? btn)
        {
            if (_isUpdatingUi) return;

            _selectedButton = btn;

            if (_selectedButton == null)
            {
                PanelButtonProperties.Visibility = Visibility.Collapsed;
                if (PanelNoSelectionPlaceholder != null)
                {
                    PanelNoSelectionPlaceholder.Visibility = Visibility.Visible;
                }
                RefreshGridPreview();
                return;
            }

            // Calculate the page index for the selected button
            var buttons = ConfigManager.CurrentButtons;
            int selectedIndex = buttons.IndexOf(_selectedButton);
            int previewSelectedIndex = selectedIndex >= 0 ? selectedIndex + 1 : -1;
            if (previewSelectedIndex >= 0)
            {
                _currentGridPage = previewSelectedIndex / 8;
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

                // Highlight active category tab
                UpdateCategoryTabsHighlight(_selectedButton.ActionType);

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
            
            TabBtnMacro.Background = System.Windows.Media.Brushes.Transparent;
            TabBtnMacro.Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));

            Button? activeBtn = null;
            switch (actionType?.ToLower())
            {
                case "app": activeBtn = TabBtnApp; break;
                case "url": activeBtn = TabBtnCtrl; break;
                case "system": activeBtn = TabBtnSetting; break;
                case "macro": activeBtn = TabBtnMacro; break;
            }

            if (activeBtn != null)
            {
                activeBtn.Background = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x4C));
                activeBtn.Foreground = System.Windows.Media.Brushes.White;
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
            if (BtnDeleteSelectedBulk != null)
            {
                if (_selectedBulkButtons.Count > 0)
                {
                    BtnDeleteSelectedBulk.Visibility = Visibility.Visible;
                    BtnDeleteSelectedBulk.Content = $"Delete Selected ({_selectedBulkButtons.Count})";
                }
                else
                {
                    BtnDeleteSelectedBulk.Visibility = Visibility.Collapsed;
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
                    Width = 85,
                    Height = 72,
                    Margin = new Thickness(4),
                    CornerRadius = new CornerRadius(12),
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
                            Color.FromRgb(0x0E, 0x0E, 0x14),
                            Color.FromRgb(0x04, 0x04, 0x06),
                            new Point(0, 0),
                            new Point(1, 1)
                        );
                        cell.BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
                        cell.BorderThickness = new Thickness(1.5);
                        
                        TextBlock txt = new TextBlock
                        {
                            Text = "\uE713", // Gear settings icon glyph in Segoe MDL2 Assets
                            FontFamily = new FontFamily("Segoe MDL2 Assets"),
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            FontSize = 30,
                            FontWeight = FontWeights.Normal,
                            Foreground = new SolidColorBrush(Colors.White)
                        };
                        cell.Child = txt;
                    }
                    else if (item is ShortcutButton btn)
                    {
                        // Selected state border highlight
                        if (btn == _selectedButton)
                        {
                            cell.BorderBrush = new SolidColorBrush(Colors.White);
                            cell.BorderThickness = new Thickness(2);
                        }
                        else
                        {
                            cell.BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
                            cell.BorderThickness = new Thickness(1.5);
                        }

                        cell.Background = new LinearGradientBrush(
                            Color.FromRgb(0x0E, 0x0E, 0x14),
                            Color.FromRgb(0x04, 0x04, 0x06),
                            new Point(0, 0),
                            new Point(1, 1)
                        );
                        cell.Cursor = System.Windows.Input.Cursors.Hand;
                        cell.Tag = btn;

                        cell.ContextMenu = CreateCellContextMenu(btn);
                        System.Windows.Controls.ContextMenuService.SetPlacement(cell, System.Windows.Controls.Primitives.PlacementMode.MousePoint);
                        
                        cell.MouseDown += (s, e) =>
                        {
                            var clickedBtn = (ShortcutButton)((Border)s).Tag;
                            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                            {
                                if (_selectedBulkButtons.Count > 0)
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

                        // Check if icon is multiple pipe-separated icons or a base64 app icon
                        if (btn.Icon != null && btn.Icon.Contains("|"))
                        {
                            var parts = btn.Icon.Split('|', StringSplitOptions.RemoveEmptyEntries);
                            var grid = new Grid
                            {
                                Width = 44,
                                Height = 44,
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
                                        itemEl = CreateIconElement("url", 14, System.Windows.HorizontalAlignment.Center, VerticalAlignment.Center, new Thickness(0));
                                    }
                                }
                                else
                                {
                                    itemEl = CreateIconElement(part, 14, System.Windows.HorizontalAlignment.Center, VerticalAlignment.Center, new Thickness(0));
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
                                    Width = 40,
                                    Height = 40,
                                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                                    VerticalAlignment = VerticalAlignment.Center
                                };
                                cell.Child = img;
                            }
                            else
                            {
                                cell.Child = CreateIconElement(btn.Icon, 30, System.Windows.HorizontalAlignment.Center, VerticalAlignment.Center, new Thickness(0));
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
                                cell.Child = CreateIconElement(btn.Icon, 30, System.Windows.HorizontalAlignment.Center, VerticalAlignment.Center, new Thickness(0));
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
                    cell.MouseDown += (s, e) =>
                    {
                        if (e.ChangedButton == System.Windows.Input.MouseButton.Left && _selectedBulkButtons.Count == 0)
                        {
                            BtnAddButton_Click(null!, null!);
                        }
                        else if (e.ChangedButton == System.Windows.Input.MouseButton.Right)
                        {
                            e.Handled = true;
                        }
                    };
                    cell.MouseUp += (s, e) =>
                    {
                        if (e.ChangedButton == System.Windows.Input.MouseButton.Right)
                        {
                            e.Handled = true;
                        }
                    };
                }

                GridPreview.Children.Add(cell);
            }
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
                Header = isBulkSelected ? "Deselect Shortcut" : "Select (for Bulk Delete)"
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

        private void BtnPrevPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentGridPage > 0)
            {
                _currentGridPage--;
                RefreshGridPreview();
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
                case "default":
                case "app_default":
                    return "M13,2 L4,13 H12 L11,22 L20,11 H12 Z";
                default:
                    return "";
            }
        }

        public static UIElement CreateIconElementStatic(string? iconName, double fontSize, System.Windows.HorizontalAlignment horizAlign, VerticalAlignment vertAlign, Thickness margin, bool isForPicker = false)
        {
            var brush = isForPicker ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B")) : new SolidColorBrush(Colors.White);

            if (iconName != null && iconName.StartsWith("text:"))
            {
                string txt = iconName.Substring(5);
                return new TextBlock
                {
                    Text = txt,
                    FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Inter, Segoe UI Emoji"),
                    HorizontalAlignment = horizAlign,
                    VerticalAlignment = vertAlign,
                    FontSize = txt.Length <= 2 ? fontSize * 1.0 : fontSize * 0.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = brush,
                    Margin = margin,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                };
            }

            string pathData = GetSvgPathForIconName(iconName ?? "");

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

                if (iconName != null && (iconName.Equals("settings", StringComparison.OrdinalIgnoreCase) ||
                                         iconName.Equals("bluetooth", StringComparison.OrdinalIgnoreCase) ||
                                         iconName.Equals("bluetooth_off", StringComparison.OrdinalIgnoreCase) ||
                                         iconName.Equals("folder", StringComparison.OrdinalIgnoreCase) ||
                                         iconName.Equals("macro", StringComparison.OrdinalIgnoreCase) ||
                                         iconName.Equals("wifi", StringComparison.OrdinalIgnoreCase) ||
                                         iconName.Equals("wifi_off", StringComparison.OrdinalIgnoreCase) ||
                                         iconName.Equals("perf_wifi", StringComparison.OrdinalIgnoreCase)))
                {
                    strokeFill.Fill = brush;
                    strokeFill.StrokeThickness = 0;
                }
                
                return strokeFill;
            }

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
                case "camera": return "\uE722";
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
                default: return "\uE945";
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
        }

        private void LoadActionDetails()
        {
            if (_selectedButton == null) return;

            // Show/hide subpanels
            PanelActionParameter.Visibility = Visibility.Visible;
            PanelMacroSequence.Visibility = Visibility.Collapsed;
            ListSystemActions.Visibility = Visibility.Collapsed;
            TxtActionData.Visibility = Visibility.Collapsed;
            if (ScrollUrlLinks != null) ScrollUrlLinks.Visibility = Visibility.Collapsed;
            if (ListInstalledApps != null) ListInstalledApps.Visibility = Visibility.Collapsed;

            if (_selectedButton.ActionType.Equals("Macro", StringComparison.OrdinalIgnoreCase))
            {
                PanelActionParameter.Visibility = Visibility.Collapsed;
                PanelMacroSequence.Visibility = Visibility.Visible;
                
                _isUpdatingUi = true;
                try
                {
                    ListMacroSteps.ItemsSource = null;
                    ListMacroSteps.ItemsSource = _selectedButton.MacroSteps;
                    ListMacroSteps.SelectedIndex = -1;
                    
                    if (PanelMacroStepEditor != null) PanelMacroStepEditor.Visibility = Visibility.Collapsed;
                    if (PanelMacroNoStepSelected != null) PanelMacroNoStepSelected.Visibility = Visibility.Visible;
                    
                    // Pre-populate apps, system actions, command presets for the editors
                    if (ListInstalledApps != null && ListMacroStepApps != null)
                    {
                        ListMacroStepApps.ItemsSource = ListInstalledApps.ItemsSource;
                    }
                    
                    if (ListMacroStepSystem != null)
                    {
                        var systemActions = new List<SystemActionItem>
                        {
                            new SystemActionItem { ActionId = "volume_up", Label = "Vol Up", Glyph = GetGlyphForIcon("volume_up") },
                            new SystemActionItem { ActionId = "volume_down", Label = "Vol Down", Glyph = GetGlyphForIcon("volume_down") },
                            new SystemActionItem { ActionId = "volume_mute", Label = "Mute", Glyph = GetGlyphForIcon("volume_mute") },
                            new SystemActionItem { ActionId = "media_play_pause", Label = "Play/Pause", Glyph = GetGlyphForIcon("media_play") },
                            new SystemActionItem { ActionId = "media_next", Label = "Next", Glyph = GetGlyphForIcon("media_next") },
                            new SystemActionItem { ActionId = "media_prev", Label = "Previous", Glyph = GetGlyphForIcon("media_prev") },
                            new SystemActionItem { ActionId = "media_forward_10", Label = "Skip 10s", Glyph = GetGlyphForIcon("media_forward_10") },
                            new SystemActionItem { ActionId = "media_backward_10", Label = "Back 10s", Glyph = GetGlyphForIcon("media_backward_10") },
                            new SystemActionItem { ActionId = "brightness_up", Label = "Bright Up", Glyph = GetGlyphForIcon("brightness_up") },
                            new SystemActionItem { ActionId = "brightness_down", Label = "Bright Down", Glyph = GetGlyphForIcon("brightness_down") },
                            new SystemActionItem { ActionId = "mic_toggle", Label = "Mic", Glyph = GetGlyphForIcon("mic") },
                            new SystemActionItem { ActionId = "pc_shutdown", Label = "Power Off", Glyph = GetGlyphForIcon("pc_shutdown") },
                            new SystemActionItem { ActionId = "pc_sleep", Label = "Hibernate PC", Glyph = GetGlyphForIcon("pc_sleep") },
                            new SystemActionItem { ActionId = "pc_lock", Label = "Lock PC", Glyph = GetGlyphForIcon("pc_lock") },
                            new SystemActionItem { ActionId = "pc_restart", Label = "Restart PC", Glyph = GetGlyphForIcon("pc_restart") },
                            new SystemActionItem { ActionId = "wifi_toggle", Label = "Toggle Wi-Fi", Glyph = GetGlyphForIcon("wifi") },
                            new SystemActionItem { ActionId = "bluetooth_toggle", Label = "Toggle Bluetooth", Glyph = GetGlyphForIcon("bluetooth") },
                            new SystemActionItem { ActionId = "screen_record", Label = "Screen Record", Glyph = GetGlyphForIcon("screen_record") },
                            new SystemActionItem { ActionId = "screenshot", Label = "Screenshot", Glyph = GetGlyphForIcon("screenshot") },
                            new SystemActionItem { ActionId = "home_screen", Label = "Home Screen", Glyph = GetGlyphForIcon("home_screen") },
                            new SystemActionItem { ActionId = "close_all_apps", Label = "Close All Apps", Glyph = GetGlyphForIcon("close_all_apps") }
                        };
                        ListMacroStepSystem.ItemsSource = systemActions;
                    }


                    // Load custom button keycap settings
                    TxtMacroButtonTitle.Text = _selectedButton.Title;
                    
                    if (ListInstalledApps != null && ListMacroIconApps != null)
                    {
                        ListMacroIconApps.ItemsSource = ListInstalledApps.ItemsSource;
                    }

                    string iconVal = _selectedButton.Icon ?? "";
                    if (string.IsNullOrEmpty(iconVal) || iconVal == "default" || iconVal == "folder" || iconVal == "macro")
                    {
                        ComboMacroButtonIconType.SelectedIndex = 0; // Default Folder Icon
                    }
                    else if (iconVal.Contains("|"))
                    {
                        ComboMacroButtonIconType.SelectedIndex = 1; // Steps Grid
                    }
                    else if (iconVal.StartsWith("text:"))
                    {
                        ComboMacroButtonIconType.SelectedIndex = 5; // Text Label / Emoji
                        TxtMacroIconText.Text = iconVal.Substring(5);
                    }
                    else if (iconVal.StartsWith("data:"))
                    {
                        ComboMacroButtonIconType.SelectedIndex = 3; // Local Image File
                        TxtMacroIconFilePath.Text = "(custom image)";
                    }
                    else
                    {
                        ComboMacroButtonIconType.SelectedIndex = 2; // App Icon
                    }

                    // Reset sub-tab select visual states to Step Config
                    SelectMacroTab("StepConfig");
                }
                finally
                {
                    _isUpdatingUi = false;
                }
            }
            else if (_selectedButton.ActionType.Equals("System", StringComparison.OrdinalIgnoreCase))
            {
                ListSystemActions.Visibility = Visibility.Visible;
                LblActionParameter.Text = "Select System Command";

                // Populate system action items
                var systemActions = new List<SystemActionItem>
                {
                    new SystemActionItem { ActionId = "volume_up", Label = "Vol Up", Glyph = GetGlyphForIcon("volume_up") },
                    new SystemActionItem { ActionId = "volume_down", Label = "Vol Down", Glyph = GetGlyphForIcon("volume_down") },
                    new SystemActionItem { ActionId = "volume_mute", Label = "Mute", Glyph = GetGlyphForIcon("volume_mute") },
                    new SystemActionItem { ActionId = "media_play_pause", Label = "Play/Pause", Glyph = GetGlyphForIcon("media_play") },
                    new SystemActionItem { ActionId = "media_next", Label = "Next", Glyph = GetGlyphForIcon("media_next") },
                    new SystemActionItem { ActionId = "media_prev", Label = "Previous", Glyph = GetGlyphForIcon("media_prev") },
                    new SystemActionItem { ActionId = "media_forward_10", Label = "Skip 10s", Glyph = GetGlyphForIcon("media_forward_10") },
                    new SystemActionItem { ActionId = "media_backward_10", Label = "Back 10s", Glyph = GetGlyphForIcon("media_backward_10") },
                    new SystemActionItem { ActionId = "brightness_up", Label = "Bright Up", Glyph = GetGlyphForIcon("brightness_up") },
                    new SystemActionItem { ActionId = "brightness_down", Label = "Bright Down", Glyph = GetGlyphForIcon("brightness_down") },
                    new SystemActionItem { ActionId = "mic_toggle", Label = "Mic", Glyph = GetGlyphForIcon("mic") },
                    new SystemActionItem { ActionId = "pc_shutdown", Label = "Power Off", Glyph = GetGlyphForIcon("pc_shutdown") },
                    new SystemActionItem { ActionId = "pc_sleep", Label = "Hibernate PC", Glyph = GetGlyphForIcon("pc_sleep") },
                    new SystemActionItem { ActionId = "pc_lock", Label = "Lock PC", Glyph = GetGlyphForIcon("pc_lock") },
                    new SystemActionItem { ActionId = "pc_restart", Label = "Restart PC", Glyph = GetGlyphForIcon("pc_restart") },
                    new SystemActionItem { ActionId = "wifi_toggle", Label = "Toggle Wi-Fi", Glyph = GetGlyphForIcon("wifi") },
                    new SystemActionItem { ActionId = "bluetooth_toggle", Label = "Toggle Bluetooth", Glyph = GetGlyphForIcon("bluetooth") },
                    new SystemActionItem { ActionId = "screen_record", Label = "Screen Record", Glyph = GetGlyphForIcon("screen_record") },
                    new SystemActionItem { ActionId = "screenshot", Label = "Screenshot", Glyph = GetGlyphForIcon("screenshot") },
                    new SystemActionItem { ActionId = "home_screen", Label = "Home Screen", Glyph = GetGlyphForIcon("home_screen") },
                    new SystemActionItem { ActionId = "close_all_apps", Label = "Close All Apps", Glyph = GetGlyphForIcon("close_all_apps") },
                    new SystemActionItem { ActionId = "perf_cpu", Label = "CPU Usage", Glyph = GetGlyphForIcon("perf_cpu") },
                    new SystemActionItem { ActionId = "perf_gpu", Label = "GPU Usage", Glyph = GetGlyphForIcon("perf_gpu") },
                    new SystemActionItem { ActionId = "perf_ram", Label = "RAM Usage", Glyph = GetGlyphForIcon("perf_ram") },
                    new SystemActionItem { ActionId = "perf_temp", Label = "PC Temp", Glyph = GetGlyphForIcon("perf_temp") },
                    new SystemActionItem { ActionId = "perf_wifi", Label = "WiFi Speed", Glyph = GetGlyphForIcon("perf_wifi") },
                };
                ListSystemActions.ItemsSource = systemActions;

                // Select matching item
                _isUpdatingUi = true;
                try
                {
                    ListSystemActions.SelectedIndex = -1;
                    int idx = systemActions.FindIndex(a => a.ActionId == _selectedButton.ActionData);
                    if (idx >= 0)
                    {
                        ListSystemActions.SelectedIndex = idx;
                    }
                }
                finally
                {
                    _isUpdatingUi = false;
                }
            }
            else if (_selectedButton.ActionType.Equals("App", StringComparison.OrdinalIgnoreCase))
            {
                if (ListInstalledApps != null)
                {
                    ListInstalledApps.Visibility = Visibility.Visible;
                    LblActionParameter.Text = "Select Application from List";

                    // Select matching app in list
                    _isUpdatingUi = true;
                    try
                    {
                        ListInstalledApps.SelectedIndex = -1;
                        if (ListInstalledApps.ItemsSource is List<InstalledApp> apps)
                        {
                            int idx = apps.FindIndex(a => a.ShortcutPath.Equals(_selectedButton.ActionData, StringComparison.OrdinalIgnoreCase));
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
            }
            else
            {
                if (_selectedButton.ActionType.Equals("URL", StringComparison.OrdinalIgnoreCase))
                {
                    LblActionParameter.Text = "Website Links (Max 4)";
                    if (ScrollUrlLinks != null)
                    {
                        ScrollUrlLinks.Visibility = Visibility.Visible;
                        RefreshUrlLinksLayout();
                    }
                }
                else
                {
                    TxtActionData.Visibility = Visibility.Visible;
                    TxtActionData.Text = _selectedButton.ActionData;
                    LblActionParameter.Text = "Action Detail";
                }
            }
        }

        // Saving edits triggers Sync
        private void TriggerConfigSync()
        {
            if (_isUpdatingUi) return;

            if (_selectedButton != null && _selectedButton.ActionType.Equals("Macro", StringComparison.OrdinalIgnoreCase))
            {
                if (ComboMacroButtonIconType != null && ComboMacroButtonIconType.SelectedIndex == 1)
                {
                    _ = UpdateMacroButtonIconGridAsync(false);
                }
            }

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
                case "app": return "#3B82F6"; // Blue
                case "url": return "#10B981"; // Green
                case "macro": return "#EC4899"; // Pink
                case "system": return "#F59E0B"; // Amber
                default: return "#6366F1"; // Indigo default
            }
        }

        private string GetDefaultIconForType(string type, string actionData = "")
        {
            switch (type?.ToLower())
            {
                case "app": return string.IsNullOrEmpty(actionData) ? "app_default" : "rocket";
                case "url": return "url";
                case "macro": return "folder";
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

        [DllImport("shell32.dll")]
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

        private static System.Windows.Media.ImageSource? Base64PngToImageSource(string base64)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                using (var ms = new MemoryStream(bytes))
                {
                    var image = new System.Windows.Media.Imaging.BitmapImage();
                    image.BeginInit();
                    image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    image.StreamSource = ms;
                    image.EndInit();
                    image.Freeze();
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

        private void LoadInstalledAppsAsync()
        {
            Task.Run(() =>
            {
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
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error scanning AppsFolder: {ex.Message}");
                }

                Dispatcher.Invoke(() =>
                {
                    ListInstalledApps.ItemsSource = apps;
                    if (ListMacroStepApps != null)
                    {
                        ListMacroStepApps.ItemsSource = apps;
                    }
                    if (ListMacroIconApps != null)
                    {
                        ListMacroIconApps.ItemsSource = apps;
                    }

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
            if (_selectedButton == null || _isUpdatingUi) return;

            var clickedBtn = sender as Button;
            if (clickedBtn == null || clickedBtn.Tag == null) return;

            string actionType = clickedBtn.Tag.ToString()!;
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
                }

                UpdateCategoryTabsHighlight(actionType);
                LoadActionDetails();
                RefreshGridPreview();
            }
            TriggerConfigSync();
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

        private async Task UpdateUrlAndFetchIconsAsync(int index, string newUrlValue)
        {
            if (_selectedButton == null) return;
            var urls = new List<string>();
            if (!string.IsNullOrWhiteSpace(_selectedButton.ActionData))
            {
                urls.AddRange(_selectedButton.ActionData.Split('|'));
            }
            while (urls.Count <= index)
            {
                urls.Add("");
            }
            urls[index] = newUrlValue.Trim();

            _selectedButton.ActionData = string.Join("|", urls);

            // Fetch favicons for all non-empty URLs
            var icons = new List<string>();
            for (int i = 0; i < urls.Count; i++)
            {
                var u = urls[i];
                if (string.IsNullOrWhiteSpace(u))
                {
                    icons.Add("url");
                }
                else
                {
                    var b64 = await FetchFaviconAsBase64Async(u);
                    if (!string.IsNullOrEmpty(b64))
                    {
                        icons.Add("data:" + b64);
                    }
                    else
                    {
                        icons.Add("url");
                    }
                }
            }
            _selectedButton.Icon = string.Join("|", icons);

            // Update title based on URLs
            if (urls.Count == 1)
            {
                string u = urls[0];
                if (!string.IsNullOrEmpty(u))
                {
                    _selectedButton.Title = u.Replace("http://", "").Replace("https://", "").Replace("www.", "").TrimEnd('/');
                }
                else
                {
                    _selectedButton.Title = "Open URL";
                }
            }
            else
            {
                int validCount = urls.Count(u => !string.IsNullOrWhiteSpace(u));
                _selectedButton.Title = validCount > 1 ? $"{validCount} Links" : "Open URL";
            }

            _selectedButton.Color = GetDefaultColorForType("url");

            ConfigManager.Save();
            RefreshGridPreview();
            TriggerConfigSync();
        }

        private async Task RemoveUrlLinkAsync(int index)
        {
            if (_selectedButton == null) return;
            var urls = new List<string>();
            if (!string.IsNullOrWhiteSpace(_selectedButton.ActionData))
            {
                urls.AddRange(_selectedButton.ActionData.Split('|'));
            }

            if (index < urls.Count)
            {
                urls.RemoveAt(index);
            }

            if (urls.Count == 0)
            {
                urls.Add("");
            }

            _selectedButton.ActionData = string.Join("|", urls);

            // Re-fetch favicons
            var icons = new List<string>();
            for (int i = 0; i < urls.Count; i++)
            {
                var u = urls[i];
                if (string.IsNullOrWhiteSpace(u))
                {
                    icons.Add("url");
                }
                else
                {
                    var b64 = await FetchFaviconAsBase64Async(u);
                    if (!string.IsNullOrEmpty(b64))
                    {
                        icons.Add("data:" + b64);
                    }
                    else
                    {
                        icons.Add("url");
                    }
                }
            }
            _selectedButton.Icon = string.Join("|", icons);

            if (urls.Count == 1)
            {
                string u = urls[0];
                if (!string.IsNullOrEmpty(u))
                {
                    _selectedButton.Title = u.Replace("http://", "").Replace("https://", "").Replace("www.", "").TrimEnd('/');
                }
                else
                {
                    _selectedButton.Title = "Open URL";
                }
            }
            else
            {
                int validCount = urls.Count(u => !string.IsNullOrWhiteSpace(u));
                _selectedButton.Title = validCount > 1 ? $"{validCount} Links" : "Open URL";
            }

            ConfigManager.Save();
            RefreshGridPreview();
            RefreshUrlLinksLayout();
            TriggerConfigSync();
        }

        private void AddUrlLinkField()
        {
            if (_selectedButton == null) return;
            var urls = new List<string>();
            if (!string.IsNullOrWhiteSpace(_selectedButton.ActionData))
            {
                urls.AddRange(_selectedButton.ActionData.Split('|'));
            }

            if (urls.Count >= 4) return;

            urls.Add("");
            _selectedButton.ActionData = string.Join("|", urls);

            // Append "url" to the icons list
            var icons = new List<string>();
            if (!string.IsNullOrWhiteSpace(_selectedButton.Icon))
            {
                icons.AddRange(_selectedButton.Icon.Split('|'));
            }
            icons.Add("url");
            _selectedButton.Icon = string.Join("|", icons);

            ConfigManager.Save();
            RefreshGridPreview();
            RefreshUrlLinksLayout();
            TriggerConfigSync();
        }

        private void RefreshUrlLinksLayout()
        {
            if (StackUrlLinksContainer == null) return;
            StackUrlLinksContainer.Children.Clear();

            var urls = new List<string>();
            if (_selectedButton != null && !string.IsNullOrWhiteSpace(_selectedButton.ActionData))
            {
                urls.AddRange(_selectedButton.ActionData.Split('|'));
            }

            if (urls.Count == 0)
            {
                urls.Add("");
            }

            // Local helper to generate standard card-styled buttons matching the app design system
            ControlTemplate GetBtnTemplate(Color bg, Color hoverBg, Color borderCol, double radius = 6)
            {
                var template = new ControlTemplate(typeof(Button));
                var btnBorder = new FrameworkElementFactory(typeof(Border));
                btnBorder.Name = "Border";
                btnBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
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
                hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), "Border")); // Red hover outline for delete
                template.Triggers.Add(hoverTrigger);
                return template;
            }

            ControlTemplate GetAddBtnTemplate(Color bg, Color hoverBg, Color borderCol, double radius = 12)
            {
                var template = new ControlTemplate(typeof(Button));
                var btnBorder = new FrameworkElementFactory(typeof(Border));
                btnBorder.Name = "Border";
                btnBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
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
                hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), "Border")); // Indigo/Blue hover outline
                template.Triggers.Add(hoverTrigger);
                return template;
            }

            for (int i = 0; i < urls.Count; i++)
            {
                int index = i;
                var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var txtBox = new System.Windows.Controls.TextBox
                {
                    Style = FindResource(typeof(System.Windows.Controls.TextBox)) as Style,
                    Text = urls[index],
                    Margin = new Thickness(0, 0, 8, 0),
                    Height = 40,
                    Padding = new Thickness(12, 0, 12, 0),
                    VerticalContentAlignment = VerticalAlignment.Center
                };

                txtBox.LostFocus += async (s, e) =>
                {
                    await UpdateUrlAndFetchIconsAsync(index, txtBox.Text);
                };
                txtBox.KeyDown += async (s, e) =>
                {
                    if (e.Key == System.Windows.Input.Key.Enter)
                    {
                        txtBox.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
                    }
                };

                Grid.SetColumn(txtBox, 0);
                grid.Children.Add(txtBox);

                var deleteBtn = new Button
                {
                    Content = "Remove",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Width = 65,
                    Height = 40,
                    Template = GetBtnTemplate(Color.FromRgb(0x11, 0x11, 0x16), Color.FromRgb(0x25, 0x11, 0x16), Color.FromRgb(0x1C, 0x1C, 0x24), 8)
                };
                deleteBtn.Click += async (s, e) =>
                {
                    await RemoveUrlLinkAsync(index);
                };
                Grid.SetColumn(deleteBtn, 1);
                grid.Children.Add(deleteBtn);

                StackUrlLinksContainer.Children.Add(grid);
            }

            if (urls.Count < 4)
            {
                var addUrlBtn = new Button
                {
                    Content = "+ Add URL Link",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)), // Emerald/Green accent
                    Margin = new Thickness(0, 8, 0, 16),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Width = 180,
                    Height = 32,
                    Template = GetAddBtnTemplate(Color.FromRgb(0x11, 0x11, 0x16), Color.FromRgb(0x1E, 0x1E, 0x28), Color.FromRgb(0x1C, 0x1C, 0x24), 10)
                };
                addUrlBtn.Click += (s, e) =>
                {
                    AddUrlLinkField();
                };
                StackUrlLinksContainer.Children.Add(addUrlBtn);
            }
        }

        private void ListSystemActions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selectedButton == null || ListSystemActions.SelectedItem == null || _isUpdatingUi) return;
            var selectedItem = ListSystemActions.SelectedItem as SystemActionItem;
            if (selectedItem != null)
            {
                _selectedButton.ActionData = selectedItem.ActionId;
                _selectedButton.Title = GetSystemActionTitle(selectedItem.ActionId);
                _selectedButton.Icon = GetDefaultIconForType("system", selectedItem.ActionId);
                _selectedButton.Color = GetDefaultColorForType("system");
                TriggerConfigSync();
            }
        }

        // Button management
        private void BtnAddButton_Click(object sender, RoutedEventArgs e)
        {
            if (ConfigManager.CurrentButtons.Count >= 63)
            {
                MessageBox.Show("Maximum limit of 63 deck shortcuts reached (8 pages).", "Limit Reached", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var newBtn = new ShortcutButton
            {
                Title = "Select App",
                Color = "#3B82F6", // Blue for App
                ActionType = "App",
                ActionData = "",
                Icon = "app_default"
            };

            ConfigManager.CurrentButtons.Add(newBtn);
            SelectShortcutButton(newBtn);
            TriggerConfigSync();
        }


        private void ListProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi) return;

            var selectedProfile = ListProfiles.SelectedItem as Profile;
            if (selectedProfile != null)
            {
                ConfigManager.Current.CurrentProfileId = selectedProfile.Id;
                ConfigManager.Save();

                // Selection change logic is complete without local profile name text fields

                SelectShortcutButton(null);
                _selectedBulkButtons.Clear();
                _server.SyncButtons();
                _server.SyncProfiles();
            }
        }

        private void OnProfileChangeRequested(string profileId)
        {
            Dispatcher.Invoke(() =>
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
                        RefreshGridPreview();
                        RefreshProfilesList();
                    }
                    finally
                    {
                        _isUpdatingUi = false;
                    }

                    SelectShortcutButton(null);
                    _selectedBulkButtons.Clear();
                    _server.SyncButtons();
                    _server.SyncProfiles();
                }
            });
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
                ListProfiles.SelectedItem = profile;
                RenameProfileWithDialog(profile);
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



        // Macro Sequence Actions
        private void BtnAddMacroStep_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedButton == null) return;
            
            // Add a clean delay step by default, which is the safest
            var step = new MacroStep
            {
                Type = "Delay",
                Data = "",
                DelayMs = 500
            };

            _selectedButton.MacroSteps.Add(step);
            TriggerConfigSync();
            
            _isUpdatingUi = true;
            try
            {
                ListMacroSteps.ItemsSource = null;
                ListMacroSteps.ItemsSource = _selectedButton.MacroSteps;
                ListMacroSteps.SelectedItem = step;
                ListMacroSteps.ScrollIntoView(step);
            }
            finally
            {
                _isUpdatingUi = false;
            }
            
            // Trigger selection changed programmatically since we set SelectedItem
            ListMacroSteps_SelectionChanged(ListMacroSteps, null!);
        }

        private void BtnRemoveMacroStep_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedButton == null || ListMacroSteps.SelectedItem == null) return;
            
            var step = ListMacroSteps.SelectedItem as MacroStep;
            if (step != null)
            {
                int index = _selectedButton.MacroSteps.IndexOf(step);
                _selectedButton.MacroSteps.Remove(step);
                TriggerConfigSync();
                
                _isUpdatingUi = true;
                try
                {
                    ListMacroSteps.ItemsSource = null;
                    ListMacroSteps.ItemsSource = _selectedButton.MacroSteps;
                    
                    // Select another step if possible
                    if (_selectedButton.MacroSteps.Count > 0)
                    {
                        int newIndex = Math.Clamp(index, 0, _selectedButton.MacroSteps.Count - 1);
                        ListMacroSteps.SelectedIndex = newIndex;
                    }
                    else
                    {
                        ListMacroSteps.SelectedIndex = -1;
                    }
                }
                finally
                {
                    _isUpdatingUi = false;
                }
                
                ListMacroSteps_SelectionChanged(ListMacroSteps, null!);
            }
        }

        private void ListMacroSteps_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi || _selectedButton == null) return;
            
            var step = ListMacroSteps.SelectedItem as MacroStep;
            if (step == null)
            {
                PanelMacroStepEditor.Visibility = Visibility.Collapsed;
                PanelMacroNoStepSelected.Visibility = Visibility.Visible;
                return;
            }
            
            _isUpdatingUi = true;
            try
            {
                PanelMacroNoStepSelected.Visibility = Visibility.Collapsed;
                PanelMacroStepEditor.Visibility = Visibility.Visible;
                
                int stepIndex = _selectedButton.MacroSteps.IndexOf(step) + 1;
                LblMacroStepEditorTitle.Text = $"Configure Step #{stepIndex}: {step.DisplayType}";
                
                // Select type in ComboBox
                foreach (ComboBoxItem item in ComboMacroStepType.Items)
                {
                    if (item.Tag?.ToString() == step.Type)
                    {
                        ComboMacroStepType.SelectedItem = item;
                        break;
                    }
                }
                
                // Show only the relevant editor subpanel and populate its fields
                UpdateMacroStepEditorSubpanels(step);
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void UpdateMacroStepEditorSubpanels(MacroStep step)
        {
            // Collapse all subpanels first
            PanelMacroStepApp.Visibility = Visibility.Collapsed;
            PanelMacroStepUrl.Visibility = Visibility.Collapsed;
            PanelMacroStepSystem.Visibility = Visibility.Collapsed;
            PanelMacroStepDelay.Visibility = Visibility.Collapsed;
            PanelMacroStepPostDelay.Visibility = Visibility.Collapsed;
            
            // Show subpanel depending on type
            switch (step.Type)
            {
                case "App":
                    PanelMacroStepApp.Visibility = Visibility.Visible;
                    PanelMacroStepPostDelay.Visibility = Visibility.Visible;
                    TxtMacroStepAppPath.Text = step.Data;
                    
                    // Select matching app in the grid
                    ListMacroStepApps.SelectedIndex = -1;
                    if (ListMacroStepApps.ItemsSource is List<InstalledApp> apps)
                    {
                        int idx = apps.FindIndex(a => a.ShortcutPath.Equals(step.Data, StringComparison.OrdinalIgnoreCase));
                        if (idx >= 0) ListMacroStepApps.SelectedIndex = idx;
                    }
                    break;
                    
                case "URL":
                    PanelMacroStepUrl.Visibility = Visibility.Visible;
                    PanelMacroStepPostDelay.Visibility = Visibility.Visible;
                    TxtMacroStepUrl.Text = step.Data;
                    break;
                    
                case "System":
                    PanelMacroStepSystem.Visibility = Visibility.Visible;
                    PanelMacroStepPostDelay.Visibility = Visibility.Visible;
                    
                    // Select matching system command
                    ListMacroStepSystem.SelectedIndex = -1;
                    if (ListMacroStepSystem.ItemsSource is List<SystemActionItem> sysActions)
                    {
                        int idx = sysActions.FindIndex(a => a.ActionId.Equals(step.Data, StringComparison.OrdinalIgnoreCase));
                        if (idx >= 0) ListMacroStepSystem.SelectedIndex = idx;
                    }
                    break;
                    
                case "Delay":
                    PanelMacroStepDelay.Visibility = Visibility.Visible;
                    SliderMacroStepDelay.Value = Math.Clamp(step.DelayMs, 50, 10000);
                    TxtMacroStepDelay.Text = step.DelayMs.ToString();
                    break;
            }
            
            // Set Post-Delay value
            if (step.Type != "Delay")
            {
                SliderMacroStepPostDelay.Value = Math.Clamp(step.DelayMs, 0, 10000);
                TxtMacroStepPostDelay.Text = step.DelayMs.ToString();
            }
        }

        private void ComboMacroStepType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi || _selectedButton == null) return;
            
            var step = ListMacroSteps.SelectedItem as MacroStep;
            if (step == null) return;
            
            var selectedItem = ComboMacroStepType.SelectedItem as ComboBoxItem;
            if (selectedItem == null || selectedItem.Tag == null) return;
            
            string newType = selectedItem.Tag.ToString() ?? "Delay";
            if (step.Type == newType) return;
            
            step.Type = newType;
            // Set some smart default action data depending on type
            step.Data = newType switch
            {
                "App" => "notepad.exe",
                "Command" => "echo Hello World",
                "URL" => "google.com",
                "System" => "volume_up",
                "Delay" => "",
                _ => ""
            };
            
            if (newType == "Delay" && step.DelayMs == 0)
            {
                step.DelayMs = 500; // Delay step should default to a positive duration
            }
            else if (newType != "Delay" && step.DelayMs == 500 && string.IsNullOrEmpty(step.Data))
            {
                step.DelayMs = 100; // Reset post-action delay to a sensible default
            }
            
            TriggerConfigSync();
            RefreshMacroStepsList();
            
            // Refresh subpanels
            _isUpdatingUi = true;
            try
            {
                int stepIndex = _selectedButton.MacroSteps.IndexOf(step) + 1;
                LblMacroStepEditorTitle.Text = $"Configure Step #{stepIndex}: {step.DisplayType}";
                UpdateMacroStepEditorSubpanels(step);
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void RefreshMacroStepsList()
        {
            if (_selectedButton == null) return;
            var selectedIndex = ListMacroSteps.SelectedIndex;
            
            _isUpdatingUi = true;
            try
            {
                ListMacroSteps.ItemsSource = null;
                ListMacroSteps.ItemsSource = _selectedButton.MacroSteps;
                ListMacroSteps.SelectedIndex = selectedIndex;
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void TxtMacroStepAppPath_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUi || _selectedButton == null) return;
            var step = ListMacroSteps.SelectedItem as MacroStep;
            if (step == null || step.Type != "App") return;
            
            step.Data = TxtMacroStepAppPath.Text;
            TriggerConfigSync();
            RefreshMacroStepsList();
        }
        
        private void BtnMacroStepBrowseApp_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedButton == null) return;
            var step = ListMacroSteps.SelectedItem as MacroStep;
            if (step == null || step.Type != "App") return;
            
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Executable files (*.exe)|*.exe|Command files (*.bat;*.cmd)|*.bat;*.cmd|All files (*.*)|*.*",
                Title = "Select Application to Launch"
            };
            
            if (openFileDialog.ShowDialog() == true)
            {
                TxtMacroStepAppPath.Text = openFileDialog.FileName;
            }
        }
        
        private void ListMacroStepApps_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi || _selectedButton == null) return;
            var step = ListMacroSteps.SelectedItem as MacroStep;
            if (step == null || step.Type != "App") return;
            
            var selectedApp = ListMacroStepApps.SelectedItem as InstalledApp;
            if (selectedApp != null)
            {
                TxtMacroStepAppPath.Text = selectedApp.ShortcutPath;
            }
        }

        private void TxtMacroStepUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUi || _selectedButton == null) return;
            var step = ListMacroSteps.SelectedItem as MacroStep;
            if (step == null || step.Type != "URL") return;
            
            step.Data = TxtMacroStepUrl.Text;
            TriggerConfigSync();
            RefreshMacroStepsList();
        }

        private void ListMacroStepSystem_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi || _selectedButton == null) return;
            var step = ListMacroSteps.SelectedItem as MacroStep;
            if (step == null || step.Type != "System") return;
            
            var systemAction = ListMacroStepSystem.SelectedItem as SystemActionItem;
            if (systemAction != null)
            {
                step.Data = systemAction.ActionId;
                TriggerConfigSync();
                RefreshMacroStepsList();
            }
        }

        private void SliderMacroStepDelay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUi || _selectedButton == null) return;
            var step = ListMacroSteps.SelectedItem as MacroStep;
            if (step == null || step.Type != "Delay") return;
            
            int newDelay = (int)SliderMacroStepDelay.Value;
            step.DelayMs = newDelay;
            TxtMacroStepDelay.Text = newDelay.ToString();
            
            TriggerConfigSync();
            RefreshMacroStepsList();
        }
        
        private void TxtMacroStepDelay_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUi || _selectedButton == null) return;
            var step = ListMacroSteps.SelectedItem as MacroStep;
            if (step == null || step.Type != "Delay") return;
            
            if (int.TryParse(TxtMacroStepDelay.Text, out int newDelay))
            {
                newDelay = Math.Clamp(newDelay, 50, 10000);
                step.DelayMs = newDelay;
                SliderMacroStepDelay.Value = newDelay;
                
                TriggerConfigSync();
                RefreshMacroStepsList();
            }
        }

        private void SliderMacroStepPostDelay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUi || _selectedButton == null) return;
            var step = ListMacroSteps.SelectedItem as MacroStep;
            if (step == null || step.Type == "Delay") return; // post delay not for Delay type steps
            
            int newDelay = (int)SliderMacroStepPostDelay.Value;
            step.DelayMs = newDelay;
            TxtMacroStepPostDelay.Text = newDelay.ToString();
            
            TriggerConfigSync();
            RefreshMacroStepsList();
        }
        
        private void TxtMacroStepPostDelay_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUi || _selectedButton == null) return;
            var step = ListMacroSteps.SelectedItem as MacroStep;
            if (step == null || step.Type == "Delay") return;
            
            if (int.TryParse(TxtMacroStepPostDelay.Text, out int newDelay))
            {
                newDelay = Math.Clamp(newDelay, 0, 10000);
                step.DelayMs = newDelay;
                SliderMacroStepPostDelay.Value = newDelay;
                
                TriggerConfigSync();
                RefreshMacroStepsList();
            }
        }

        private void BtnMoveMacroStepUp_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedButton == null || ListMacroSteps.SelectedItem == null) return;
            var step = ListMacroSteps.SelectedItem as MacroStep;
            if (step == null) return;
            
            int index = _selectedButton.MacroSteps.IndexOf(step);
            if (index <= 0) return; // Already at the top
            
            _selectedButton.MacroSteps.RemoveAt(index);
            _selectedButton.MacroSteps.Insert(index - 1, step);
            
            TriggerConfigSync();
            
            _isUpdatingUi = true;
            try
            {
                ListMacroSteps.ItemsSource = null;
                ListMacroSteps.ItemsSource = _selectedButton.MacroSteps;
                ListMacroSteps.SelectedItem = step;
                ListMacroSteps.ScrollIntoView(step);
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }
        
        private void BtnMoveMacroStepDown_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedButton == null || ListMacroSteps.SelectedItem == null) return;
            var step = ListMacroSteps.SelectedItem as MacroStep;
            if (step == null) return;
            
            int index = _selectedButton.MacroSteps.IndexOf(step);
            if (index < 0 || index >= _selectedButton.MacroSteps.Count - 1) return; // Already at the bottom
            
            _selectedButton.MacroSteps.RemoveAt(index);
            _selectedButton.MacroSteps.Insert(index + 1, step);
            
            TriggerConfigSync();
            
            _isUpdatingUi = true;
            try
            {
                ListMacroSteps.ItemsSource = null;
                ListMacroSteps.ItemsSource = _selectedButton.MacroSteps;
                ListMacroSteps.SelectedItem = step;
                ListMacroSteps.ScrollIntoView(step);
            }
            finally
            {
                _isUpdatingUi = false;
            }
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
        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            if (GridSidebarProfiles != null) GridSidebarProfiles.Visibility = Visibility.Collapsed;
            if (GridSidebarSettings != null) GridSidebarSettings.Visibility = Visibility.Visible;
            TxtSettingsDeviceName.Text = ConfigManager.Current.DeviceName;
            RefreshConnectionHistory();
        }

        private void BtnBackToDashboard_Click(object sender, RoutedEventArgs e)
        {
            HideSidebarSettings();
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

        private void RefreshConnectionHistory()
        {
            // Remove duplicates from in-memory history if any exist (e.g. from older config runs)
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var uniqueHistory = new List<DeviceConnection>();
            foreach (var item in ConfigManager.Current.ConnectionHistory)
            {
                if (seen.Add(item.DeviceName))
                {
                    uniqueHistory.Add(item);
                }
            }
            if (uniqueHistory.Count != ConfigManager.Current.ConnectionHistory.Count)
            {
                ConfigManager.Current.ConnectionHistory = uniqueHistory;
                ConfigManager.Save();
            }

            ListConnectionHistory.ItemsSource = null;
            ListConnectionHistory.ItemsSource = ConfigManager.Current.ConnectionHistory;
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            ConfigManager.Current.ConnectionHistory.Clear();
            ConfigManager.Save();
            RefreshConnectionHistory();
        }

        private void BtnResetApp_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Are you sure you want to reset all application data? This will clear all shortcuts, connection history, and pairing tokens.", 
                "Reset Application Data", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            
            if (result == MessageBoxResult.Yes)
            {
                _server.Stop();
                ConfigManager.ResetConfig();

                _selectedButton = null;
                ListConnectionHistory.ItemsSource = null;
                HideSidebarSettings();
                GridDashboardMainContent.Visibility = Visibility.Visible;

                ShowDisconnectedPanel();
            }
        }



        private void InitializePerformanceMonitoring()
        {
            _perfTimer = new DispatcherTimer();
            _perfTimer.Interval = TimeSpan.FromSeconds(1);
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

                        if (!_isCellContextMenuOpen)
                        {
                            RefreshGridPreview();
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



        // --- Custom Macro Button Customizer Event Handlers & Helpers ---

        private void BtnMacroTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tabName)
            {
                SelectMacroTab(tabName);
            }
        }

        private void TxtMacroButtonTitle_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_selectedButton == null || _isUpdatingUi) return;
            _selectedButton.Title = TxtMacroButtonTitle.Text;
            TriggerConfigSync();
        }

        private void BtnMacroColorBadge_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedButton == null || sender is not Button btn || btn.Tag is not string colorHex) return;
            _selectedButton.Color = colorHex;
            TriggerConfigSync();
        }

        private void ComboMacroButtonIconType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selectedButton == null || _isUpdatingUi) return;

            // Hide all contextual panels first
            PanelMacroIconApp.Visibility = Visibility.Collapsed;
            PanelMacroIconFile.Visibility = Visibility.Collapsed;
            PanelMacroIconUrl.Visibility = Visibility.Collapsed;
            PanelMacroIconText.Visibility = Visibility.Collapsed;

            var selectedItem = ComboMacroButtonIconType.SelectedItem as ComboBoxItem;
            if (selectedItem == null || selectedItem.Tag == null) return;

            string tag = selectedItem.Tag.ToString()!;
            switch (tag)
            {
                case "Default":
                    _selectedButton.Icon = "folder";
                    TriggerConfigSync();
                    break;

                case "Grid":
                    _ = UpdateMacroButtonIconGridAsync(true);
                    break;

                case "App":
                    PanelMacroIconApp.Visibility = Visibility.Visible;
                    ListMacroIconApps.SelectedIndex = -1;
                    break;

                case "File":
                    PanelMacroIconFile.Visibility = Visibility.Visible;
                    break;

                case "Url":
                    PanelMacroIconUrl.Visibility = Visibility.Visible;
                    break;

                case "Text":
                    PanelMacroIconText.Visibility = Visibility.Visible;
                    _isUpdatingUi = true;
                    try
                    {
                        string iconVal = _selectedButton.Icon ?? "";
                        TxtMacroIconText.Text = iconVal.StartsWith("text:") ? iconVal.Substring(5) : "";
                    }
                    finally
                    {
                        _isUpdatingUi = false;
                    }
                    break;
            }
        }

        private void ListMacroIconApps_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selectedButton == null || ListMacroIconApps.SelectedItem == null || _isUpdatingUi) return;

            var selectedApp = ListMacroIconApps.SelectedItem as InstalledApp;
            if (selectedApp != null)
            {
                string? iconBase64 = ImageSourceToBase64Png(selectedApp.Icon);
                if (!string.IsNullOrEmpty(iconBase64))
                {
                    _selectedButton.Icon = "data:" + iconBase64;
                }
                else
                {
                    _selectedButton.Icon = "rocket";
                }
                TriggerConfigSync();
            }
        }

        private void BtnMacroBrowseIconFile_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedButton == null) return;

            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Image Files (*.png;*.jpg;*.jpeg;*.bmp;*.ico)|*.png;*.jpg;*.jpeg;*.bmp;*.ico|All Files (*.*)|*.*",
                Title = "Select Keycap Image"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    string filePath = openFileDialog.FileName;
                    TxtMacroIconFilePath.Text = filePath;

                    byte[] bytes = File.ReadAllBytes(filePath);
                    string base64 = Convert.ToBase64String(bytes);
                    _selectedButton.Icon = "data:" + base64;
                    TriggerConfigSync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to load image file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnMacroDownloadIconUrl_Click(object sender, RoutedEventArgs e)
        {
            string url = TxtMacroIconUrl.Text;
            if (string.IsNullOrWhiteSpace(url)) return;
            await DownloadImageAsBase64Async(url);
        }

        private async Task DownloadImageAsBase64Async(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            string cleanUrl = url.Trim();
            if (!cleanUrl.StartsWith("http://") && !cleanUrl.StartsWith("https://"))
            {
                cleanUrl = "https://" + cleanUrl;
            }

            Dispatcher.Invoke(() => {
                LblMacroIconUrlStatus.Text = "Downloading image...";
                LblMacroIconUrlStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
            });

            try
            {
                byte[] bytes = await _httpClient.GetByteArrayAsync(cleanUrl);
                if (bytes != null && bytes.Length > 0)
                {
                    using (var ms = new MemoryStream(bytes))
                    {
                        var image = new System.Windows.Media.Imaging.BitmapImage();
                        image.BeginInit();
                        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        image.StreamSource = ms;
                        image.EndInit();
                    }

                    string base64 = Convert.ToBase64String(bytes);
                    Dispatcher.Invoke(() =>
                    {
                        if (_selectedButton != null)
                        {
                            _selectedButton.Icon = "data:" + base64;
                            TriggerConfigSync();
                        }
                        LblMacroIconUrlStatus.Text = "Success!";
                        LblMacroIconUrlStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                    });
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        LblMacroIconUrlStatus.Text = "Downloaded empty data.";
                        LblMacroIconUrlStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                    });
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    LblMacroIconUrlStatus.Text = $"Error: {ex.Message}";
                    LblMacroIconUrlStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                });
            }
        }

        private void TxtMacroIconText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_selectedButton == null || _isUpdatingUi) return;
            _selectedButton.Icon = "text:" + TxtMacroIconText.Text;
            TriggerConfigSync();
        }

        private async Task UpdateMacroButtonIconGridAsync(bool triggerSyncAfter = true)
        {
            if (_selectedButton == null) return;

            var stepIcons = new List<string>();
            var steps = new List<MacroStep>(_selectedButton.MacroSteps);

            for (int i = 0; i < Math.Min(steps.Count, 4); i++)
            {
                var step = steps[i];
                string stepIcon = GetDefaultIconForType(step.Type, step.Data);

                if (step.Type.Equals("App", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(step.Data))
                {
                    string? base64 = null;
                    if (ListInstalledApps?.ItemsSource is List<InstalledApp> apps)
                    {
                        var app = apps.Find(a => a.ShortcutPath.Equals(step.Data, StringComparison.OrdinalIgnoreCase) ||
                                                 a.DisplayName.Equals(step.Data, StringComparison.OrdinalIgnoreCase));
                        if (app != null)
                        {
                            base64 = ImageSourceToBase64Png(app.Icon);
                        }
                    }

                    if (string.IsNullOrEmpty(base64))
                    {
                        try
                        {
                            var imgSource = GetShellIcon(step.Data);
                            base64 = ImageSourceToBase64Png(imgSource);
                        }
                        catch {}
                    }

                    if (!string.IsNullOrEmpty(base64))
                    {
                        stepIcon = "data:" + base64;
                    }
                }
                else if (step.Type.Equals("URL", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(step.Data))
                {
                    string? base64 = await FetchFaviconAsBase64Async(step.Data);
                    if (!string.IsNullOrEmpty(base64))
                    {
                        stepIcon = "data:" + base64;
                    }
                }

                stepIcons.Add(stepIcon);
            }

            string newIcon;
            if (stepIcons.Count == 0)
            {
                newIcon = "folder";
            }
            else
            {
                newIcon = string.Join("|", stepIcons);
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

        private void SelectMacroTab(string tab)
        {
            if (BtnMacroTabStepConfig == null || BtnMacroTabButtonConfig == null) return;
            if (PanelMacroStepsContainer == null || PanelMacroKeycapCustomizer == null) return;

            if (tab == "StepConfig")
            {
                BtnMacroTabStepConfig.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x24));
                BtnMacroTabStepConfig.BorderBrush = new SolidColorBrush(Colors.White);
                BtnMacroTabStepConfig.Foreground = new SolidColorBrush(Colors.White);

                BtnMacroTabButtonConfig.Background = System.Windows.Media.Brushes.Transparent;
                BtnMacroTabButtonConfig.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x35));
                BtnMacroTabButtonConfig.Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));

                PanelMacroStepsContainer.Visibility = Visibility.Visible;
                PanelMacroKeycapCustomizer.Visibility = Visibility.Collapsed;
            }
            else if (tab == "ButtonConfig")
            {
                BtnMacroTabButtonConfig.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x24));
                BtnMacroTabButtonConfig.BorderBrush = new SolidColorBrush(Colors.White);
                BtnMacroTabButtonConfig.Foreground = new SolidColorBrush(Colors.White);

                BtnMacroTabStepConfig.Background = System.Windows.Media.Brushes.Transparent;
                BtnMacroTabStepConfig.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x35));
                BtnMacroTabStepConfig.Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));

                PanelMacroStepsContainer.Visibility = Visibility.Collapsed;
                PanelMacroKeycapCustomizer.Visibility = Visibility.Visible;
            }
        }

    }

    public class ProfileButtonColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var buttons = value as List<ShortcutButton>;
            if (buttons == null)
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E5E7EB"));

            int index = 0;
            if (parameter != null)
            {
                int.TryParse(parameter.ToString(), out index);
            }

            // Index 0: settings gear (slot 0)
            if (index == 0)
            {
                return GetGradientBrushFromHex("#6366F1");
            }

            int btnIndex = index - 1;
            if (btnIndex >= 0 && btnIndex < buttons.Count)
            {
                return GetGradientBrushFromHex(buttons[btnIndex].Color);
            }

            // Default empty slot placeholder
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E5E7EB"));
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }

        private System.Windows.Media.Brush GetGradientBrushFromHex(string hex)
        {
            try
            {
                var baseColor = (Color)ColorConverter.ConvertFromString(hex);
                var darkerColor = Color.FromRgb(
                    (byte)Math.Max(0, baseColor.R * 0.75),
                    (byte)Math.Max(0, baseColor.G * 0.75),
                    (byte)Math.Max(0, baseColor.B * 0.75)
                );
                return new LinearGradientBrush(baseColor, darkerColor, new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
            }
            catch
            {
                var start = (Color)ColorConverter.ConvertFromString("#6366F1");
                var end = (Color)ColorConverter.ConvertFromString("#4F46E5");
                return new LinearGradientBrush(start, end, new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
            }
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
}
