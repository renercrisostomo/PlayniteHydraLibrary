using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Playnite.SDK;

namespace HydraPlaynite.Settings
{
    public class HydraLibrarySettings : ISettings, INotifyPropertyChanged
    {
        private readonly HydraLibrary plugin;
        private HydraLibrarySettings editingSnapshot;

        private bool launchViaHydraUri = true;
        private bool autoSyncPlaytimeOnStartup = true;
        private bool updateExistingGamesOnRefresh = false;
        private bool importInstalledGames = true;
        private bool importUninstalledGames = false;
        private bool keepInstalledIfFilesExistOnDisk = false;

        public event PropertyChangedEventHandler PropertyChanged;

        public bool LaunchViaHydraUri
        {
            get { return launchViaHydraUri; }
            set { SetValue(ref launchViaHydraUri, value); }
        }

        public bool AutoSyncPlaytimeOnStartup
        {
            get { return autoSyncPlaytimeOnStartup; }
            set { SetValue(ref autoSyncPlaytimeOnStartup, value); }
        }

        public bool UpdateExistingGamesOnRefresh
        {
            get { return updateExistingGamesOnRefresh; }
            set { SetValue(ref updateExistingGamesOnRefresh, value); }
        }

        public bool ImportInstalledGames
        {
            get { return importInstalledGames; }
            set { SetValue(ref importInstalledGames, value); }
        }

        public bool ImportUninstalledGames
        {
            get { return importUninstalledGames; }
            set { SetValue(ref importUninstalledGames, value); }
        }

        public bool KeepInstalledIfFilesExistOnDisk
        {
            get { return keepInstalledIfFilesExistOnDisk; }
            set { SetValue(ref keepInstalledIfFilesExistOnDisk, value); }
        }

        public HydraLibrarySettings()
        {
        }

        public HydraLibrarySettings(HydraLibrary plugin)
        {
            this.plugin = plugin;
            if (plugin != null)
            {
                HydraLibrarySettings saved = plugin.LoadPluginSettings<HydraLibrarySettings>();
                if (saved != null)
                {
                    CopyFrom(saved);
                }
            }
        }

        public void ResetToDefaults()
        {
            CopyFrom(new HydraLibrarySettings());
        }

        public void CopyFrom(HydraLibrarySettings source)
        {
            if (source == null) return;

            LaunchViaHydraUri = source.LaunchViaHydraUri;
            AutoSyncPlaytimeOnStartup = source.AutoSyncPlaytimeOnStartup;
            UpdateExistingGamesOnRefresh = source.UpdateExistingGamesOnRefresh;
            ImportInstalledGames = source.ImportInstalledGames;
            ImportUninstalledGames = source.ImportUninstalledGames;
            KeepInstalledIfFilesExistOnDisk = source.KeepInstalledIfFilesExistOnDisk;
        }

        public void BeginEdit()
        {
            editingSnapshot = new HydraLibrarySettings();
            editingSnapshot.CopyFrom(this);
        }

        public void CancelEdit()
        {
            if (editingSnapshot != null)
            {
                CopyFrom(editingSnapshot);
            }
        }

        public void EndEdit()
        {
            if (plugin == null) return;

            bool syncPlaytimeJustChecked = AutoSyncPlaytimeOnStartup && (editingSnapshot == null || !editingSnapshot.AutoSyncPlaytimeOnStartup);
            bool updateArtworkJustChecked = UpdateExistingGamesOnRefresh && (editingSnapshot == null || !editingSnapshot.UpdateExistingGamesOnRefresh);
            bool keepInstalledChanged = editingSnapshot == null || editingSnapshot.KeepInstalledIfFilesExistOnDisk != KeepInstalledIfFilesExistOnDisk;

            plugin.SavePluginSettings(this);

            if (syncPlaytimeJustChecked || updateArtworkJustChecked || keepInstalledChanged)
            {
                plugin.ApplySettingsOnSave(forceUpdateArtwork: updateArtworkJustChecked);
            }
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            return true;
        }

        private void SetValue<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            field = value;
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
