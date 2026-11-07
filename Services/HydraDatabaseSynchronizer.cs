using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HydraPlaynite.Models;
using HydraPlaynite.Settings;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace HydraPlaynite.Services
{
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
            bool forceUpdateActions,
            bool forceUpdateArtwork,
            List<HydraGameRecord> preloadedRecords = null)
        {
            int updatedCount = 0;
            try
            {
                List<HydraGameRecord> records = preloadedRecords ?? repository.LoadAllGames();
                Dictionary<string, HydraGameRecord> activeHydraRecordsByKey = IndexActiveRecordsByKey(records);

                bool syncPlaytime = settings.AutoSyncPlaytimeOnStartup || forceUpdateActions || settings.UpdateExistingGamesOnRefresh;
                bool syncActions = forceUpdateActions || settings.UpdateExistingGamesOnRefresh;
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
                            // O jogo foi removido do Hydra (isDeleted = true ou não existe mais no banco do Hydra):
                            // Ignora sincronização de dados do Hydra com segurança e apenas valida se os arquivos locais ainda existem.
                            if (SyncInstallationStateForRemovedHydraGame(game))
                            {
                                api.Database.Games.Update(game);
                                updatedCount++;
                            }
                            continue;
                        }

                        bool changed = false;

                        changed |= SyncInstallationState(game, record);

                        if (syncPlaytime)
                        {
                            changed |= SyncPlaytimeAndInstallSize(game, record);
                        }

                        if (syncActions)
                        {
                            changed |= SyncSourceAndPlayActions(game, record, settings);
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
                    logger.Error(ex, "HydraDatabaseSynchronizer: Erro ao sincronizar jogos existentes.");
                }
            }

            return updatedCount;
        }

        public static Dictionary<string, HydraGameRecord> IndexActiveRecordsByKey(List<HydraGameRecord> records)
        {
            Dictionary<string, HydraGameRecord> byKey = new Dictionary<string, HydraGameRecord>(StringComparer.OrdinalIgnoreCase);
            if (records == null)
                return byKey;

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

        private static bool SyncInstallationState(Game game, HydraGameRecord record)
        {
            bool changed = false;
            bool isInstalledOnDisk = !string.IsNullOrWhiteSpace(record.ExecutablePath) && File.Exists(record.ExecutablePath);

            if (game.IsInstalled != isInstalledOnDisk)
            {
                game.IsInstalled = isInstalledOnDisk;
                changed = true;
            }

            if (isInstalledOnDisk && !string.IsNullOrWhiteSpace(record.InstallDirectory) &&
                !string.Equals(game.InstallDirectory, record.InstallDirectory, StringComparison.OrdinalIgnoreCase))
            {
                game.InstallDirectory = record.InstallDirectory;
                changed = true;
            }

            return changed;
        }

        private static bool SyncInstallationStateForRemovedHydraGame(Game game)
        {
            if (!game.IsInstalled)
                return false;

            if (!string.IsNullOrWhiteSpace(game.InstallDirectory) && Directory.Exists(game.InstallDirectory))
                return false;

            game.IsInstalled = false;
            return true;
        }

        private static bool SyncPlaytimeAndInstallSize(Game game, HydraGameRecord record)
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

            if (record.InstalledSizeInBytes != null && game.InstallSize == null)
            {
                game.InstallSize = record.InstalledSizeInBytes;
                changed = true;
            }

            return changed;
        }

        private bool SyncSourceAndPlayActions(Game game, HydraGameRecord record, HydraLibrarySettings settings)
        {
            GameSource targetSource = GetOrCreateSource(HydraOriginDetector.DefaultPlayniteSourceName);
            if (targetSource != null && game.SourceId != targetSource.Id)
            {
                game.SourceId = targetSource.Id;
            }

            List<GameAction> actions = HydraPlayActionFactory.CreateActions(record, settings.LaunchViaHydraUri, localizer);
            game.GameActions = HydraPlayActionFactory.ToObservableCollection(actions);
            game.IncludeLibraryPluginAction = false;
            return true;
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
            if (string.IsNullOrEmpty(candidatePathOrUrl))
                return false;

            newFileId = HydraAssetResolver.ImportFileIntoPlayniteDb(api, candidatePathOrUrl, gameId);
            if (string.IsNullOrEmpty(newFileId))
                return false;

            if (!string.IsNullOrEmpty(currentFileId))
            {
                try { api.Database.RemoveFile(currentFileId); } catch { }
            }
            return true;
        }

        private GameSource GetOrCreateSource(string sourceName)
        {
            string normalizedName = string.IsNullOrWhiteSpace(sourceName) ? "Hydra" : sourceName;
            GameSource existing = api.Database.Sources.FirstOrDefault(s =>
                string.Equals(s.Name, normalizedName, StringComparison.OrdinalIgnoreCase));

            return existing ?? api.Database.Sources.Add(normalizedName);
        }
    }
}
