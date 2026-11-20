using System;
using System.Collections.Generic;
using System.IO;
using HydraPlaynite.Models;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace HydraPlaynite.Services
{
    /// <summary>
    /// Constrói dinamicamente o controlador de execução do jogo (via protocolo hydralauncher://run
    /// ou via executável direto .exe) para uso em GetPlayActions.
    /// </summary>
    public static class HydraPlayActionFactory
    {
        public const int HydraLaunchTrackingGraceSeconds = 15;

        public static List<PlayController> CreatePlayControllers(
            Game game,
            HydraGameRecord activeHydraRecord,
            bool launchViaHydraUri,
            HydraLocalizer localizer = null)
        {
            List<PlayController> controllers = new List<PlayController>();
            if (game == null)
                return controllers;

            bool isLaunchableInHydra = activeHydraRecord != null &&
                                       !activeHydraRecord.IsDeleted &&
                                       !string.IsNullOrWhiteSpace(activeHydraRecord.ExecutablePath);
            bool hasValidUri = isLaunchableInHydra &&
                               !string.IsNullOrWhiteSpace(activeHydraRecord.Shop) &&
                               !string.IsNullOrWhiteSpace(activeHydraRecord.ObjectId);

            string resolvedExe = ResolveExecutablePath(game, activeHydraRecord);
            bool exeExists = !string.IsNullOrWhiteSpace(resolvedExe) && File.Exists(resolvedExe);

            if (launchViaHydraUri && hasValidUri)
            {
                string hydraUri = string.Format("hydralauncher://run?shop={0}&objectId={1}", activeHydraRecord.Shop, activeHydraRecord.ObjectId);
                string trackingDir = exeExists
                    ? Path.GetDirectoryName(resolvedExe)
                    : (!string.IsNullOrWhiteSpace(activeHydraRecord.InstallDirectory) ? activeHydraRecord.InstallDirectory : game.InstallDirectory);

                AutomaticPlayController hydraController = new AutomaticPlayController(game)
                {
                    Name = GetText(localizer, "LOCPlayniteHydra_ActionPlayViaHydra", "Play via Hydra (Cloud Save & Achievements)"),
                    Type = AutomaticPlayActionType.Url,
                    Path = hydraUri
                };

                if (!string.IsNullOrWhiteSpace(trackingDir) && Directory.Exists(trackingDir))
                {
                    hydraController.TrackingMode = TrackingMode.Directory;
                    hydraController.TrackingPath = trackingDir;
                    hydraController.InitialTrackingDelay = HydraLaunchTrackingGraceSeconds;
                }

                controllers.Add(hydraController);
                return controllers;
            }

            if (exeExists)
            {
                string exeDir = Path.GetDirectoryName(resolvedExe);
                AutomaticPlayController directExeController = new AutomaticPlayController(game)
                {
                    Name = GetText(localizer, "LOCPlayniteHydra_ActionPlay", "Play"),
                    Type = AutomaticPlayActionType.File,
                    Path = resolvedExe,
                    WorkingDir = !string.IsNullOrWhiteSpace(exeDir) ? exeDir : game.InstallDirectory,
                    Arguments = activeHydraRecord != null ? activeHydraRecord.LaunchOptions : null,
                    TrackingMode = TrackingMode.Default
                };

                controllers.Add(directExeController);
                return controllers;
            }

            if (hasValidUri)
            {
                string hydraUri = string.Format("hydralauncher://run?shop={0}&objectId={1}", activeHydraRecord.Shop, activeHydraRecord.ObjectId);
                controllers.Add(new AutomaticPlayController(game)
                {
                    Name = GetText(localizer, "LOCPlayniteHydra_ActionPlayViaHydra", "Play via Hydra (Cloud Save & Achievements)"),
                    Type = AutomaticPlayActionType.Url,
                    Path = hydraUri
                });
            }

            return controllers;
        }

        public static bool IsLegacyPluginGeneratedAction(GameAction action)
        {
            if (action == null)
                return false;

            if (action.Type == GameActionType.URL &&
                !string.IsNullOrWhiteSpace(action.Path) &&
                action.Path.StartsWith("hydralauncher://run", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (action.Type == GameActionType.File && !string.IsNullOrWhiteSpace(action.Name))
            {
                string n = action.Name.Trim();
                if (string.Equals(n, "Play", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(n, "Jogar", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(n, "Играть", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(n, "Play direct executable (.exe)", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(n, "Abrir executável direto (.exe)", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(n, "Запустить напрямую (.exe)", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ResolveExecutablePath(Game game, HydraGameRecord activeHydraRecord)
        {
            if (activeHydraRecord != null &&
                !string.IsNullOrWhiteSpace(activeHydraRecord.ExecutablePath) &&
                File.Exists(activeHydraRecord.ExecutablePath))
            {
                return activeHydraRecord.ExecutablePath;
            }

            if (game != null &&
                !string.IsNullOrWhiteSpace(game.InstallDirectory) &&
                Directory.Exists(game.InstallDirectory))
            {
                string found = HydraRepository.FindBestExecutableInFolder(game.InstallDirectory, game.Name);
                if (!string.IsNullOrEmpty(found) && File.Exists(found))
                    return found;
            }

            return null;
        }

        private static string GetText(HydraLocalizer localizer, string key, string fallback)
        {
            return localizer != null ? localizer.Get(key, fallback) : fallback;
        }
    }
}
