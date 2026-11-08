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
        private bool importUninstalledGames = false;

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

        public bool ImportUninstalledGames
        {
            get { return importUninstalledGames; }
            set { SetValue(ref importUninstalledGames, value); }
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
            ImportUninstalledGames = source.ImportUninstalledGames;
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

            bool launchModeChanged = editingSnapshot == null || editingSnapshot.LaunchViaHydraUri != LaunchViaHydraUri;
            bool syncPlaytimeJustChecked = AutoSyncPlaytimeOnStartup && (editingSnapshot == null || !editingSnapshot.AutoSyncPlaytimeOnStartup);
            bool updateArtworkJustChecked = UpdateExistingGamesOnRefresh && (editingSnapshot == null || !editingSnapshot.UpdateExistingGamesOnRefresh);

            plugin.SavePluginSettings(this);

            if (launchModeChanged || syncPlaytimeJustChecked || updateArtworkJustChecked)
            {
                plugin.ApplySettingsOnSave(
                    forceUpdateActions: launchModeChanged || updateArtworkJustChecked,
                    forceUpdateArtwork: updateArtworkJustChecked);
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
