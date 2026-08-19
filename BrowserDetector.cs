using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace SwiftDockDesktop
{
    public class BrowserProfileOption
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string ExePath { get; set; } = "";
        public string ProfileArg { get; set; } = "";

        public override string ToString()
        {
            return DisplayName;
        }
    }

    public static class BrowserDetector
    {
        public static List<BrowserProfileOption> GetInstalledBrowserProfiles()
        {
            var options = new List<BrowserProfileOption>();

            // 1. Default System Browser
            options.Add(new BrowserProfileOption
            {
                Id = "default",
                DisplayName = "Default System Browser",
                ExePath = "",
                ProfileArg = ""
            });

            try
            {
                // Scan Windows Registry for installed browsers
                var installedBrowsers = FindBrowsersInRegistry();

                foreach (var browser in installedBrowsers)
                {
                    string browserName = browser.Key;
                    string exePath = browser.Value;

                    if (browserName.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
                    {
                        var chromeProfiles = DetectChromiumProfiles("Google Chrome", exePath, "Google\\Chrome");
                        if (chromeProfiles.Count > 0)
                        {
                            options.AddRange(chromeProfiles);
                        }
                        else
                        {
                            options.Add(new BrowserProfileOption
                            {
                                Id = "chrome_default",
                                DisplayName = "Google Chrome",
                                ExePath = exePath,
                                ProfileArg = ""
                            });
                        }
                    }
                    else if (browserName.Contains("Edge", StringComparison.OrdinalIgnoreCase))
                    {
                        var edgeProfiles = DetectChromiumProfiles("Microsoft Edge", exePath, "Microsoft\\Edge");
                        if (edgeProfiles.Count > 0)
                        {
                            options.AddRange(edgeProfiles);
                        }
                        else
                        {
                            options.Add(new BrowserProfileOption
                            {
                                Id = "edge_default",
                                DisplayName = "Microsoft Edge",
                                ExePath = exePath,
                                ProfileArg = ""
                            });
                        }
                    }
                    else if (browserName.Contains("Brave", StringComparison.OrdinalIgnoreCase))
                    {
                        var braveProfiles = DetectChromiumProfiles("Brave", exePath, "BraveSoftware\\Brave-Browser");
                        if (braveProfiles.Count > 0)
                        {
                            options.AddRange(braveProfiles);
                        }
                        else
                        {
                            options.Add(new BrowserProfileOption
                            {
                                Id = "brave_default",
                                DisplayName = "Brave Browser",
                                ExePath = exePath,
                                ProfileArg = ""
                            });
                        }
                    }
                    else
                    {
                        // Firefox, Opera, Vivaldi, etc.
                        options.Add(new BrowserProfileOption
                        {
                            Id = browserName.ToLower().Replace(" ", "_"),
                            DisplayName = browserName,
                            ExePath = exePath,
                            ProfileArg = ""
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error detecting browsers: {ex.Message}");
            }

            return options;
        }

        private static Dictionary<string, string> FindBrowsersInRegistry()
        {
            var browsers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string[] registryKeys = new string[]
            {
                @"SOFTWARE\Clients\StartMenuInternet",
                @"SOFTWARE\WOW6432Node\Clients\StartMenuInternet"
            };

            foreach (var rootKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var subKeyPath in registryKeys)
                {
                    using var key = rootKey.OpenSubKey(subKeyPath);
                    if (key == null) continue;

                    foreach (var name in key.GetSubKeyNames())
                    {
                        using var browserKey = key.OpenSubKey(name);
                        if (browserKey == null) continue;

                        string displayName = browserKey.GetValue("") as string ?? name;
                        using var commandKey = browserKey.OpenSubKey(@"shell\open\command");
                        if (commandKey == null) continue;

                        string rawCmd = commandKey.GetValue("") as string ?? "";
                        string exePath = ExtractExePath(rawCmd);

                        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                        {
                            if (!browsers.ContainsKey(displayName))
                            {
                                browsers[displayName] = exePath;
                            }
                        }
                    }
                }
            }

            return browsers;
        }

        private static List<BrowserProfileOption> DetectChromiumProfiles(string appTitle, string exePath, string localAppDataSubPath)
        {
            var result = new List<BrowserProfileOption>();
            try
            {
                string localStatePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    localAppDataSubPath,
                    "User Data",
                    "Local State"
                );

                if (File.Exists(localStatePath))
                {
                    string jsonText = File.ReadAllText(localStatePath);
                    using var doc = JsonDocument.Parse(jsonText);

                    if (doc.RootElement.TryGetProperty("profile", out var profileElem) &&
                        profileElem.TryGetProperty("info_cache", out var infoCacheElem))
                    {
                        foreach (var prop in infoCacheElem.EnumerateObject())
                        {
                            string dirName = prop.Name; // e.g. "Profile 1" or "Default"
                            string profileName = dirName;

                            if (prop.Value.TryGetProperty("name", out var nameElem))
                            {
                                profileName = nameElem.GetString() ?? dirName;
                            }

                            string label = $"{appTitle} ({profileName})";
                            result.Add(new BrowserProfileOption
                            {
                                Id = $"{appTitle.ToLower().Replace(" ", "_")}_{dirName.ToLower().Replace(" ", "_")}",
                                DisplayName = label,
                                ExePath = exePath,
                                ProfileArg = $"--profile-directory=\"{dirName}\""
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing {appTitle} profiles: {ex.Message}");
            }

            return result;
        }

        private static string ExtractExePath(string rawCmd)
        {
            if (string.IsNullOrWhiteSpace(rawCmd)) return "";
            rawCmd = rawCmd.Trim();
            if (rawCmd.StartsWith("\""))
            {
                int endQuote = rawCmd.IndexOf("\"", 1);
                if (endQuote > 1) return rawCmd.Substring(1, endQuote - 1);
            }
            int spaceIdx = rawCmd.IndexOf(" ");
            if (spaceIdx > 0) return rawCmd.Substring(0, spaceIdx);
            return rawCmd;
        }
    }
}
