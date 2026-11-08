using System;
using System.Collections.Generic;
using System.Globalization;

namespace HydraPlaynite.Models
{
    public class HydraGameRecord
    {
        public string UniqueKey { get; set; }
        public string Title { get; set; }
        public string Shop { get; set; }
        public string ObjectId { get; set; }
        public string Source { get; set; }
        public string ExecutablePath { get; set; }
        public string InstallDirectory { get; set; }
        public string LaunchOptions { get; set; }
        public double PlayTimeInMilliseconds { get; set; }
        public DateTime? LastTimePlayed { get; set; }
        public ulong? InstalledSizeInBytes { get; set; }
        public bool IsDeleted { get; set; }
        public bool Favorite { get; set; }
        public bool HasActiveSteamImport { get; set; }
        public bool HasActiveEpicImport { get; set; }
        public bool HasEpicInstallation { get; set; }
        public bool HasActiveGogImport { get; set; }
        public bool HasGogInstallation { get; set; }
        public bool IsEmulator { get; set; }
        public bool HasRecompilation { get; set; }

        // Caminhos e URLs de arte (Ícone, Capa, Fundo/Hero e Logo)
        public string CustomOriginalIconPath { get; set; }
        public string CustomIconUrl { get; set; }
        public string OriginalIconPath { get; set; }
        public string IconUrl { get; set; }

        public string CustomOriginalCoverPath { get; set; }
        public string CustomCoverImageUrl { get; set; }
        public string CoverImageUrl { get; set; }
        public string LibraryImageUrl { get; set; }

        public string CustomOriginalHeroPath { get; set; }
        public string CustomHeroImageUrl { get; set; }
        public string OriginalHeroPath { get; set; }
        public string LibraryHeroImageUrl { get; set; }

        public string CustomOriginalLogoPath { get; set; }
        public string CustomLogoImageUrl { get; set; }
        public string LogoImageUrl { get; set; }

        public bool HasCustomIcon
        {
            get
            {
                return HasValue(CustomOriginalIconPath) ||
                       HasValue(CustomIconUrl) ||
                       IsLocalAssetUri(IconUrl);
            }
        }

        public bool HasCustomCover
        {
            get
            {
                return HasValue(CustomOriginalCoverPath) ||
                       HasValue(CustomCoverImageUrl) ||
                       IsLocalAssetUri(CoverImageUrl);
            }
        }

        public bool HasCustomHero
        {
            get
            {
                return HasValue(CustomOriginalHeroPath) ||
                       HasValue(CustomHeroImageUrl) ||
                       IsLocalAssetUri(LibraryHeroImageUrl);
            }
        }

        public ulong GetPlaytimeInSeconds()
        {
            return PlayTimeInMilliseconds > 0
                ? (ulong)Math.Round(PlayTimeInMilliseconds / 1000.0)
                : 0UL;
        }

