using System;
using HydraPlaynite.Models;

namespace HydraPlaynite.Services
{
    public static class HydraOriginDetector
    {
        public const string DefaultPlayniteSourceName = "Hydra";

        public static HydraGameOrigin DetectOrigin(HydraGameRecord rec)
        {
            if (rec == null)
                return HydraGameOrigin.Hydra;

            string source = (rec.Source ?? string.Empty).Trim().ToLowerInvariant();
            string shop = (rec.Shop ?? string.Empty).Trim().ToLowerInvariant();

            if (IsEmulatorOrigin(rec, source, shop))
                return HydraGameOrigin.Emulator;

            if (IsEpicOrigin(rec, source, shop))
                return HydraGameOrigin.Epic;

            if (IsSteamOrigin(rec, source))
                return HydraGameOrigin.Steam;

            return HydraGameOrigin.Hydra;
        }

        public static bool ShouldImport(HydraGameRecord rec)
        {
            return DetectOrigin(rec) == HydraGameOrigin.Hydra;
        }

        private static bool IsEmulatorOrigin(HydraGameRecord rec, string source, string shop)
        {
            return rec.IsEmulator ||
                   source == "emulator" ||
                   shop == "emulator" ||
                   shop == "launchbox" ||
                   shop == "retroarch" ||
                   source == "rom" ||
                   (rec.UniqueKey != null && rec.UniqueKey.StartsWith("emulator:", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsEpicOrigin(HydraGameRecord rec, string source, string shop)
        {
            return rec.HasActiveEpicImport ||
                   rec.HasEpicInstallation ||
                   source == "epic" ||
                   shop == "epic";
        }

        private static bool IsSteamOrigin(HydraGameRecord rec, string source)
        {
            string normalizedExePath = (rec.ExecutablePath ?? string.Empty).Replace('/', '\\').ToLowerInvariant();
            return rec.HasActiveSteamImport ||
                   source == "steam" ||
                   normalizedExePath.Contains("\\steamapps\\common\\");
        }
    }
}
