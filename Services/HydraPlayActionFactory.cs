using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using HydraPlaynite.Models;
using Playnite.SDK.Models;

namespace HydraPlaynite.Services
{
    public static class HydraPlayActionFactory
    {
        public const int HydraLaunchTrackingGraceSeconds = 15;

        public static List<GameAction> CreateActions(HydraGameRecord rec, bool launchViaHydraUri, HydraLocalizer localizer = null)
        {
            List<GameAction> actions = new List<GameAction>();
            bool exeExists = !string.IsNullOrWhiteSpace(rec.ExecutablePath) && File.Exists(rec.ExecutablePath);
            bool hasValidUri = !string.IsNullOrWhiteSpace(rec.Shop) && !string.IsNullOrWhiteSpace(rec.ObjectId);
            string hydraUri = hasValidUri
                ? string.Format("hydralauncher://run?shop={0}&objectId={1}", rec.Shop, rec.ObjectId)
                : null;

            string labelPlay = GetText(localizer, "LOCPlayniteHydra_ActionPlay", "Play");
            string labelPlayViaHydra = GetText(localizer, "LOCPlayniteHydra_ActionPlayViaHydra", "Play via Hydra (Cloud Save & Achievements)");
            string labelDirectExe = GetText(localizer, "LOCPlayniteHydra_ActionLaunchDirectExe", "Play direct executable (.exe)");

            if (launchViaHydraUri && hasValidUri)
            {
                actions.Add(CreateHydraUriAction(rec, hydraUri, labelPlayViaHydra, isPrimary: true));
                if (exeExists)
                {
                    actions.Add(CreateExecutableAction(rec, labelDirectExe, isPrimary: false));
                }
            }
            else
            {
                if (exeExists)
                {
                    actions.Add(CreateExecutableAction(rec, labelPlay, isPrimary: true));
                }
                if (hasValidUri)
                {
                    actions.Add(CreateHydraUriAction(rec, hydraUri, labelPlayViaHydra, isPrimary: !exeExists));
                }
            }

            return actions;
        }

        public static ObservableCollection<GameAction> ToObservableCollection(IEnumerable<GameAction> actions)
        {
            return actions != null
                ? new ObservableCollection<GameAction>(actions)
                : new ObservableCollection<GameAction>();
        }

        private static GameAction CreateHydraUriAction(HydraGameRecord rec, string hydraUri, string name, bool isPrimary)
        {
            GameAction action = new GameAction
            {
                Name = name,
                Type = GameActionType.URL,
                Path = hydraUri,
                IsPlayAction = isPrimary
            };

            if (isPrimary && !string.IsNullOrWhiteSpace(rec.InstallDirectory) && Directory.Exists(rec.InstallDirectory))
            {
                action.TrackingMode = TrackingMode.Directory;
                action.TrackingPath = rec.InstallDirectory;
                action.InitialTrackingDelay = HydraLaunchTrackingGraceSeconds;
            }

            return action;
        }

        private static GameAction CreateExecutableAction(HydraGameRecord rec, string name, bool isPrimary)
        {
            return new GameAction
            {
                Name = name,
                Type = GameActionType.File,
                Path = rec.ExecutablePath,
                WorkingDir = rec.InstallDirectory,
                Arguments = rec.LaunchOptions,
                IsPlayAction = isPrimary
            };
        }

        private static string GetText(HydraLocalizer localizer, string key, string fallback)
        {
            return localizer != null ? localizer.Get(key, fallback) : fallback;
        }
    }
}
