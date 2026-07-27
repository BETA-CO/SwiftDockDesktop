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
        public string ActionType { get; set; } = "App"; // "App", "URL", "System", "Profile"
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
    }

    public class InstalledApp
    {
        public string DisplayName { get; set; } = "";
        public string ShortcutPath { get; set; } = "";
        public System.Windows.Media.ImageSource? Icon { get; set; }
    }

    public class SystemActionItem
    {
        public string ActionId { get; set; } = "";
        public string Label { get; set; } = "";
        public string Glyph { get; set; } = "";
    }
}
