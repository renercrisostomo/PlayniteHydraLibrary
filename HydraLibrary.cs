using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Controls;
using HydraPlaynite.Client;
using HydraPlaynite.Models;
using HydraPlaynite.Services;
using HydraPlaynite.Settings;
using HydraPlaynite.Views;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace HydraPlaynite
{
    /// <summary>
    /// Plugin de integração de biblioteca do Hydra Launcher para o Playnite.
    /// </summary>
    public class HydraLibrary : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public static readonly Guid PluginGuid = new Guid("8f4e3a12-6c9b-4d73-9a21-5b8d7e3f1042");

        private readonly string pluginDir;
        private readonly HydraLocalizer localizer;
        private readonly HydraRepository repository;
        private readonly HydraDatabaseSynchronizer synchronizer;
        private readonly HydraLibrarySettings settings;

        public override Guid Id
        {
            get { return PluginGuid; }
        }

        public override string Name
        {
            get { return "Hydra"; }
        }

        public override string LibraryIcon
        {
            get { return Path.Combine(pluginDir, "hydra_library_icon.png"); }
        }

        public override LibraryClient Client
        {
            get { return new HydraClient(LibraryIcon); }
        }

        public HydraLocalizer Localizer
        {
            get { return localizer; }
        }

        public HydraLibrary(IPlayniteAPI api) : base(api)
        {
            pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            localizer = new HydraLocalizer(api, pluginDir, logger);
            repository = new HydraRepository(logger);
            synchronizer = new HydraDatabaseSynchronizer(api, PluginGuid, repository, localizer, logger);
            settings = new HydraLibrarySettings(this);

            Properties = new LibraryPluginProperties
            {
                HasSettings = true,
                CanShutdownClient = false
            };
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new HydraLibrarySettingsView(this, localizer);
        }

        public override IEnumerable<PlayController> GetPlayActions(GetPlayActionsArgs args)
        {
            if (args == null || args.Game == null || args.Game.PluginId != Id)
            {
                yield break;
            }

            HydraGameRecord activeRecord = FindActiveHydraRecord(args.Game.GameId);
            List<PlayController> controllers = HydraPlayActionFactory.CreatePlayControllers(
                args.Game,
                activeRecord,
                settings.LaunchViaHydraUri,
                localizer);

            foreach (PlayController controller in controllers)
            {
                yield return controller;
            }
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
            {
                yield break;
            }

            yield return new HydraUninstallController(args.Game, repository, new HydraClient(LibraryIcon), localizer, logger, PlayniteApi, keepInstalledIfFilesExistOnDisk: settings.KeepInstalledIfFilesExistOnDisk);
        }

        public int ApplySettingsOnSave(bool forceUpdateArtwork)
        {
            return synchronizer.SynchronizeExistingGames(settings, forceUpdateArtwork: forceUpdateArtwork);
        }

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            if (localizer != null)
            {
                localizer.Reload();
            }

            List<HydraGameRecord> allRecords = repository.LoadAllGames();

            synchronizer.SynchronizeExistingGames(
                settings,
                forceUpdateArtwork: settings.UpdateExistingGamesOnRefresh,
                preloadedRecords: allRecords);

            List<GameMetadata> importedGames = new List<GameMetadata>();
            foreach (HydraGameRecord record in allRecords)
            {
                bool isExecutableInstalled;
                if (ShouldImportRecord(record, out isExecutableInstalled))
                {
                    importedGames.Add(BuildGameMetadata(record, isExecutableInstalled));
                }
            }

            if (logger != null)
            {
                logger.Info(string.Format("PlayniteHydraLibrary v0.9.0: Read {0} Hydra records, imported {1} games into Playnite.", allRecords.Count, importedGames.Count));
            }

            return importedGames;
        }

        private HydraGameRecord FindActiveHydraRecord(string gameId)
        {
            if (string.IsNullOrWhiteSpace(gameId))
                return null;

            List<HydraGameRecord> allRecords = repository.LoadAllGames();
            if (allRecords == null)
                return null;

            foreach (HydraGameRecord record in allRecords)
            {
                if (record != null &&
                    !record.IsDeleted &&
                    string.Equals(record.UniqueKey, gameId, StringComparison.OrdinalIgnoreCase))
                {
                    return record;
                }
            }

            return null;
        }

        private bool ShouldImportRecord(HydraGameRecord record, out bool isExecutableInstalled)
        {
            isExecutableInstalled = false;

            if (record == null || record.IsDeleted)
                return false;

            isExecutableInstalled = !string.IsNullOrWhiteSpace(record.ExecutablePath) && File.Exists(record.ExecutablePath);
            if (isExecutableInstalled && !settings.ImportInstalledGames)
                return false;
            if (!isExecutableInstalled && !settings.ImportUninstalledGames)
                return false;

            return HydraOriginDetector.ShouldImport(record);
        }

        private GameMetadata BuildGameMetadata(HydraGameRecord record, bool isExecutableInstalled)
        {
            GameMetadata metadata = new GameMetadata
            {
                Source = new MetadataNameProperty(HydraOriginDetector.DefaultPlayniteSourceName),
                Name = record.Title,
                GameId = record.UniqueKey,
                InstallDirectory = record.InstallDirectory,
                IsInstalled = isExecutableInstalled,
                Playtime = record.GetPlaytimeInSeconds(),
                LastActivity = record.LastTimePlayed,
                InstallSize = record.InstalledSizeInBytes,
                Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("pc_windows") },
                Icon = ResolveMetadataIcon(record, isExecutableInstalled)
            };

            string coverCandidate = HydraAssetResolver.ResolveCoverCandidate(record);
            if (!string.IsNullOrEmpty(coverCandidate))
            {
                metadata.CoverImage = new MetadataFile(coverCandidate);
            }

            string heroCandidate = HydraAssetResolver.ResolveHeroCandidate(record);
            if (!string.IsNullOrEmpty(heroCandidate))
            {
                metadata.BackgroundImage = new MetadataFile(heroCandidate);
            }

            return metadata;
        }

        private MetadataFile ResolveMetadataIcon(HydraGameRecord record, bool isExecutableInstalled)
        {
            string iconCandidate = HydraAssetResolver.ResolveIconCandidate(record);
            if (!string.IsNullOrEmpty(iconCandidate))
                return new MetadataFile(iconCandidate);

            if (isExecutableInstalled)
                return new MetadataFile(record.ExecutablePath);

            if (File.Exists(LibraryIcon))
                return new MetadataFile(LibraryIcon);

            return null;
        }
    }
}
