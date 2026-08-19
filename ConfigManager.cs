using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SwiftDock
{
    public static class ConfigManager
    {
        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        private static AppConfig? _currentConfig;

        public static AppConfig Current
        {
            get
            {
                if (_currentConfig == null)
                {
                    Load();
                }
                return _currentConfig!;
            }
        }

        public static List<ShortcutButton> CurrentButtons
        {
            get
            {
                var config = Current;
                if (config.Profiles == null || config.Profiles.Count == 0)
                {
                    MigrateOrInitializeProfiles();
                }

                var currentProfile = config.Profiles!.Find(p => p.Id == config.CurrentProfileId);
                if (currentProfile == null)
                {
                    if (config.Profiles.Count > 0)
                    {
                        config.CurrentProfileId = config.Profiles[0].Id;
                        return config.Profiles[0].Buttons;
                    }
                    var defaultProfile = new Profile { Name = "Default Profile" };
                    config.Profiles.Add(defaultProfile);
                    config.CurrentProfileId = defaultProfile.Id;
                    Save();
                    return defaultProfile.Buttons;
                }
                return currentProfile.Buttons;
            }
        }

        public static void MigrateOrInitializeProfiles()
        {
            var config = Current;
            if (config.Profiles == null) config.Profiles = new List<Profile>();
            
            if (config.Profiles.Count == 0)
            {
                var defaultProfile = new Profile { Name = "Default Profile" };
                if (config.Buttons != null && config.Buttons.Count > 0)
                {
                    defaultProfile.Buttons = new List<ShortcutButton>(config.Buttons);
                }
                config.Profiles.Add(defaultProfile);
                config.CurrentProfileId = defaultProfile.Id;
                Save();
            }
        }

        public static void Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    _currentConfig = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
                else
                {
                    _currentConfig = new AppConfig();
                    Save(); // Create default file
                }

                // Initialize PairedDevices and migrate legacy single pairing
                if (_currentConfig.PairedDevices == null)
                {
                    _currentConfig.PairedDevices = new List<PairedDevice>();
                }
                if (!string.IsNullOrEmpty(_currentConfig.PairedToken))
                {
                    if (!_currentConfig.PairedDevices.Exists(d => d.Token == _currentConfig.PairedToken))
                    {
                        _currentConfig.PairedDevices.Add(new PairedDevice
                        {
                            DeviceName = _currentConfig.PairedDeviceName,
                            Token = _currentConfig.PairedToken
                        });
                        Save(); // Save migrated list
                    }
                }

                if (_currentConfig.VolumeUpButton == null)
                {
                    _currentConfig.VolumeUpButton = new ShortcutButton { Title = "Volume Up", ActionType = "System", ActionData = "volume_up" };
                }
                if (_currentConfig.VolumeDownButton == null)
                {
                    _currentConfig.VolumeDownButton = new ShortcutButton { Title = "Volume Down", ActionType = "System", ActionData = "volume_down" };
                }

                MigrateOrInitializeProfiles();
                SanitizeProfileButtons();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading config: {ex.Message}");
                _currentConfig = new AppConfig();
                MigrateOrInitializeProfiles();
            }
        }

        public static void SanitizeProfileButtons()
        {
            var config = Current;
            if (config.Profiles == null || config.Profiles.Count == 0) return;

            var existingIds = new HashSet<string>();
            foreach (var profile in config.Profiles)
            {
                if (!string.IsNullOrEmpty(profile.Id))
                {
                    existingIds.Add(profile.Id);
                }
            }

            bool modified = false;
            string defaultFallbackId = config.CurrentProfileId;
            if (string.IsNullOrEmpty(defaultFallbackId) || !existingIds.Contains(defaultFallbackId))
            {
                defaultFallbackId = config.Profiles[0].Id;
            }

            foreach (var profile in config.Profiles)
            {
                if (profile.Buttons != null)
                {
                    foreach (var button in profile.Buttons)
                    {
                        if ("Profile".Equals(button.ActionType, StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.IsNullOrEmpty(button.ActionData) || !existingIds.Contains(button.ActionData))
                            {
                                button.ActionData = defaultFallbackId;
                                modified = true;
                            }
                        }
                    }
                }
            }

            if (modified)
            {
                Save();
            }
        }

        public static void Save()
        {
            try
            {
                if (_currentConfig == null) _currentConfig = new AppConfig();
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_currentConfig, options);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving config: {ex.Message}");
            }
        }

        public static void ResetConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    File.Delete(ConfigPath);
                }
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "swift_dock_debug.log");
                if (File.Exists(logPath))
                {
                    File.Delete(logPath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error resetting config: {ex.Message}");
            }
            _currentConfig = new AppConfig();
            Save();
        }

        private const string RunRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "SwiftDock";

        public static bool IsAutoStartEnabled()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
                var value = key?.GetValue(AppName) as string;
                return !string.IsNullOrEmpty(value);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading registry for autostart: {ex.Message}");
                return false;
            }
        }

        public static void SetAutoStart(bool enable)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
                if (key == null) return;

                if (enable)
                {
                    string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName 
                                     ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SwiftDock.exe");
                    key.SetValue(AppName, $"\"{exePath}\"");
                }
                else
                {
                    if (key.GetValue(AppName) != null)
                    {
                        key.DeleteValue(AppName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating registry for autostart: {ex.Message}");
            }
        }
    }
}
