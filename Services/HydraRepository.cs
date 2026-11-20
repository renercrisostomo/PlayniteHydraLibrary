using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HydraPlaynite.Models;
using Playnite.SDK;

namespace HydraPlaynite.Services
{
    /// <summary>
    /// Lê, mescla e consolida os registros de jogos e downloads armazenados no LevelDB do Hydra Launcher.
    /// </summary>
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

        /// <summary>
        /// Gera uma assinatura rápida baseada no tamanho e data de modificação dos arquivos do LevelDB
        /// para detectar alterações sem precisar reler e descomprimir os blocos.
        /// </summary>
        public string GetDatabaseStateSignature(IEnumerable<string> databaseDirectories = null)
        {
            StringBuilder sb = new StringBuilder();
            IEnumerable<string> dirs = databaseDirectories ?? GetCandidateDatabaseDirectories();

            foreach (string dbPath in dirs)
            {
                if (string.IsNullOrEmpty(dbPath) || !Directory.Exists(dbPath))
                    continue;

                try
                {
                    foreach (FileInfo file in GetOrderedLevelDbFiles(dbPath))
                    {
                        long liveLength = GetLiveFileLength(file);
                        sb.Append(file.Name)
                          .Append(':')
                          .Append(liveLength)
                          .Append(':')
                          .Append(file.LastWriteTimeUtc.Ticks)
                          .Append(';');
                    }
                }
                catch { }
            }

            return sb.ToString();
        }

