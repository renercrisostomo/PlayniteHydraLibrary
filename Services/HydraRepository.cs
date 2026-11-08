using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HydraPlaynite.Models;
using Playnite.SDK;

namespace HydraPlaynite.Services
{
    public class HydraRepository
    {
        private const double RootDirectoryBonusScore = 100.0;
        private const double SubfolderDepthPenaltyPerLevel = 20.0;
        private const double TitleMatchBonusScore = 80.0;
        private const double FolderNameMatchBonusScore = 50.0;
        private const double UnrealShippingSubfolderPenalty = 40.0;
        private const double AuxiliaryToolPenalty = 60.0;
        private const double MaxFileSizeTieBreakerScore = 15.0;

        private readonly ILogger logger;

        private static readonly string[] BinarySubfolderNames = new string[]
        {
            "binaries", "win64", "win32", "x64", "x86", "bin", "system",
            "engine", "retail", "game", "release", "shipping", "scripts"
        };

        private static readonly string[] IgnoredExecutableKeywords = new string[]
        {
            "unins", "setup", "crash", "redist", "vcredist", "dxsetup",
            "unitycrashhandler", "dotnet", "directx", "physx", "easyanticheat",
            "battleye", "cep", "launcher_helper", "updater"
        };

        private static readonly string[] SecondaryUtilityKeywords = new string[]
        {
            "server", "config", "editor", "benchmark", " handler", "reporter"
        };

        public HydraRepository(ILogger logger)
        {
            this.logger = logger;
        }

        public List<HydraGameRecord> LoadAllGames()
        {
            return LoadGamesFromDirectories(GetCandidateDatabaseDirectories());
        }

        public List<HydraGameRecord> LoadGamesFromDirectories(IEnumerable<string> databaseDirectories)
        {
            Dictionary<string, Dictionary<string, string>> mergedRecords = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, string>> downloadRecords = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            if (databaseDirectories != null)
            {
                foreach (string dbPath in databaseDirectories)
                {
                    if (string.IsNullOrEmpty(dbPath) || !Directory.Exists(dbPath))
                        continue;

                    foreach (FileInfo file in GetOrderedLevelDbFiles(dbPath))
                    {
                        ProcessDatabaseFile(file.FullName, mergedRecords, downloadRecords);
                    }
                }
            }

            Dictionary<string, string> completedDownloadFolders;
            HashSet<string> incompleteDownloads;
            ClassifyDownloads(downloadRecords, out completedDownloadFolders, out incompleteDownloads);

            return BuildGameRecords(mergedRecords, completedDownloadFolders, incompleteDownloads);
        }

        private static string[] GetCandidateDatabaseDirectories()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return new string[]
            {
                Path.Combine(appData, "hydra", "hydra-db"),
                Path.Combine(appData, "Hydra", "hydra-db"),
                Path.Combine(appData, "hydralauncher", "hydra-db"),
                Path.Combine(appData, "hydra", "hydra-db-staging"),
                Path.Combine(appData, "Hydra", "data.db"),
                Path.Combine(appData, "hydra", "leveldb"),
                Path.Combine(appData, "hydra", "Local Storage", "leveldb")
            };
        }

