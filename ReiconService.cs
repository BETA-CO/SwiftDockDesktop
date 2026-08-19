using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SwiftDock
{
    public class ReiconItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public string Keywords { get; set; } = "";
        public string PathData { get; set; } = "";
        public string Glyph { get; set; } = "";
    }

    public static class ReiconService
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        private static readonly Dictionary<string, List<ReiconItem>> SearchCache = new Dictionary<string, List<ReiconItem>>(StringComparer.OrdinalIgnoreCase);
        private static List<ReiconItem>? _defaultCatalogCache;

        public static async Task<List<ReiconItem>> SearchAsync(string query, string category = "All")
        {
            query = (query ?? "").Trim().ToLowerInvariant();
            category = (category ?? "All").Trim();

            string cacheKey = $"{query}|{category}";
            if (SearchCache.TryGetValue(cacheKey, out var cachedResults))
            {
                return cachedResults;
            }

            if (string.IsNullOrEmpty(query))
            {
                var defaultItems = await GetDefaultRichCatalogAsync();
                var filteredDefault = FilterByCategory(defaultItems, category);
                SearchCache[cacheKey] = filteredDefault;
                return filteredDefault;
            }

            try
            {
                string apiUrl = $"https://api.iconify.design/search?query={Uri.EscapeDataString(query)}&limit=48";
                string json = await _httpClient.GetStringAsync(apiUrl);

                var iconIdsByPrefix = new Dictionary<string, List<string>>();

                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("icons", out var iconsArray) && iconsArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in iconsArray.EnumerateArray())
                        {
                            string rawId = el.GetString() ?? "";
                            if (string.IsNullOrEmpty(rawId) || !rawId.Contains(":")) continue;

                            string[] parts = rawId.Split(':');
                            string prefix = parts[0];
                            string name = parts[1];

                            string cat = GetCategoryFromPrefix(prefix);
                            if (!category.Equals("All", StringComparison.OrdinalIgnoreCase) && 
                                !cat.Equals(category, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (!iconIdsByPrefix.ContainsKey(prefix))
                            {
                                iconIdsByPrefix[prefix] = new List<string>();
                            }
                            iconIdsByPrefix[prefix].Add(name);
                        }
                    }
                }

                var results = new List<ReiconItem>();

                foreach (var kvp in iconIdsByPrefix.Take(5))
                {
                    string prefix = kvp.Key;
                    string namesList = string.Join(",", kvp.Value.Take(12));
                    string batchUrl = $"https://api.iconify.design/{prefix}.json?icons={namesList}";

                    try
                    {
                        string batchJson = await _httpClient.GetStringAsync(batchUrl);
                        using (JsonDocument bdoc = JsonDocument.Parse(batchJson))
                        {
                            var broot = bdoc.RootElement;
                            if (broot.TryGetProperty("icons", out var iconsObj) && iconsObj.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var iconProp in iconsObj.EnumerateObject())
                                {
                                    string iconName = iconProp.Name;
                                    if (iconProp.Value.TryGetProperty("body", out var bodyEl))
                                    {
                                        string bodyXml = bodyEl.GetString() ?? "";
                                        string pathData = ExtractPathDataFromSvgBody(bodyXml);
                                        if (string.IsNullOrEmpty(pathData)) continue;

                                        string cleanTitle = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(iconName.Replace("-", " "));
                                        string categoryName = GetCategoryFromPrefix(prefix);

                                        results.Add(new ReiconItem
                                        {
                                            Id = "svgpath:" + pathData,
                                            Name = cleanTitle,
                                            Category = categoryName,
                                            Keywords = $"{prefix} {iconName} {query}",
                                            PathData = pathData,
                                            Glyph = "\uE8A9"
                                        });
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Batch fetch failed for {prefix}: {ex.Message}");
                    }
                }

                if (results.Count > 0)
                {
                    SearchCache[cacheKey] = results;
                    return results;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Reicon dynamic search failed: {ex.Message}");
            }

            var fallback = GetFallbackCatalog(query, category);
            SearchCache[cacheKey] = fallback;
            return fallback;
        }

        private static async Task<List<ReiconItem>> GetDefaultRichCatalogAsync()
        {
            if (_defaultCatalogCache != null && _defaultCatalogCache.Count > 0)
            {
                return _defaultCatalogCache;
            }

            var items = new List<ReiconItem>();

            var defaultIconSpecs = new Dictionary<string, string[]>
            {
                { "ri", new[] { "home-line", "settings-3-line", "file-copy-line", "clipboard-line", "scissors-cut-line", "text", "edit-line", "delete-bin-line", "save-line", "search-line", "lock-line", "user-line", "star-line", "wifi-line", "bluetooth-line", "volume-up-line", "mic-line", "camera-line", "code-s-slash-line", "terminal-box-line", "folder-line", "play-line", "pause-line", "restart-line", "rocket-line" } },
                { "logos", new[] { "discord", "spotify", "visual-studio-code", "chrome", "figma", "youtube", "twitch", "github-icon", "steam", "google-drive", "obs-studio" } },
                { "tabler", new[] { "device-desktop", "brand-windows", "brand-apple", "sun", "moon", "bell", "battery-charging", "shield-check" } }
            };

            foreach (var kvp in defaultIconSpecs)
            {
                string prefix = kvp.Key;
                string namesList = string.Join(",", kvp.Value);
                string batchUrl = $"https://api.iconify.design/{prefix}.json?icons={namesList}";

                try
                {
                    string batchJson = await _httpClient.GetStringAsync(batchUrl);
                    using (JsonDocument bdoc = JsonDocument.Parse(batchJson))
                    {
                        var broot = bdoc.RootElement;
                        if (broot.TryGetProperty("icons", out var iconsObj) && iconsObj.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var iconProp in iconsObj.EnumerateObject())
                            {
                                string iconName = iconProp.Name;
                                if (iconProp.Value.TryGetProperty("body", out var bodyEl))
                                {
                                    string bodyXml = bodyEl.GetString() ?? "";
                                    string pathData = ExtractPathDataFromSvgBody(bodyXml);
                                    if (string.IsNullOrEmpty(pathData)) continue;

                                    string cleanTitle = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(iconName.Replace("-", " "));
                                    string categoryName = GetCategoryFromPrefix(prefix);

                                    items.Add(new ReiconItem
                                    {
                                        Id = "svgpath:" + pathData,
                                        Name = cleanTitle,
                                        Category = categoryName,
                                        Keywords = $"{prefix} {iconName}",
                                        PathData = pathData,
                                        Glyph = "\uE8A9"
                                    });
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed loading default icons for {prefix}: {ex.Message}");
                }
            }

            if (items.Count == 0)
            {
                items = GetFallbackCatalog("", "All");
            }

            _defaultCatalogCache = items;
            return items;
        }

        private static List<ReiconItem> FilterByCategory(List<ReiconItem> items, string category)
        {
            if (string.Equals(category, "All", StringComparison.OrdinalIgnoreCase))
            {
                return items;
            }
            return items.Where(i => i.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public static string ExtractPathDataFromSvgBody(string svgBodyXml)
        {
            if (string.IsNullOrEmpty(svgBodyXml)) return "";

            var pathParts = new List<string>();

            // 1. Extract <path d="..." />
            var pathMatches = Regex.Matches(svgBodyXml, @"<path[^>]*\bd=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            foreach (Match m in pathMatches)
            {
                if (m.Groups.Count > 1 && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
                {
                    pathParts.Add(m.Groups[1].Value.Trim());
                }
            }

            // 2. Convert <circle cx="X" cy="Y" r="R" />
            var circleMatches = Regex.Matches(svgBodyXml, @"<circle[^>]*\bcx=[""']([^""']+)[""'][^>]*\bcy=[""']([^""']+)[""'][^>]*\br=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            foreach (Match m in circleMatches)
            {
                if (double.TryParse(m.Groups[1].Value, out double cx) &&
                    double.TryParse(m.Groups[2].Value, out double cy) &&
                    double.TryParse(m.Groups[3].Value, out double r))
                {
                    pathParts.Add($"M {cx - r} {cy} A {r} {r} 0 1 0 {cx + r} {cy} A {r} {r} 0 1 0 {cx - r} {cy} Z");
                }
            }

            // 3. Convert <rect x="X" y="Y" width="W" height="H" />
            var rectMatches = Regex.Matches(svgBodyXml, @"<rect[^>]*\bx=[""']([^""']+)[""'][^>]*\by=[""']([^""']+)[""'][^>]*\bwidth=[""']([^""']+)[""'][^>]*\bheight=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            foreach (Match m in rectMatches)
            {
                if (double.TryParse(m.Groups[1].Value, out double x) &&
                    double.TryParse(m.Groups[2].Value, out double y) &&
                    double.TryParse(m.Groups[3].Value, out double w) &&
                    double.TryParse(m.Groups[4].Value, out double h))
                {
                    pathParts.Add($"M {x} {y} h {w} v {h} h -{w} Z");
                }
            }

            // 4. Convert <polygon points="..." />
            var polyMatches = Regex.Matches(svgBodyXml, @"<polygon[^>]*\bpoints=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            foreach (Match m in polyMatches)
            {
                string pts = m.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(pts))
                {
                    var tokens = Regex.Split(pts, @"[\s,]+");
                    if (tokens.Length >= 2)
                    {
                        var sb = new System.Text.StringBuilder($"M {tokens[0]} {tokens[1]}");
                        for (int i = 2; i < tokens.Length - 1; i += 2)
                        {
                            sb.Append($" L {tokens[i]} {tokens[i + 1]}");
                        }
                        sb.Append(" Z");
                        pathParts.Add(sb.ToString());
                    }
                }
            }

            return string.Join(" ", pathParts);
        }

        private static string GetCategoryFromPrefix(string prefix)
        {
            switch (prefix.ToLowerInvariant())
            {
                case "logos": case "simple-icons": case "brand": case "cib": case "clarity": return "Brands";
                case "material-symbols": case "mdi": case "ic": case "uil": case "carbon": return "Interface";
                case "tabler": case "solar": case "ri": case "octicon": case "codicon": return "Development";
                case "fluent-emoji": case "line-md": case "ph": case "bi": return "Media";
                default: return "Interface";
            }
        }

        public static List<string> GetCategories()
        {
            return new List<string> { "All", "Interface", "Development", "Media", "Brands" };
        }

        public static List<ReiconItem> Search(string query, string category = "All")
        {
            return GetFallbackCatalog(query, category);
        }

        private static List<ReiconItem> GetFallbackCatalog(string query, string category)
        {
            var fallback = new List<ReiconItem>
            {
                new ReiconItem { Id = "svgpath:M21 20a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V9.49a1 1 0 0 1 .386-.79l8-6.223a1 1 0 0 1 1.228 0l8 6.223a1 1 0 0 1 .386.79zm-2-1V9.978l-7-5.444l-7 5.444V19z", Name = "Home", Category = "Interface", PathData = "M21 20a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V9.49a1 1 0 0 1 .386-.79l8-6.223a1 1 0 0 1 1.228 0l8 6.223a1 1 0 0 1 .386.79zm-2-1V9.978l-7-5.444l-7 5.444V19z", Glyph = "\uE80F" },
                new ReiconItem { Id = "svgpath:M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zm0 2a5 5 0 1 1 0-10 5 5 0 0 1 0 10z", Name = "Settings", Category = "Interface", PathData = "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zm0 2a5 5 0 1 1 0-10 5 5 0 0 1 0 10z", Glyph = "\uE713" }
            };

            for (int i = 1; i <= 12; i++)
            {
                fallback.Add(new ReiconItem
                {
                    Id = $"text:F{i}",
                    Name = $"Keyboard F{i}",
                    Category = "Interface",
                    Keywords = $"keyboard f{i} f{i} function key",
                    PathData = "",
                    Glyph = $"text:F{i}"
                });
            }

            var res = fallback.AsEnumerable();
            if (!category.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                res = res.Where(i => i.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrEmpty(query))
            {
                res = res.Where(i => i.Name.ToLowerInvariant().Contains(query) || i.Category.ToLowerInvariant().Contains(query));
            }
            return res.ToList();
        }
    }
}
