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

                MigrateOrInitializeProfiles();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading config: {ex.Message}");
                _currentConfig = new AppConfig();
                MigrateOrInitializeProfiles();
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
    }
}
