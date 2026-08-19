using System;
using System.Collections.Generic;

namespace SwiftDock
{


    public class ShortcutButton
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = "New Button";
        public string Color { get; set; } = "#6366F1"; // Default Indigo accent
        public string Icon { get; set; } = "default";
        public string ActionType { get; set; } = "App"; // "App", "URL", "System", "Profile", "Hotkey"
        public string ActionData { get; set; } = "";
    }

    public class DeviceConnection
    {
        public string DeviceName { get; set; } = "";
        public string ConnectionTime { get; set; } = "";
    }

    public class PairedDevice
    {
        public string DeviceName { get; set; } = "";
        public string Token { get; set; } = "";
    }

    public class Profile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "New Profile";
        public bool IsLocked { get; set; } = false;
        public List<ShortcutButton> Buttons { get; set; } = new List<ShortcutButton>();

        public int PageNumber
        {
            get
            {
                int index = ConfigManager.Current?.Profiles?.IndexOf(this) ?? -1;
                return index >= 0 ? index + 1 : 1;
            }
        }

        public int ConfiguredButtonsCount
        {
            get
            {
                if (Buttons == null) return 0;
                int count = 0;
                foreach (var btn in Buttons)
                {
                    if (!string.IsNullOrEmpty(btn.ActionData))
                    {
                        count++;
                    }
                }
                return count;
            }
        }
    }

    public class SavedWebsiteItem
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string Icon { get; set; } = "";
        public string ActionData { get; set; } = "";
    }

    public class AppConfig
    {
        public string DeviceName { get; set; } = Environment.MachineName;
        public bool AutoStartOnBoot { get; set; } = false;
        public string PairedToken { get; set; } = "";
        public string PairedDeviceName { get; set; } = "";
        public List<PairedDevice> PairedDevices { get; set; } = new List<PairedDevice>();
        public List<ShortcutButton> Buttons { get; set; } = new List<ShortcutButton>();
        public List<DeviceConnection> ConnectionHistory { get; set; } = new List<DeviceConnection>();
        public List<Profile> Profiles { get; set; } = new List<Profile>();
        public string CurrentProfileId { get; set; } = "";
        public List<SavedWebsiteItem> SavedWebsites { get; set; } = new List<SavedWebsiteItem>();
        public List<HotkeyActionItem> SavedCustomHotkeys { get; set; } = new List<HotkeyActionItem>();
        public ShortcutButton VolumeUpButton { get; set; } = new ShortcutButton { Title = "Volume Up", ActionType = "System", ActionData = "volume_up" };
        public ShortcutButton VolumeDownButton { get; set; } = new ShortcutButton { Title = "Volume Down", ActionType = "System", ActionData = "volume_down" };
        public string ProfilePin { get; set; } = "";
        public bool EnableFingerprintUnlock { get; set; } = true;
    }

    public class InstalledApp
    {
        public string DisplayName { get; set; } = "";
        public string ShortcutPath { get; set; } = "";
        public System.Windows.Media.ImageSource? Icon { get; set; }
    }

    public class CachedInstalledApp
    {
        public string DisplayName { get; set; } = "";
        public string ShortcutPath { get; set; } = "";
        public string IconBase64 { get; set; } = "";
    }

    public class SystemActionItem
    {
        public string Category { get; set; } = "";
        public string ActionId { get; set; } = "";
        public string Label { get; set; } = "";
        public string Glyph { get; set; } = "";
    }

    public class HotkeyActionItem
    {
        public string Category { get; set; } = "";
        public string ActionId { get; set; } = "";
        public string Label { get; set; } = "";
        public string KeysDisplay { get; set; } = "";
        public string Glyph { get; set; } = "";
    }
}
