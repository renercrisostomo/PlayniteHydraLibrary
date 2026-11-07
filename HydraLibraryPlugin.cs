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
    public class HydraLibraryPlugin : LibraryPlugin
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

        public HydraLibraryPlugin(IPlayniteAPI api) : base(api)
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

        public int ApplySettingsOnSave(bool forceUpdateActions, bool forceUpdateArtwork)
        {
            return synchronizer.SynchronizeExistingGames(settings, forceUpdateActions: forceUpdateActions, forceUpdateArtwork: forceUpdateArtwork);
        }

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            if (localizer != null)
            {
                localizer.Reload();
            }

            List<HydraGameRecord> allRecords = repository.LoadAllGames();

            if (settings.AutoSyncPlaytimeOnStartup || settings.UpdateExistingGamesOnRefresh)
            {
                synchronizer.SynchronizeExistingGames(
                    settings,
                    forceUpdateActions: settings.UpdateExistingGamesOnRefresh,
                    forceUpdateArtwork: settings.UpdateExistingGamesOnRefresh,
                    preloadedRecords: allRecords);
            }

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
                logger.Info(string.Format("PlayniteHydraLibrary v0.6.0: {0} registros lidos do Hydra, {1} jogos enviados ao Playnite.", allRecords.Count, importedGames.Count));
            }

            return importedGames;
        }

        private bool ShouldImportRecord(HydraGameRecord record, out bool isExecutableInstalled)
        {
            isExecutableInstalled = false;

            if (record == null || record.IsDeleted)
                return false;

            isExecutableInstalled = !string.IsNullOrWhiteSpace(record.ExecutablePath) && File.Exists(record.ExecutablePath);
            if (!isExecutableInstalled && !settings.ImportUninstalledGames)
                return false;

            return HydraOriginDetector.ShouldImport(record);
        }

        private GameMetadata BuildGameMetadata(HydraGameRecord record, bool isExecutableInstalled)
        {
            List<GameAction> actions = HydraPlayActionFactory.CreateActions(record, settings.LaunchViaHydraUri, localizer);

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
                GameActions = actions,
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
