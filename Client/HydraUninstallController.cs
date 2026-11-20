using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using HydraPlaynite.Models;
using HydraPlaynite.Services;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace HydraPlaynite.Client
{
    /// <summary>
    /// Controlador de desinstalação que solicita confirmação ao usuário, abre o cliente Hydra Launcher
    /// sem bloquear a interface do Playnite e monitora a remoção dos arquivos ou do executável no LevelDB.
    /// </summary>
    public class HydraUninstallController : UninstallController
    {
        public const int WatcherPollIntervalMilliseconds = 2000;

        private static readonly ConcurrentDictionary<Guid, byte> activeUninstallGameIds = new ConcurrentDictionary<Guid, byte>();

        private readonly HydraRepository repository;
        private readonly HydraClient client;
        private readonly HydraLocalizer localizer;
        private readonly ILogger logger;
        private readonly IPlayniteAPI api;
        private readonly Func<bool> confirmationPromptOverride;
        private readonly bool keepInstalledIfFilesExistOnDisk;
        private CancellationTokenSource watcherToken;
        private string lastDatabaseSignature;
        private bool lastDatabaseInstalledState = true;
        private string monitoredExecutablePath;

        public bool WasUninstalledInvoked { get; private set; }
        public bool WasCancelledByUser { get; private set; }

        public HydraUninstallController(
            Game game,
            HydraRepository repository,
            HydraClient client,
            HydraLocalizer localizer = null,
            ILogger logger = null,
            IPlayniteAPI api = null,
            Func<bool> confirmationPromptOverride = null,
            bool keepInstalledIfFilesExistOnDisk = false) : base(game)
        {
            this.repository = repository;
            this.client = client;
            this.localizer = localizer;
            this.logger = logger;
            this.api = api;
            this.confirmationPromptOverride = confirmationPromptOverride;
            this.keepInstalledIfFilesExistOnDisk = keepInstalledIfFilesExistOnDisk;
            Name = GetLocalizedText("LOCPlayniteHydra_ActionUninstall", "Uninstall using Hydra client");
        }

        public static bool IsUninstallActive(Guid gameId)
        {
            return gameId != Guid.Empty && activeUninstallGameIds.ContainsKey(gameId);
        }

        public override void Dispose()
        {
            if (Game != null && Game.Id != Guid.Empty)
            {
                byte removed;
                activeUninstallGameIds.TryRemove(Game.Id, out removed);
            }

            if (watcherToken != null)
            {
                watcherToken.Cancel();
            }
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            Dispose();
            WasUninstalledInvoked = false;
            WasCancelledByUser = false;

            if (!IsExecutablePresentOnDisk())
            {
                CompleteUninstallation();
                return;
            }

            if (client == null || !client.IsInstalled)
            {
                ClearUninstallingState();
                throw new Exception(GetLocalizedText("LOCPlayniteHydra_ErrorClientNotFound", "Hydra Launcher installation not found."));
            }

            if (!ConfirmOpenHydraForUninstall())
            {
                WasCancelledByUser = true;
                ClearUninstallingState();
                return;
            }

            if (Game != null && Game.Id != Guid.Empty)
            {
                activeUninstallGameIds[Game.Id] = 1;
            }

            Task.Run(() =>
            {
                try
                {
                    client.Open();
                }
                catch (Exception ex)
                {
                    if (logger != null)
                    {
                        logger.Warn(ex, "HydraUninstallController: Failed to open Hydra Launcher client.");
                    }
                }
            });

            StartUninstallWatcher();
        }

        public async void StartUninstallWatcher()
        {
            watcherToken = new CancellationTokenSource();
            CancellationToken token = watcherToken.Token;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    bool isInstalled = true;
                    try
                    {
                        isInstalled = await Task.Run(() => IsGameCurrentlyInstalled(forceReloadDb: false));
                    }
                    catch (Exception ex)
                    {
                        if (logger != null)
                        {
                            logger.Warn(ex, "HydraUninstallController: Failed to check uninstall status for game " + (Game != null ? Game.Name : string.Empty));
                        }
                    }

                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (!isInstalled)
                    {
                        CompleteUninstallation();
                        return;
                    }

                    try
                    {
                        await Task.Delay(WatcherPollIntervalMilliseconds, token);
                    }
                    catch (TaskCanceledException)
                    {
                        return;
                    }
                }
            }
            finally
            {
                if (Game != null && Game.Id != Guid.Empty && token.IsCancellationRequested)
                {
                    byte removed;
                    activeUninstallGameIds.TryRemove(Game.Id, out removed);
                }
            }
        }

        /// <summary>
        /// Realiza verificação rápida em disco sem ler o LevelDB para evitar bloqueio da thread de interface.
        /// Verifica tanto ações de arquivo do Playnite quanto o último executável monitorado do Hydra.
        /// </summary>
        public bool IsExecutablePresentOnDisk()
        {
            if (Game == null)
                return false;

            if (!string.IsNullOrWhiteSpace(monitoredExecutablePath))
            {
                return File.Exists(monitoredExecutablePath);
            }

            if (Game.GameActions != null)
            {
                bool hasFileAction = false;
                foreach (GameAction action in Game.GameActions)
                {
                    if (action != null && action.Type == GameActionType.File && !string.IsNullOrWhiteSpace(action.Path))
                    {
                        hasFileAction = true;
                        if (File.Exists(action.Path))
                            return true;
                    }
                }

                if (hasFileAction)
                    return false;
            }

            if (!string.IsNullOrWhiteSpace(Game.InstallDirectory))
            {
                if (!Directory.Exists(Game.InstallDirectory))
                    return false;

                string foundExe = HydraRepository.FindBestExecutableInFolder(Game.InstallDirectory, Game.Name);
                return !string.IsNullOrEmpty(foundExe) && File.Exists(foundExe);
            }

            return false;
        }

        /// <summary>
        /// Verifica em segundo plano se o jogo continua instalado checando o executável no disco
        /// e relendo o LevelDB quando os arquivos do banco sofrem alteração (como ao clicar em "Limpar" ou "Remover da biblioteca").
        /// </summary>
        public bool IsGameCurrentlyInstalled(bool forceReloadDb = true)
        {
            if (!IsExecutablePresentOnDisk())
                return false;

            if (repository != null && !string.IsNullOrWhiteSpace(Game.GameId))
            {
                string currentSignature = repository.GetDatabaseStateSignature();
                if (!forceReloadDb &&
                    !string.IsNullOrEmpty(lastDatabaseSignature) &&
                    string.Equals(currentSignature, lastDatabaseSignature, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(monitoredExecutablePath) && !File.Exists(monitoredExecutablePath))
                    {
                        lastDatabaseInstalledState = false;
                        return false;
                    }

                    return lastDatabaseInstalledState;
                }

                List<HydraGameRecord> records = repository.LoadAllGames();
                lastDatabaseSignature = currentSignature;

                if (records != null)
                {
                    HydraGameRecord matchingRecord = null;
                    foreach (HydraGameRecord rec in records)
                    {
                        if (rec != null && string.Equals(rec.UniqueKey, Game.GameId, StringComparison.OrdinalIgnoreCase))
                        {
                            matchingRecord = rec;
                            break;
                        }
                    }

                    if (matchingRecord == null || matchingRecord.IsDeleted)
                    {
                        bool stillInstalled = keepInstalledIfFilesExistOnDisk && IsExecutablePresentOnDisk();
                        lastDatabaseInstalledState = stillInstalled;
                        return stillInstalled;
                    }

                    if (string.IsNullOrWhiteSpace(matchingRecord.ExecutablePath))
                    {
                        bool stillInstalled = keepInstalledIfFilesExistOnDisk && IsExecutablePresentOnDisk();
                        if (!stillInstalled)
                        {
                            monitoredExecutablePath = null;
                        }
                        lastDatabaseInstalledState = stillInstalled;
                        return stillInstalled;
                    }

                    if (!File.Exists(matchingRecord.ExecutablePath))
                    {
                        monitoredExecutablePath = null;
                        lastDatabaseInstalledState = false;
                        return false;
                    }

                    monitoredExecutablePath = matchingRecord.ExecutablePath;
                    lastDatabaseInstalledState = true;
                    return true;
                }
            }

            return true;
        }

        private bool ConfirmOpenHydraForUninstall()
        {
            if (confirmationPromptOverride != null)
            {
                return confirmationPromptOverride();
            }

            if (api == null || api.Dialogs == null)
            {
                return true;
            }

            string gameTitle = Game != null && !string.IsNullOrWhiteSpace(Game.Name)
                ? Game.Name
                : "Game";

            string title = GetLocalizedText(
                "LOCPlayniteHydra_DialogUninstallTitle",
                "Uninstall via Hydra");

            string messageTemplate = keepInstalledIfFilesExistOnDisk
                ? GetLocalizedText(
                    "LOCPlayniteHydra_DialogUninstallMessageKeepFiles",
                    "To uninstall \"{0}\", remove the game files in Hydra Launcher (or delete the folder manually). For Recomp games, also remove the PC version under Properties -> Discs.")
                : GetLocalizedText(
                    "LOCPlayniteHydra_DialogUninstallMessage",
                    "To uninstall \"{0}\", remove the game files, remove it from your library, or click \"Clear\" next to the executable path in Hydra. For Recomp games, also remove the PC version under Properties -> Discs.");

            string message = string.Format(messageTemplate, gameTitle);

            MessageBoxOption openHydraOption = new MessageBoxOption(
                GetLocalizedText("LOCPlayniteHydra_DialogBtnOpenHydra", "Open Hydra"),
                true,
                false);

            MessageBoxOption cancelOption = new MessageBoxOption(
                GetLocalizedText("LOCPlayniteHydra_DialogBtnCancel", "Cancel"),
                false,
                true);

            List<MessageBoxOption> options = new List<MessageBoxOption>
            {
                openHydraOption,
                cancelOption
            };

            MessageBoxOption selected = api.Dialogs.ShowMessage(
                message,
                title,
                MessageBoxImage.Information,
                options);

            return selected != null && !selected.IsCancel && ReferenceEquals(selected, openHydraOption);
        }

        private void ClearUninstallingState()
        {
            if (Game == null)
                return;

            RunOnUiThread(() =>
            {
                if (Game != null)
                {
                    Game.IsUninstalling = false;
                    UpdateGameInDatabase();
                }
            });

            if (api != null && api.Database != null)
            {
                Task.Run(async () =>
                {
                    await Task.Delay(150);
                    RunOnUiThread(() =>
                    {
                        if (Game != null && Game.IsUninstalling)
                        {
                            Game.IsUninstalling = false;
                            UpdateGameInDatabase();
                        }
                    });
                });
            }
        }

        private void CompleteUninstallation()
        {
            WasUninstalledInvoked = true;
            Dispose();

            RunOnUiThread(() =>
            {
                if (Game != null)
                {
                    Game.IsUninstalling = false;
                    Game.IsInstalled = false;
                    UpdateGameInDatabase();
                }

                try
                {
                    InvokeOnUninstalled(new GameUninstalledEventArgs());
                }
                catch (NullReferenceException)
                {
                }
            });
        }

        private void UpdateGameInDatabase()
        {
            if (Game == null || api == null || api.Database == null || api.Database.Games == null)
                return;

            try
            {
                api.Database.Games.Update(Game);
            }
            catch (Exception ex)
            {
                if (logger != null)
                {
                    logger.Warn(ex, "HydraUninstallController: Failed to update game state in Playnite database.");
                }
            }
        }

        private static void RunOnUiThread(Action action)
        {
            if (action == null)
                return;

            try
            {
                Application app = Application.Current;
                if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.BeginInvoke(action);
                    return;
                }
            }
            catch
            {
            }

            action();
        }

        private string GetLocalizedText(string key, string fallback)
        {
            return localizer != null ? localizer.Get(key, fallback) : fallback;
        }
    }
}