        public static HydraGameRecord FromMergedDictionary(
            string uniqueKey,
            Dictionary<string, string> fields,
            string resolvedExePath,
            string resolvedInstallDir)
        {
            string title = GetField(fields, "title");
            string shop = GetField(fields, "shop") ?? "custom";
            string objectId = GetField(fields, "objectId") ?? GetField(fields, "objectID") ?? title;

            return new HydraGameRecord
            {
                UniqueKey = uniqueKey,
                Title = title,
                Shop = shop,
                ObjectId = objectId,
                Source = GetField(fields, "source"),
                ExecutablePath = resolvedExePath,
                InstallDirectory = resolvedInstallDir,
                LaunchOptions = GetField(fields, "launchOptions"),
                PlayTimeInMilliseconds = ParseDoubleField(fields, "playTimeInMilliseconds"),
                LastTimePlayed = ParseDateField(fields, "lastTimePlayed"),
                InstalledSizeInBytes = ParseUlongField(fields, "installedSizeInBytes") ?? ParseUlongField(fields, "sizeInBytes"),
                IsDeleted = IsTrueField(fields, "isDeleted"),
                Favorite = IsTrueField(fields, "favorite"),
                HasActiveSteamImport = IsTrueField(fields, "hasActiveSteamImport"),
                HasActiveEpicImport = IsTrueField(fields, "hasActiveEpicImport"),
                HasEpicInstallation = fields.ContainsKey("epicInstallation"),
                HasActiveGogImport = IsTrueField(fields, "hasActiveGogImport"),
                HasGogInstallation = fields.ContainsKey("gogInstallation"),
                HasRecompilation = IsTrueField(fields, "__hasRecompilation") ||
                                   HasRecompilationMarker(fields, resolvedExePath, resolvedInstallDir),
                IsEmulator = (fields.ContainsKey("romSizeBytes") ||
                              fields.ContainsKey("selectedDiscPath") ||
                              fields.ContainsKey("customLaunchOptions") ||
                              fields.ContainsKey("platform")) &&
                             !(IsTrueField(fields, "__hasRecompilation") ||
                               HasRecompilationMarker(fields, resolvedExePath, resolvedInstallDir)),

                CustomOriginalIconPath = GetField(fields, "customOriginalIconPath"),
                CustomIconUrl = GetField(fields, "customIconUrl"),
                OriginalIconPath = GetField(fields, "originalIconPath"),
                IconUrl = GetField(fields, "iconUrl"),

                CustomOriginalCoverPath = GetField(fields, "customOriginalCoverPath"),
                CustomCoverImageUrl = GetField(fields, "customCoverImageUrl"),
                CoverImageUrl = GetField(fields, "coverImageUrl"),
                LibraryImageUrl = GetField(fields, "libraryImageUrl"),

                CustomOriginalHeroPath = GetField(fields, "customOriginalHeroPath"),
                CustomHeroImageUrl = GetField(fields, "customHeroImageUrl"),
                OriginalHeroPath = GetField(fields, "originalHeroPath"),
                LibraryHeroImageUrl = GetField(fields, "libraryHeroImageUrl"),

                CustomOriginalLogoPath = GetField(fields, "customOriginalLogoPath"),
                CustomLogoImageUrl = GetField(fields, "customLogoImageUrl"),
                LogoImageUrl = GetField(fields, "logoImageUrl")
            };
        }

        private static string GetField(Dictionary<string, string> fields, string key)
        {
            string val;
            if (fields != null && fields.TryGetValue(key, out val) && !string.IsNullOrWhiteSpace(val))
                return val;
            return null;
        }

        private static bool IsTrueField(Dictionary<string, string> fields, string key)
        {
            return string.Equals(GetField(fields, key), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static double ParseDoubleField(Dictionary<string, string> fields, string key)
        {
            string raw = GetField(fields, key);
            double value;
            if (!string.IsNullOrEmpty(raw) &&
                double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }
            return 0;
        }

        private static ulong? ParseUlongField(Dictionary<string, string> fields, string key)
        {
            double val = ParseDoubleField(fields, key);
            return val > 0 ? (ulong?)Math.Round(val) : null;
        }

        private static DateTime? ParseDateField(Dictionary<string, string> fields, string key)
        {
            string raw = GetField(fields, key);
            DateTime dt;
            if (!string.IsNullOrEmpty(raw) && DateTime.TryParse(raw, out dt))
                return dt.ToLocalTime();
            return null;
        }

        private static bool HasValue(string value)
        {
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool IsLocalAssetUri(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.StartsWith("local:", StringComparison.OrdinalIgnoreCase);
        }

        public static bool HasRecompilationMarker(
            Dictionary<string, string> fields,
            string resolvedExePath = null,
            string resolvedInstallDir = null)
        {
            if (fields != null)
            {
                if (fields.ContainsKey("recompilationSlug"))
                    return true;

                string discsJson = GetField(fields, "discs");
                if (!string.IsNullOrEmpty(discsJson) &&
                    (discsJson.IndexOf("recompilationSlug", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     discsJson.IndexOf("recompilations", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     discsJson.IndexOf("\"isExecutable\":true", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }

                string rawExe = GetField(fields, "executablePath");
                if (!string.IsNullOrEmpty(rawExe) &&
                    rawExe.IndexOf("recompilations", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                string selectedDisc = GetField(fields, "selectedDiscPath");
                if (!string.IsNullOrEmpty(selectedDisc) &&
                    (selectedDisc.IndexOf("recompilations", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     selectedDisc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            if (!string.IsNullOrEmpty(resolvedExePath) &&
                resolvedExePath.IndexOf("recompilations", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(resolvedInstallDir) &&
                resolvedInstallDir.IndexOf("recompilations", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }
    }
}