        private static FileInfo[] GetOrderedLevelDbFiles(string dbPath)
        {
            return new DirectoryInfo(dbPath)
                .GetFiles()
                .Where(f => f.Extension.Equals(".ldb", StringComparison.OrdinalIgnoreCase) ||
                            f.Extension.Equals(".sst", StringComparison.OrdinalIgnoreCase) ||
                            f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f =>
                {
                    long sequenceNumber;
                    return long.TryParse(Path.GetFileNameWithoutExtension(f.Name), out sequenceNumber) ? sequenceNumber : 0L;
                })
                .ThenBy(f => f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ToArray();
        }

        private void ProcessDatabaseFile(
            string filePath,
            Dictionary<string, Dictionary<string, string>> mergedRecords,
            Dictionary<string, Dictionary<string, string>> downloadRecords)
        {
            try
            {
                List<string> jsonObjects = LevelDbFileReader.ReadAllJsonFromFile(filePath);
                foreach (string json in jsonObjects)
                {
                    Dictionary<string, string> fields = HydraJsonParser.ParseTopLevelJson(json);
                    if (fields.Count == 0)
                        continue;

                    string shop = fields.ContainsKey("shop") && !string.IsNullOrWhiteSpace(fields["shop"])
                        ? fields["shop"].Trim()
                        : "custom";

                    string objectId = GetFirstNonEmpty(fields, "objectId", "objectID");

                    if (IsDownloadRecord(fields))
                    {
                        UpdateDownloadRecord(fields, shop, objectId, downloadRecords);
                        continue;
                    }

                    if (!IsGameOrAssetRecord(fields))
                        continue;

                    string uniqueKey = !string.IsNullOrEmpty(objectId)
                        ? (shop + ":" + objectId)
                        : ("custom:" + fields["title"].Trim());

                    MergeRecordFields(mergedRecords, uniqueKey, fields);
                }
            }
            catch (Exception ex)
            {
                if (logger != null)
                {
                    logger.Debug(ex, "HydraRepository: Falha ao processar arquivo " + filePath);
                }
            }
        }

        public static bool IsDownloadRecord(Dictionary<string, string> fields)
        {
            if (fields == null)
                return false;

            if (fields.ContainsKey("playTimeInMilliseconds") || fields.ContainsKey("isDeleted"))
                return false;

            return fields.ContainsKey("downloader") ||
                   fields.ContainsKey("downloadPath") ||
                   (fields.ContainsKey("folderName") && fields.ContainsKey("progress"));
        }

        public static void UpdateDownloadRecord(
            Dictionary<string, string> fields,
            string shop,
            string objectId,
            Dictionary<string, Dictionary<string, string>> downloadRecords)
        {
            if (string.IsNullOrEmpty(objectId) || fields == null)
                return;

            string key = shop + ":" + objectId;
            Dictionary<string, string> existing;
            if (!downloadRecords.TryGetValue(key, out existing))
            {
                existing = new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase);
                if (IsDownloadCompleted(existing))
                {
                    existing["__completedOnce"] = "true";
                }
                downloadRecords[key] = existing;
                return;
            }

            bool incomingCompleted = IsDownloadCompleted(fields);
            bool alreadyCompleted = existing.ContainsKey("__completedOnce") || IsDownloadCompleted(existing);

            double existingProgress = ParseProgressValue(existing);
            double incomingProgress = ParseProgressValue(fields);

            foreach (KeyValuePair<string, string> kvp in fields)
            {
                if (string.IsNullOrWhiteSpace(kvp.Value))
                    continue;

                // Em blocos .ldb do LevelDB, snapshots antigos (paused / progress menor) podem aparecer após o registro completo:
                // nunca permite que um snapshot antigo sobrescreva um download que já concluiu (progress = 1 / status = complete).
                if (alreadyCompleted && !incomingCompleted &&
                    (string.Equals(kvp.Key, "status", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(kvp.Key, "progress", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(kvp.Key, "extracting", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(kvp.Key, "queued", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (string.Equals(kvp.Key, "progress", StringComparison.OrdinalIgnoreCase) &&
                    incomingProgress >= 0.0 && existingProgress > incomingProgress)
                {
                    continue;
                }

                existing[kvp.Key] = kvp.Value;
            }

            if (incomingCompleted || alreadyCompleted)
            {
                existing["__completedOnce"] = "true";
            }
        }

        private static double ParseProgressValue(Dictionary<string, string> dlFields)
        {
            string rawProgress = GetFirstNonEmpty(dlFields, "progress");
            double val;
            if (!string.IsNullOrEmpty(rawProgress) &&
                double.TryParse(rawProgress, NumberStyles.Any, CultureInfo.InvariantCulture, out val))
            {
                return val;
            }
            return -1.0;
        }

        public static bool IsDownloadCompleted(Dictionary<string, string> dlFields)
        {
            if (dlFields == null || dlFields.Count == 0)
                return true;

            if (dlFields.ContainsKey("__completedOnce"))
                return true;

            string status = (GetFirstNonEmpty(dlFields, "status") ?? string.Empty).ToLowerInvariant();
            if (status == "removed")
                return false;

            string extracting = (GetFirstNonEmpty(dlFields, "extracting") ?? string.Empty).ToLowerInvariant();
            string installerRunning = (GetFirstNonEmpty(dlFields, "installerRunning") ?? string.Empty).ToLowerInvariant();
            if (extracting == "true" || installerRunning == "true")
                return false;

            if (status == "active" || status == "paused" || status == "waiting" || status == "extracting" || status == "error")
                return false;

            double progress = -1.0;
            string rawProgress = GetFirstNonEmpty(dlFields, "progress");
            if (!string.IsNullOrEmpty(rawProgress))
            {
                if (!double.TryParse(rawProgress, NumberStyles.Any, CultureInfo.InvariantCulture, out progress))
                {
                    progress = -1.0;
                }
            }

            if (status == "complete" || status == "seeding")
                return true;

            if (progress >= 0.0)
                return progress >= 0.9999;

            return true;
        }

        public static void ClassifyDownloads(
            Dictionary<string, Dictionary<string, string>> downloadRecords,
            out Dictionary<string, string> completedDownloadFolders,
            out HashSet<string> incompleteDownloads)
        {
            completedDownloadFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            incompleteDownloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (downloadRecords == null)
                return;

            foreach (KeyValuePair<string, Dictionary<string, string>> entry in downloadRecords)
            {
                string key = entry.Key;
                Dictionary<string, string> dl = entry.Value;
                string status = (GetFirstNonEmpty(dl, "status") ?? string.Empty).ToLowerInvariant();
                if (status == "removed")
                    continue;

                if (!IsDownloadCompleted(dl))
                {
                    incompleteDownloads.Add(key);
                    continue;
                }

                string downloadPath = GetFirstNonEmpty(dl, "downloadPath");
                string folderName = GetFirstNonEmpty(dl, "folderName");
                if (!string.IsNullOrEmpty(downloadPath) && !string.IsNullOrEmpty(folderName))
                {
                    try
                    {
                        string combinedPath = Path.Combine(downloadPath, folderName);
                        string resolvedPath = ResolveExistingPathAcrossDrives(combinedPath);
                        if (!string.IsNullOrEmpty(resolvedPath))
                        {
                            completedDownloadFolders[key] = resolvedPath;
                        }
                    }
                    catch { }
                }
            }
        }

        private static bool IsGameOrAssetRecord(Dictionary<string, string> fields)
        {
            if (!fields.ContainsKey("title") || string.IsNullOrWhiteSpace(fields["title"]))
                return false;

            if (IsDownloadRecord(fields))
                return false;

            return true;
        }

        private static bool HasLibraryGameMarker(Dictionary<string, string> fields)
        {
            return fields.ContainsKey("playTimeInMilliseconds") ||
                   fields.ContainsKey("isDeleted") ||
                   fields.ContainsKey("executablePath") ||
                   fields.ContainsKey("lastTimePlayed") ||
                   fields.ContainsKey("discs") ||
                   fields.ContainsKey("recompilationSlug") ||
                   fields.ContainsKey("customIconUrl") ||
                   fields.ContainsKey("customOriginalIconPath") ||
                   fields.ContainsKey("customCoverImageUrl") ||
                   fields.ContainsKey("customHeroImageUrl");
        }

        public static void MergeRecordFields(
            Dictionary<string, Dictionary<string, string>> mergedRecords,
            string uniqueKey,
            Dictionary<string, string> incomingFields)
        {
            bool isFullGameRecord = HasLibraryGameMarker(incomingFields);

            Dictionary<string, string> target;
            if (!mergedRecords.TryGetValue(uniqueKey, out target))
            {
                target = new Dictionary<string, string>(incomingFields, StringComparer.OrdinalIgnoreCase);
                if (isFullGameRecord)
                {
                    target["__isLibraryGame"] = "true";
                }
                mergedRecords[uniqueKey] = target;
                return;
            }

            if (isFullGameRecord)
            {
                target["__isLibraryGame"] = "true";
            }

            foreach (KeyValuePair<string, string> prop in incomingFields)
            {
                if (string.IsNullOrWhiteSpace(prop.Value))
                    continue;

                if (isFullGameRecord || !target.ContainsKey(prop.Key) || string.IsNullOrWhiteSpace(target[prop.Key]))
                {
                    target[prop.Key] = prop.Value;
                }
            }
        }

        public static List<HydraGameRecord> BuildGameRecords(
            Dictionary<string, Dictionary<string, string>> mergedRecords,
            Dictionary<string, string> completedDownloadFolders,
            HashSet<string> incompleteDownloads = null)
        {
            List<HydraGameRecord> records = new List<HydraGameRecord>();

            foreach (KeyValuePair<string, Dictionary<string, string>> entry in mergedRecords)
            {
                string uniqueKey = entry.Key;
                Dictionary<string, string> fields = entry.Value;

                string title = GetFirstNonEmpty(fields, "title");
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                bool isDownloadInProgress = incompleteDownloads != null && incompleteDownloads.Contains(uniqueKey);

                // Se o próprio registro !games! do Hydra já possui executablePath gravado pelo Hydra (ex: jogo já iniciado no Hydra),
                // confia diretamente nele mesmo que exista algum histórico antigo na tabela de downloads.
                string rawExePath = GetFirstNonEmpty(fields, "executablePath");
                string exePath = ResolveExecutableOrDirectoryTarget(rawExePath, title);
                if (!string.IsNullOrEmpty(exePath))
                {
                    isDownloadInProgress = false;
                }

                string recompExePath;
                string recompInstallDir;
                string recompIconUrl;
                bool hasRecomp = TryResolveRecompilationInfo(fields, title, out recompExePath, out recompInstallDir, out recompIconUrl);
                if (hasRecomp)
                {
                    fields["__hasRecompilation"] = "true";
                    if (string.IsNullOrEmpty(exePath) && !isDownloadInProgress && !string.IsNullOrEmpty(recompExePath))
                    {
                        exePath = recompExePath;
                    }
                    if (!string.IsNullOrEmpty(recompIconUrl) && string.IsNullOrEmpty(GetFirstNonEmpty(fields, "iconUrl")))
                    {
                        fields["iconUrl"] = recompIconUrl;
                    }
                }

                string downloadPath = null;
                bool hasCompletedDownloadFolder = !isDownloadInProgress &&
                                                  completedDownloadFolders != null &&
                                                  completedDownloadFolders.TryGetValue(uniqueKey, out downloadPath);

                if (string.IsNullOrEmpty(exePath) && hasCompletedDownloadFolder)
                {
                    exePath = ResolveExecutableOrDirectoryTarget(downloadPath, title);
                }

                bool isUserLibraryGame = fields.ContainsKey("__isLibraryGame") || hasCompletedDownloadFolder || !string.IsNullOrEmpty(exePath);
                if (!isUserLibraryGame)
                    continue;

                bool exeExists = !isDownloadInProgress && !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath);
                string resolvedExe = exeExists ? exePath : null;
                string resolvedInstallDir = exeExists
                    ? ResolveRootInstallDirectoryFromExe(exePath)
                    : (!string.IsNullOrEmpty(recompInstallDir) && Directory.Exists(recompInstallDir)
                        ? recompInstallDir
                        : (hasCompletedDownloadFolder && Directory.Exists(downloadPath) ? downloadPath : ResolveDirectoryFromRawExe(rawExePath)));

                HydraGameRecord record = HydraGameRecord.FromMergedDictionary(uniqueKey, fields, resolvedExe, resolvedInstallDir);
                records.Add(record);
            }

            return FilterOutCustomDuplicates(records);
        }

        private static string ResolveExecutableOrDirectoryTarget(string pathCandidate, string title)
        {
            if (string.IsNullOrWhiteSpace(pathCandidate))
                return null;

            string resolvedFile = ResolveExistingFileAcrossDrives(pathCandidate);
            if (!string.IsNullOrEmpty(resolvedFile) && resolvedFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return resolvedFile;

            string resolvedDir = ResolveExistingPathAcrossDrives(pathCandidate);
            if (!string.IsNullOrEmpty(resolvedDir) && Directory.Exists(resolvedDir))
            {
                return FindBestExecutableInFolder(resolvedDir, title);
            }

            return null;
        }

        public static bool TryResolveRecompilationInfo(
            Dictionary<string, string> fields,
            string title,
            out string recompExePath,
            out string recompInstallDir,
            out string recompIconUrl)
        {
            recompExePath = null;
            recompInstallDir = null;
            recompIconUrl = null;

            if (fields == null)
                return false;

            bool detectedRecomp = HydraGameRecord.HasRecompilationMarker(fields);
            string discsJson = GetFirstNonEmpty(fields, "discs");
            if (!string.IsNullOrEmpty(discsJson))
            {
                List<Dictionary<string, string>> discs = HydraJsonParser.ParseJsonArrayOfObjects(discsJson);
                foreach (Dictionary<string, string> disc in discs)
                {
                    string slug = GetFirstNonEmpty(disc, "recompilationSlug");
                    string isExe = (GetFirstNonEmpty(disc, "isExecutable") ?? string.Empty).ToLowerInvariant();
                    string discPath = GetFirstNonEmpty(disc, "path");

                    bool isRecompDisc = !string.IsNullOrEmpty(slug) ||
                                        isExe == "true" ||
                                        (!string.IsNullOrEmpty(discPath) && discPath.IndexOf("recompilations", StringComparison.OrdinalIgnoreCase) >= 0);

                    if (!isRecompDisc)
                        continue;

                    detectedRecomp = true;
                    string iconCandidate = GetFirstNonEmpty(disc, "iconUrl");
                    if (!string.IsNullOrEmpty(iconCandidate))
                    {
                        recompIconUrl = iconCandidate;
                    }

                    if (!string.IsNullOrEmpty(discPath))
                    {
                        string resolved = ResolveExistingPathAcrossDrives(discPath);
                        if (!string.IsNullOrEmpty(resolved))
                        {
                            if (File.Exists(resolved) && resolved.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                recompExePath = resolved;
                                recompInstallDir = Path.GetDirectoryName(resolved);
                                return true;
                            }
                            if (Directory.Exists(resolved))
                            {
                                recompInstallDir = resolved;
                                recompExePath = FindBestExecutableInFolder(resolved, title);
                                if (!string.IsNullOrEmpty(recompExePath))
                                    return true;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(slug))
                    {
                        string slugFolder = FindHydraRecompilationFolderBySlug(slug);
                        if (!string.IsNullOrEmpty(slugFolder))
                        {
                            recompInstallDir = slugFolder;
                            recompExePath = FindBestExecutableInFolder(slugFolder, title);
                            if (!string.IsNullOrEmpty(recompExePath))
                                return true;
                        }
                    }
                }
            }

            string selectedDisc = GetFirstNonEmpty(fields, "selectedDiscPath");
            if (detectedRecomp && !string.IsNullOrEmpty(selectedDisc))
            {
                string resolvedSelected = ResolveExistingPathAcrossDrives(selectedDisc);
                if (!string.IsNullOrEmpty(resolvedSelected))
                {
                    if (File.Exists(resolvedSelected) && resolvedSelected.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        recompExePath = resolvedSelected;
                        recompInstallDir = Path.GetDirectoryName(resolvedSelected);
                        return true;
                    }
                    if (Directory.Exists(resolvedSelected))
                    {
                        recompInstallDir = resolvedSelected;
                        recompExePath = FindBestExecutableInFolder(resolvedSelected, title);
                    }
                }
            }

            return detectedRecomp;
        }

        private static string FindHydraRecompilationFolderBySlug(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug))
                return null;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] candidates = new string[]
            {
                Path.Combine(appData, "hydra", "recompilations", slug),
                Path.Combine(appData, "Hydra", "recompilations", slug),
                Path.Combine(appData, "hydralauncher", "recompilations", slug)
            };

            foreach (string candidate in candidates)
            {
                string resolved = ResolveExistingPathAcrossDrives(candidate);
                if (!string.IsNullOrEmpty(resolved) && Directory.Exists(resolved))
                    return resolved;
            }

            return null;
        }

        private static string ResolveExistingFileAcrossDrives(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            if (File.Exists(filePath))
                return filePath;

            if (filePath.Length >= 3 && filePath[1] == ':' && (filePath[2] == '\\' || filePath[2] == '/'))
            {
                string pathWithoutDrive = filePath.Substring(2);
                try
                {
                    foreach (DriveInfo drive in DriveInfo.GetDrives())
                    {
                        if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                            continue;

                        string driveLetter = drive.Name.Substring(0, 2);
                        string candidate = driveLetter + pathWithoutDrive;
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
                catch { }
            }

            return null;
        }

        private static string ResolveExistingPathAcrossDrives(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            if (Directory.Exists(path) || File.Exists(path))
                return path;

            if (path.Length >= 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
            {
                string pathWithoutDrive = path.Substring(2);
                try
                {
                    foreach (DriveInfo drive in DriveInfo.GetDrives())
                    {
                        if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                            continue;

                        string driveLetter = drive.Name.Substring(0, 2);
                        string candidate = driveLetter + pathWithoutDrive;
                        if (Directory.Exists(candidate) || File.Exists(candidate))
                            return candidate;
                    }
                }
                catch { }
            }

            return null;
        }

        private static string ResolveDirectoryFromRawExe(string rawExePath)
        {
            if (string.IsNullOrWhiteSpace(rawExePath))
                return null;

            try
            {
                string dir = Path.GetDirectoryName(rawExePath);
                return ResolveExistingPathAcrossDrives(dir);
            }
            catch
            {
                return null;
            }
        }

        private static List<HydraGameRecord> FilterOutCustomDuplicates(List<HydraGameRecord> allRecords)
        {
            return allRecords
                .Where(rec => !IsCustomDuplicateOfCatalogGame(rec, allRecords))
                .ToList();
        }

        private static bool IsCustomDuplicateOfCatalogGame(HydraGameRecord record, List<HydraGameRecord> allRecords)
        {
            if (!string.Equals(record.Shop, "custom", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(record.ExecutablePath))
            {
                return false;
            }

            return allRecords.Any(other =>
                other != record &&
                !other.IsDeleted &&
                !string.Equals(other.Shop, "custom", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(other.ExecutablePath, record.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        }

        private static string ResolveRootInstallDirectoryFromExe(string executablePath)
        {
            try
            {
                string currentDir = Path.GetDirectoryName(executablePath);
                while (!string.IsNullOrEmpty(currentDir))
                {
                    string folderName = Path.GetFileName(currentDir);
                    if (string.IsNullOrEmpty(folderName) || !IsBinarySubfolderName(folderName))
                        break;

                    string parentDir = Path.GetDirectoryName(currentDir);
                    if (string.IsNullOrEmpty(parentDir) || parentDir == currentDir)
                        break;

                    currentDir = parentDir;
                }
                return currentDir;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsBinarySubfolderName(string folderName)
        {
            return BinarySubfolderNames.Any(sub => string.Equals(sub, folderName, StringComparison.OrdinalIgnoreCase));
        }

        public static string FindBestExecutableInFolder(string rootFolder, string gameTitle = null)
        {
            if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
                return null;

            try
            {
                string normalizedRoot = rootFolder.TrimEnd('\\', '/');
                string cleanTitle = NormalizeAlphanumeric(gameTitle);
                string cleanFolderName = NormalizeAlphanumeric(Path.GetFileName(normalizedRoot));

                string bestExePath = null;
                double highestScore = double.NegativeInfinity;

                foreach (string exePath in Directory.GetFiles(rootFolder, "*.exe", SearchOption.AllDirectories))
                {
                    string exeNameWithoutExt = Path.GetFileNameWithoutExtension(exePath) ?? string.Empty;
                    string lowerExeName = exeNameWithoutExt.ToLowerInvariant();

                    if (IgnoredExecutableKeywords.Any(lowerExeName.Contains))
                        continue;

                    double score = CalculateExecutableScore(exePath, lowerExeName, normalizedRoot, cleanTitle, cleanFolderName);
                    if (score > highestScore)
                    {
                        highestScore = score;
                        bestExePath = exePath;
                    }
                }

                return bestExePath;
            }
            catch
            {
                return null;
            }
        }

        private static double CalculateExecutableScore(
            string exePath,
            string lowerExeName,
            string normalizedRoot,
            string cleanTitle,
            string cleanFolderName)
        {
            double score = 0;

            string exeDir = (Path.GetDirectoryName(exePath) ?? string.Empty).TrimEnd('\\', '/');
            int folderDepth = CountFolderDepth(normalizedRoot, exeDir);

            score += (folderDepth == 0)
                ? RootDirectoryBonusScore
                : -(folderDepth * SubfolderDepthPenaltyPerLevel);

            string cleanExeName = NormalizeAlphanumeric(lowerExeName);
            if (!string.IsNullOrEmpty(cleanExeName))
            {
                if (IsFuzzyNameMatch(cleanExeName, cleanTitle))
                    score += TitleMatchBonusScore;

                if (IsFuzzyNameMatch(cleanExeName, cleanFolderName))
                    score += FolderNameMatchBonusScore;
            }

            if (lowerExeName.Contains("shipping") && folderDepth > 0)
                score -= UnrealShippingSubfolderPenalty;

            if (SecondaryUtilityKeywords.Any(lowerExeName.Contains))
                score -= AuxiliaryToolPenalty;

            try
            {
                long sizeInMegabytes = new FileInfo(exePath).Length / (1024 * 1024);
                score += Math.Min(MaxFileSizeTieBreakerScore, sizeInMegabytes * 0.25);
            }
            catch { }

            return score;
        }

        private static bool IsFuzzyNameMatch(string firstClean, string secondClean)
        {
            return !string.IsNullOrEmpty(firstClean) &&
                   !string.IsNullOrEmpty(secondClean) &&
                   (firstClean.Contains(secondClean) || secondClean.Contains(firstClean));
        }

        private static int CountFolderDepth(string normalizedRoot, string normalizedTargetDir)
        {
            if (string.Equals(normalizedRoot, normalizedTargetDir, StringComparison.OrdinalIgnoreCase))
                return 0;

            if (normalizedTargetDir.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                string relativePath = normalizedTargetDir.Substring(normalizedRoot.Length).Trim('\\', '/');
                if (string.IsNullOrEmpty(relativePath))
                    return 0;

                return relativePath.Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length;
            }
            return 1;
        }

        private static string NormalizeAlphanumeric(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;
            return Regex.Replace(input.ToLowerInvariant(), @"[^a-z0-9]", string.Empty);
        }

        private static string GetFirstNonEmpty(Dictionary<string, string> fields, params string[] keys)
        {
            if (fields == null || keys == null)
                return null;

            foreach (string key in keys)
            {
                string val;
                if (fields.TryGetValue(key, out val) && !string.IsNullOrWhiteSpace(val))
                    return val.Trim();
            }
            return null;
        }
    }
}
