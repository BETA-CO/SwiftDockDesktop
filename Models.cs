using System;
using System.Collections.Generic;

namespace SwiftDock
{
    public class MacroStep
    {
        public string Type { get; set; } = "Delay"; // "App", "Command", "URL", "System", "Delay"
        public string Data { get; set; } = "";
        public int DelayMs { get; set; } = 500;

        // Visual helper properties for WPF data binding
        public string DisplayBadge
        {
            get
            {
                return Type switch
                {
                    "App" => "📱",
                    "Command" => "💻",
                    "URL" => "🌐",
                    "System" => "⚙️",
                    "Delay" => "⏱️",
                    _ => "❓"
                };
            }
        }

        public string DisplayType
        {
            get
            {
                return Type switch
                {
                    "App" => "Launch Application",
                    "Command" => "Run Cmd/Script",
                    "URL" => "Open Website",
                    "System" => "System Command",
                    "Delay" => "Delay / Pause",
                    _ => "Action Step"
                };
            }
        }

        public string DisplayTitle
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Data))
                {
                    return Type == "Delay" ? $"Wait {DelayMs}ms" : "(unconfigured)";
                }

                if (Type == "App")
                {
                    try
                    {
                        return System.IO.Path.GetFileName(Data);
                    }
                    catch
                    {
                        return Data;
                    }
                }

                if (Type == "System")
                {
                    return Data switch
                    {
                        "volume_up" => "Volume Up",
                        "volume_down" => "Volume Down",
                        "volume_mute" => "Mute Volume",
                        "media_play_pause" => "Play/Pause Media",
                        "media_next" => "Next Track",
                        "media_prev" => "Previous Track",
                        "media_forward_10" => "Skip 10s Forward",
                        "media_backward_10" => "Skip 10s Backward",
                        "brightness_up" => "Increase Brightness",
                        "brightness_down" => "Decrease Brightness",
                        "mic_toggle" => "Toggle Mic",
                        "pc_shutdown" => "PC Shutdown",
                        "pc_sleep" => "PC Sleep",
                        "pc_lock" => "PC Lock",
                        "pc_restart" => "PC Restart",
                        "wifi_toggle" => "Toggle Wi-Fi",
                        "bluetooth_toggle" => "Toggle Bluetooth",
                        "screen_record" => "Screen Recording",
                        "screenshot" => "Take Screenshot",
                        "home_screen" => "Home Screen",
                        "close_all_apps" => "Close All Apps",
                        _ => Data
                    };
                }

                return Data;
            }
        }
    }

    public class ShortcutButton
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = "New Button";
        public string Color { get; set; } = "#6366F1"; // Default Indigo accent
        public string Icon { get; set; } = "default";
        public string ActionType { get; set; } = "App"; // "App", "Command", "URL", "Macro", "System"
        public string ActionData { get; set; } = "";
        public List<MacroStep> MacroSteps { get; set; } = new List<MacroStep>();
    }

    public class DeviceConnection
    {
        public string DeviceName { get; set; } = "";
        public string ConnectionTime { get; set; } = "";
    }

    public class CommandPresetItem
    {
        public string DisplayName { get; set; } = "";
        public string CommandText { get; set; } = "";
    }

    public class CommandLanguageCategory
    {
        public string Name { get; set; } = "";
        public string Color { get; set; } = "#FFFFFF";
        public List<CommandPresetItem> Presets { get; set; } = new List<CommandPresetItem>();
    }

    public class Profile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "New Profile";
        public List<ShortcutButton> Buttons { get; set; } = new List<ShortcutButton>();
    }

    public class AppConfig
    {
        public string DeviceName { get; set; } = Environment.MachineName;
        public string PairedToken { get; set; } = "";
        public string PairedDeviceName { get; set; } = "";
        public List<ShortcutButton> Buttons { get; set; } = new List<ShortcutButton>();
        public List<DeviceConnection> ConnectionHistory { get; set; } = new List<DeviceConnection>();
        public List<Profile> Profiles { get; set; } = new List<Profile>();
        public string CurrentProfileId { get; set; } = "";
        public List<CommandLanguageCategory> CommandPresets { get; set; } = new List<CommandLanguageCategory>();
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
