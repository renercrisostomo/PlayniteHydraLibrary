using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HydraPlaynite.Client;
using HydraPlaynite.Models;
using HydraPlaynite.Settings;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace HydraPlaynite.Services
{
    /// <summary>
    /// Sincroniza jogos já presentes no banco do Playnite com os dados mais recentes do Hydra Launcher.
    /// </summary>
    public class HydraDatabaseSynchronizer
    {
        private readonly IPlayniteAPI api;
        private readonly Guid pluginId;
        private readonly HydraRepository repository;
        private readonly HydraLocalizer localizer;
        private readonly ILogger logger;

        public HydraDatabaseSynchronizer(
            IPlayniteAPI api,
            Guid pluginId,
            HydraRepository repository,
            HydraLocalizer localizer,
            ILogger logger)
        {
            this.api = api;
            this.pluginId = pluginId;
            this.repository = repository;
            this.localizer = localizer;
            this.logger = logger;
        }

        public int SynchronizeExistingGames(
            HydraLibrarySettings settings,
            bool forceUpdateArtwork = false,
            List<HydraGameRecord> preloadedRecords = null)
        {
            int updatedCount = 0;

            try
            {
                List<HydraGameRecord> records = preloadedRecords ?? repository.LoadAllGames();
                Dictionary<string, HydraGameRecord> activeHydraRecordsByKey = IndexActiveRecordsByKey(records);

                bool syncPlaytime = settings.AutoSyncPlaytimeOnStartup || settings.UpdateExistingGamesOnRefresh;
                bool syncArtwork = forceUpdateArtwork || settings.UpdateExistingGamesOnRefresh;

                using (api.Database.BufferedUpdate())
                {
                    foreach (Game game in api.Database.Games)
                    {
                        if (game == null || game.PluginId != pluginId || string.IsNullOrEmpty(game.GameId))
                            continue;

                        HydraGameRecord record;
                        if (!activeHydraRecordsByKey.TryGetValue(game.GameId, out record))
                        {
                            bool removedChanged = false;
                            removedChanged |= SyncInstallationStateForRemovedHydraGame(game, settings.KeepInstalledIfFilesExistOnDisk);
                            removedChanged |= MigrateLegacyPluginActions(game);

                            if (removedChanged)
                            {
                                api.Database.Games.Update(game);
                                updatedCount++;
                            }
                            continue;
                        }

                        bool changed = false;

                        changed |= SyncInstallationState(game, record, settings.KeepInstalledIfFilesExistOnDisk);
                        changed |= SyncSource(game);
                        changed |= MigrateLegacyPluginActions(game);

                        if (syncPlaytime)
                        {
                            changed |= SyncPlaytimeAndInstallSize(game, record);
                        }

                        if (syncArtwork)
                        {
                            changed |= SyncGameArtwork(game, record, forceUpdateArtwork);
                        }

                        if (changed)
                        {
                            api.Database.Games.Update(game);
                            updatedCount++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (logger != null)
                {
                    logger.Error(ex, "HydraDatabaseSynchronizer: Failed to synchronize existing games.");
                }
            }

            return updatedCount;
        }

        public static Dictionary<string, HydraGameRecord> IndexActiveRecordsByKey(List<HydraGameRecord> records)
        {
            Dictionary<string, HydraGameRecord> byKey = new Dictionary<string, HydraGameRecord>(StringComparer.OrdinalIgnoreCase);
            if (records == null) return byKey;

            foreach (HydraGameRecord record in records)
            {
                if (record == null || record.IsDeleted || string.IsNullOrEmpty(record.UniqueKey))
                    continue;

                if (!HydraOriginDetector.ShouldImport(record))
                    continue;

                byKey[record.UniqueKey] = record;
            }

            return byKey;
        }

        /// <summary>
        /// Sincroniza o estado de instalação e limpa flags de desinstalação interrompidas caso não haja watcher ativo.
        /// </summary>
        public static bool SyncInstallationState(Game game, HydraGameRecord record, bool keepInstalledIfFilesExistOnDisk = false)
        {
            bool changed = false;
            bool isInstalledInHydra = !string.IsNullOrWhiteSpace(record.ExecutablePath) && File.Exists(record.ExecutablePath);
            bool effectiveInstalled = isInstalledInHydra || (keepInstalledIfFilesExistOnDisk && HasExecutablePresentOnDisk(game));

            if (game.IsUninstalling && !HydraUninstallController.IsUninstallActive(game.Id))
            {
                game.IsUninstalling = false;
                changed = true;
            }

            if (game.IsInstalled != effectiveInstalled)
            {
                game.IsInstalled = effectiveInstalled;
                changed = true;
            }

            if (isInstalledInHydra &&
                !string.IsNullOrWhiteSpace(record.InstallDirectory) &&
                !string.Equals(game.InstallDirectory, record.InstallDirectory, StringComparison.OrdinalIgnoreCase))
            {
                game.InstallDirectory = record.InstallDirectory;
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// Atualiza o estado de instalação de jogos removidos da biblioteca do Hydra Launcher.
        /// Por padrão marca como não instalado; se keepInstalledIfFilesExistOnDisk estiver ativo,
        /// mantém como instalado enquanto o executável existir no disco sem sincronizar metadados do Hydra.
        /// </summary>
        public static bool SyncInstallationStateForRemovedHydraGame(Game game, bool keepInstalledIfFilesExistOnDisk = false)
        {
            bool changed = false;

            if (game.IsUninstalling && !HydraUninstallController.IsUninstallActive(game.Id))
            {
                game.IsUninstalling = false;
                changed = true;
            }

            if (!game.IsInstalled)
                return changed;

            if (keepInstalledIfFilesExistOnDisk && HasExecutablePresentOnDisk(game))
            {
                return changed;
            }

            game.IsInstalled = false;
            return true;
        }

        private static bool HasExecutablePresentOnDisk(Game game)
        {
            if (game == null)
                return false;

            if (game.GameActions != null)
            {
                foreach (GameAction action in game.GameActions)
                {
                    if (action != null && action.Type == GameActionType.File && !string.IsNullOrWhiteSpace(action.Path))
                    {
                        if (File.Exists(action.Path))
                            return true;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(game.InstallDirectory) && Directory.Exists(game.InstallDirectory))
            {
                string remainingExe = HydraRepository.FindBestExecutableInFolder(game.InstallDirectory, game.Name);
                if (!string.IsNullOrEmpty(remainingExe))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Migra jogos importados em versões anteriores para usar GetPlayActions dinâmico (IncludeLibraryPluginAction = true),
        /// removendo apenas ações padrão geradas pelo plugin e preservando ações personalizadas do usuário.
        /// </summary>
        public static bool MigrateLegacyPluginActions(Game game)
        {
            if (game == null)
                return false;

            bool changed = false;

            if (!game.IncludeLibraryPluginAction)
            {
                game.IncludeLibraryPluginAction = true;
                changed = true;
            }

            if (game.GameActions != null && game.GameActions.Count > 0)
            {
                List<GameAction> toRemove = new List<GameAction>();
                foreach (GameAction action in game.GameActions)
                {
                    if (HydraPlayActionFactory.IsLegacyPluginGeneratedAction(action))
                    {
                        toRemove.Add(action);
                    }
                }

                foreach (GameAction legacy in toRemove)
                {
                    game.GameActions.Remove(legacy);
                    changed = true;
                }
            }

            return changed;
        }

        public static bool SyncPlaytimeAndInstallSize(Game game, HydraGameRecord record)
        {
            bool changed = false;
            ulong hydraPlaytimeSeconds = record.GetPlaytimeInSeconds();

            if (hydraPlaytimeSeconds > game.Playtime)
            {
                game.Playtime = hydraPlaytimeSeconds;
                changed = true;
            }

            if (record.LastTimePlayed != null && (game.LastActivity == null || record.LastTimePlayed > game.LastActivity))
            {
                game.LastActivity = record.LastTimePlayed;
                changed = true;
            }

            if (record.InstalledSizeInBytes != null &&
                record.InstalledSizeInBytes > 0 &&
                game.InstallSize != record.InstalledSizeInBytes)
            {
                game.InstallSize = record.InstalledSizeInBytes;
                changed = true;
            }

            return changed;
        }

        private bool SyncSource(Game game)
        {
            GameSource targetSource = GetOrCreateSource(HydraOriginDetector.DefaultPlayniteSourceName);
            if (targetSource != null && game.SourceId != targetSource.Id)
            {
                game.SourceId = targetSource.Id;
                return true;
            }

            return false;
        }

        private bool SyncGameArtwork(Game game, HydraGameRecord record, bool overwriteWithCustomMedia)
        {
            bool changed = false;

            if (string.IsNullOrEmpty(game.Icon) || (overwriteWithCustomMedia && record.HasCustomIcon))
            {
                string iconCandidate = HydraAssetResolver.ResolveIconCandidate(record);
                string newIconId;
                if (TryReplaceDatabaseMediaFile(game.Id, game.Icon, iconCandidate, out newIconId))
                {
                    game.Icon = newIconId;
                    changed = true;
                }
            }

            if (string.IsNullOrEmpty(game.CoverImage) || (overwriteWithCustomMedia && record.HasCustomCover))
            {
                string coverCandidate = HydraAssetResolver.ResolveCoverCandidate(record);
                string newCoverId;
                if (TryReplaceDatabaseMediaFile(game.Id, game.CoverImage, coverCandidate, out newCoverId))
                {
                    game.CoverImage = newCoverId;
                    changed = true;
                }
            }

            if (string.IsNullOrEmpty(game.BackgroundImage) || (overwriteWithCustomMedia && record.HasCustomHero))
            {
                string heroCandidate = HydraAssetResolver.ResolveHeroCandidate(record);
                if (!string.IsNullOrEmpty(heroCandidate))
                {
                    if (File.Exists(heroCandidate))
                    {
                        string newHeroId;
                        if (TryReplaceDatabaseMediaFile(game.Id, null, heroCandidate, out newHeroId))
                        {
                            game.BackgroundImage = newHeroId;
                            changed = true;
                        }
                    }
                    else
                    {
                        game.BackgroundImage = heroCandidate;
                        changed = true;
                    }
                }
            }

            return changed;
        }

        private bool TryReplaceDatabaseMediaFile(Guid gameId, string currentFileId, string candidatePathOrUrl, out string newFileId)
        {
            newFileId = null;
            if (string.IsNullOrEmpty(candidatePathOrUrl)) return false;

            newFileId = HydraAssetResolver.ImportFileIntoPlayniteDb(api, candidatePathOrUrl, gameId);
            if (string.IsNullOrEmpty(newFileId)) return false;

            if (!string.IsNullOrEmpty(currentFileId))
            {
                try { api.Database.RemoveFile(currentFileId); } catch { }
            }

            return true;
        }

        private GameSource GetOrCreateSource(string sourceName)
        {
            string normalizedName = string.IsNullOrWhiteSpace(sourceName) ? "Hydra" : sourceName;
            GameSource existing = api.Database.Sources.FirstOrDefault(
                s => string.Equals(s.Name, normalizedName, StringComparison.OrdinalIgnoreCase));

            return existing ?? api.Database.Sources.Add(normalizedName);
        }
    }
}
