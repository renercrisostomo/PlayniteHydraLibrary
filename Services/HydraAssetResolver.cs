using System;
using System.IO;
using System.Linq;
using System.Net;
using HydraPlaynite.Models;
using Playnite.SDK;

namespace HydraPlaynite.Services
{
    public static class HydraAssetResolver
    {
        private const int DownloadTimeoutMilliseconds = 10000;
        private const int MinimumValidImageBytes = 64;

        private sealed class TimedWebClient : WebClient
        {
            private readonly int timeoutMs;

            public TimedWebClient(int timeoutMs = DownloadTimeoutMilliseconds)
            {
                this.timeoutMs = timeoutMs;
            }

            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest request = base.GetWebRequest(address);
                if (request != null)
                {
                    request.Timeout = timeoutMs;
                }
                return request;
            }
        }

        public static string ResolveIconCandidate(HydraGameRecord rec)
        {
            if (rec == null) return null;

            return FirstResolvedAsset(
                rec.CustomOriginalIconPath,
                rec.CustomIconUrl,
                rec.OriginalIconPath,
                rec.IconUrl);
        }

        public static string ResolveCoverCandidate(HydraGameRecord rec)
        {
            if (rec == null) return null;

            string customCover = FirstResolvedAsset(rec.CustomOriginalCoverPath, rec.CustomCoverImageUrl);
            if (!string.IsNullOrEmpty(customCover))
                return customCover;

            if (!string.IsNullOrWhiteSpace(rec.CoverImageUrl) &&
                rec.CoverImageUrl.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
            {
                string localCover = NormalizeAssetPathOrUrl(rec.CoverImageUrl);
                if (!string.IsNullOrEmpty(localCover))
                    return localCover;
            }

            if (IsSteamCatalogRecord(rec))
            {
                return string.Format(
                    "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{0}/library_600x900_2x.jpg",
                    rec.ObjectId);
            }

            return FirstResolvedAsset(rec.CoverImageUrl, rec.LibraryImageUrl);
        }

        public static string ResolveHeroCandidate(HydraGameRecord rec)
        {
            if (rec == null) return null;

            string resolvedHero = FirstResolvedAsset(
                rec.CustomOriginalHeroPath,
                rec.CustomHeroImageUrl,
                rec.OriginalHeroPath,
                rec.LibraryHeroImageUrl);

            if (!string.IsNullOrEmpty(resolvedHero))
                return resolvedHero;

            if (IsSteamCatalogRecord(rec))
            {
                return string.Format(
                    "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{0}/library_hero.jpg",
                    rec.ObjectId);
            }

            return null;
        }

        public static string NormalizeAssetPathOrUrl(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
                return null;

            string trimmed = rawValue.Trim();

            if (trimmed.StartsWith("local:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveHydraLocalProtocolPath(trimmed);
            }

            if (IsHttpUrl(trimmed))
                return trimmed;

            string candidateLocal = trimmed.Replace('/', '\\');
            if (File.Exists(candidateLocal))
                return candidateLocal;

            return null;
        }

        public static string ImportFileIntoPlayniteDb(IPlayniteAPI api, string pathOrUrl, Guid gameId)
        {
            if (api == null || string.IsNullOrWhiteSpace(pathOrUrl))
                return null;

            try
            {
                if (File.Exists(pathOrUrl))
                    return api.Database.AddFile(pathOrUrl, gameId);

                if (IsHttpUrl(pathOrUrl))
                    return DownloadAndAddHttpImage(api, pathOrUrl, gameId);
            }
            catch { }

            return null;
        }

        private static string FirstResolvedAsset(params string[] rawCandidates)
        {
            if (rawCandidates == null) return null;

            return rawCandidates
                .Select(NormalizeAssetPathOrUrl)
                .FirstOrDefault(resolved => !string.IsNullOrEmpty(resolved));
        }

        private static bool IsSteamCatalogRecord(HydraGameRecord rec)
        {
            return string.Equals(rec.Shop, "steam", StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrEmpty(rec.ObjectId);
        }

        private static bool IsHttpUrl(string value)
        {
            return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveHydraLocalProtocolPath(string localUri)
        {
            string relativePart = localUri.StartsWith("local:", StringComparison.OrdinalIgnoreCase)
                ? localUri.Substring(6)
                : localUri.Substring(5);

            try { relativePart = Uri.UnescapeDataString(relativePart); } catch { }
            relativePart = relativePart.TrimStart('/', '\\').Replace('/', '\\');

            if (File.Exists(relativePart))
                return relativePart;

            string assetsFolder = GetHydraAssetsFolder();
            string directAssetPath = Path.Combine(assetsFolder, relativePart);
            if (File.Exists(directAssetPath))
                return directAssetPath;

            string fileNameOnly = Path.GetFileName(relativePart);
            if (!string.IsNullOrEmpty(fileNameOnly))
            {
                string assetByFileName = Path.Combine(assetsFolder, fileNameOnly);
                if (File.Exists(assetByFileName))
                    return assetByFileName;
            }

            return null;
        }

        private static string GetHydraAssetsFolder()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string lowerCasePath = Path.Combine(appData, "hydra", "Assets");
            if (Directory.Exists(lowerCasePath))
                return lowerCasePath;

            string upperCasePath = Path.Combine(appData, "Hydra", "Assets");
            return Directory.Exists(upperCasePath) ? upperCasePath : lowerCasePath;
        }

        private static string DownloadAndAddHttpImage(IPlayniteAPI api, string imageUrl, Guid gameId)
        {
            string extension = DetectImageExtension(imageUrl);
            string tempFile = Path.Combine(Path.GetTempPath(), "hydra_media_" + Guid.NewGuid().ToString("N") + extension);

            try
            {
                using (TimedWebClient client = new TimedWebClient(DownloadTimeoutMilliseconds))
                {
                    client.Headers.Add("User-Agent", "Mozilla/5.0 PlayniteHydraLibrary/0.7.0");
                    client.DownloadFile(imageUrl, tempFile);
                }

                if (File.Exists(tempFile) && new FileInfo(tempFile).Length > MinimumValidImageBytes)
                {
                    return api.Database.AddFile(tempFile, gameId);
                }
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }

            return null;
        }

        private static string DetectImageExtension(string url)
        {
            if (url.IndexOf(".jpg", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf(".jpeg", StringComparison.OrdinalIgnoreCase) >= 0)
                return ".jpg";

            if (url.IndexOf(".ico", StringComparison.OrdinalIgnoreCase) >= 0)
                return ".ico";

            if (url.IndexOf(".webp", StringComparison.OrdinalIgnoreCase) >= 0)
                return ".webp";

            return ".png";
        }
    }
}