        private static long GetLiveFileLength(FileInfo file)
        {
            if (file == null)
                return 0L;

            if (file.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using (FileStream fs = new FileStream(
                        file.FullName,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    {
                        return fs.Length;
                    }
                }
                catch
                {
                }
            }

            return file.Length;
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
                Path.Combine(appData, "hydra", "data.db"),
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
                bool isWriteAheadLog = string.Equals(Path.GetExtension(filePath), ".log", StringComparison.OrdinalIgnoreCase);
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

                    MergeRecordFields(mergedRecords, uniqueKey, fields, allowExplicitNullClear: true);
                }
            }
            catch (Exception ex)
            {
                if (logger != null)
                {
                    logger.Debug(ex, "HydraRepository: Failed to process file " + filePath);
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

        /// <summary>
        /// Mescla registros da tabela de downloads impedindo que snapshots antigos de blocos SSTable
        /// sobrescrevam um download já concluído, exceto quando o status passa a ser "removed".
        /// </summary>
        public static void UpdateDownloadRecord(
            Dictionary<string, string> fields,
            string shop,
            string objectId,
            Dictionary<string, Dictionary<string, string>> downloadRecords)
        {
            if (string.IsNullOrEmpty(objectId) || fields == null)
                return;

            string key = shop + ":" + objectId;
            string incomingStatus = (GetFirstNonEmpty(fields, "status") ?? string.Empty).ToLowerInvariant();
            bool incomingRemoved = incomingStatus == "removed";

            Dictionary<string, string> existing;
            if (!downloadRecords.TryGetValue(key, out existing))
            {
                existing = new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase);
                if (!incomingRemoved && IsDownloadCompleted(existing))
                {
                    existing["__completedOnce"] = "true";
                }
                downloadRecords[key] = existing;
                return;
            }

            if (incomingRemoved)
            {
                existing["status"] = "removed";
                existing.Remove("__completedOnce");
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

            string status = (GetFirstNonEmpty(dlFields, "status") ?? string.Empty).ToLowerInvariant();
            if (status == "removed")
                return false;

            if (dlFields.ContainsKey("__completedOnce"))
                return true;

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

        /// <summary>
        /// Mescla campos de registros do LevelDB garantindo que registros de cache (!gameShopAssets!)
        /// não sobrescrevam dados de registros completos de biblioteca (!games!).
        /// </summary>
        public static void MergeRecordFields(
            Dictionary<string, Dictionary<string, string>> mergedRecords,
            string uniqueKey,
            Dictionary<string, string> incomingFields,
            bool allowExplicitNullClear = false)
        {
            bool isFullGameRecord = HasLibraryGameMarker(incomingFields);

            Dictionary<string, string> target;
            if (!mergedRecords.TryGetValue(uniqueKey, out target))
            {
                target = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, string> kvp in incomingFields)
                {
                    if (string.Equals(kvp.Value, HydraJsonParser.ExplicitNullMarker, StringComparison.Ordinal))
                    {
                        if (isFullGameRecord && allowExplicitNullClear &&
                            string.Equals(kvp.Key, "executablePath", StringComparison.OrdinalIgnoreCase))
                        {
                            target["__explicitNullExe"] = "true";
                        }
                        continue;
                    }

                    target[kvp.Key] = kvp.Value;
                    if (string.Equals(kvp.Key, "executablePath", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(kvp.Value))
                    {
                        target.Remove("__explicitNullExe");
                    }
                }
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

            bool isFullGameSnapshot = incomingFields.ContainsKey("playTimeInMilliseconds") ||
                                      incomingFields.ContainsKey("isDeleted");
            if (isFullGameSnapshot && allowExplicitNullClear && !incomingFields.ContainsKey("executablePath"))
            {
                target.Remove("executablePath");
                target["__explicitNullExe"] = "true";
            }

            foreach (KeyValuePair<string, string> prop in incomingFields)
            {
                if (string.Equals(prop.Value, HydraJsonParser.ExplicitNullMarker, StringComparison.Ordinal) ||
                    (string.IsNullOrWhiteSpace(prop.Value) && string.Equals(prop.Key, "executablePath", StringComparison.OrdinalIgnoreCase)))
                {
                    if (isFullGameRecord && allowExplicitNullClear &&
                        string.Equals(prop.Key, "executablePath", StringComparison.OrdinalIgnoreCase))
                    {
                        target.Remove("executablePath");
                        target["__explicitNullExe"] = "true";
                    }
                    continue;
                }

                if (string.IsNullOrWhiteSpace(prop.Value))
                    continue;

                if (isFullGameRecord || !target.ContainsKey(prop.Key) || string.IsNullOrWhiteSpace(target[prop.Key]))
                {
                    target[prop.Key] = prop.Value;
                    if (string.Equals(prop.Key, "executablePath", StringComparison.OrdinalIgnoreCase))
                    {
                        target.Remove("__explicitNullExe");
                    }
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

                bool wasExecutableExplicitlyCleared = fields.ContainsKey("__explicitNullExe");
                if (string.IsNullOrEmpty(exePath) && hasCompletedDownloadFolder && !wasExecutableExplicitlyCleared)
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

                ulong? resolvedInstallSize = ResolveInstalledSizeInBytes(fields, hasRecomp);
                HydraGameRecord record = HydraGameRecord.FromMergedDictionary(uniqueKey, fields, resolvedExe, resolvedInstallDir, resolvedInstallSize);
                records.Add(record);
            }

            return FilterOutCustomDuplicates(records);
        }

        /// <summary>
        /// Obtém o tamanho instalado a partir dos metadados registrados pelo Hydra no LevelDB.
        /// Para jogos com recompilação, soma o tamanho da ROM (romSizeBytes/discs) ao tamanho do pacote Recomp (sizeInBytes).
        /// </summary>
        public static ulong? ResolveInstalledSizeInBytes(
            Dictionary<string, string> fields,
            bool hasRecompilation)
        {
            if (fields == null)
                return null;

            ulong jsonInstalledSize = HydraGameRecord.ParseUlongField(fields, "installedSizeInBytes") ?? 0UL;
            ulong jsonSizeInBytes = HydraGameRecord.ParseUlongField(fields, "sizeInBytes") ?? 0UL;
            ulong jsonRomSizeBytes = HydraGameRecord.ParseUlongField(fields, "romSizeBytes") ?? 0UL;

            ulong baseJsonSize = Math.Max(jsonInstalledSize, jsonSizeInBytes);

            if (!hasRecompilation && jsonRomSizeBytes == 0UL && !fields.ContainsKey("discs"))
            {
                return baseJsonSize > 0UL ? (ulong?)baseJsonSize : null;
            }

            ulong discsRomBytes = 0UL;
            ulong discsRecompBytes = 0UL;

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

                    ulong discJsonSize = HydraGameRecord.ParseUlongField(disc, "sizeBytes")
                                         ?? HydraGameRecord.ParseUlongField(disc, "sizeInBytes")
                                         ?? HydraGameRecord.ParseUlongField(disc, "fileSize")
                                         ?? HydraGameRecord.ParseUlongField(disc, "romSizeBytes")
                                         ?? 0UL;

                    if (isRecompDisc)
                    {
                        discsRecompBytes += discJsonSize;
                    }
                    else
                    {
                        discsRomBytes += discJsonSize;
                    }
                }
            }

            ulong effectiveRomComponent = Math.Max(jsonRomSizeBytes, discsRomBytes);
            ulong effectiveRecompComponent = discsRecompBytes;

            if (baseJsonSize > 0UL && baseJsonSize < effectiveRomComponent && baseJsonSize > effectiveRecompComponent)
            {
                effectiveRecompComponent = baseJsonSize;
            }

            ulong combinedRecompTotal = effectiveRomComponent + effectiveRecompComponent;
            ulong bestTotal = Math.Max(baseJsonSize, combinedRecompTotal);

            return bestTotal > 0UL ? (ulong?)bestTotal : null;
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
                                recompInstallDir = ResolveRootInstallDirectoryFromExe(resolved);
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
                        recompInstallDir = ResolveRootInstallDirectoryFromExe(resolvedSelected);
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

        /// <summary>
        /// Seleciona o executável principal do jogo na pasta de instalação através de pontuação heurística,
        /// priorizando a raiz, correspondência com o título e descartando desinstaladores/utilitários.
        /// </summary>
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
                if (fields.TryGetValue(key, out val) && !string.IsNullOrWhiteSpace(val) &&
                    !string.Equals(val, HydraJsonParser.ExplicitNullMarker, StringComparison.Ordinal))
                {
                    return val.Trim();
                }
            }
            return null;
        }
    }
}
