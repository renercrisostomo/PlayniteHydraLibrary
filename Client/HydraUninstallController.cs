using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HydraPlaynite.Models;
using HydraPlaynite.Services;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace HydraPlaynite.Client
{
    /// <summary>
    /// Controlador de desinstalação que delega a remoção ao cliente Hydra Launcher
    /// e monitora a exclusão do executável ou a remoção do registro no LevelDB.
    /// </summary>
    public class HydraUninstallController : UninstallController
    {
        public const int WatcherPollIntervalMilliseconds = 2000;

        private static readonly ConcurrentDictionary<Guid, byte> activeUninstallGameIds = new ConcurrentDictionary<Guid, byte>();

        private readonly HydraRepository repository;
        private readonly HydraClient client;
        private readonly HydraLocalizer localizer;
        private readonly ILogger logger;
        private CancellationTokenSource watcherToken;
        private string lastDatabaseSignature;
        private bool lastDatabaseInstalledState = true;

        public bool WasUninstalledInvoked { get; private set; }

        public HydraUninstallController(
            Game game,
            HydraRepository repository,
            HydraClient client,
            HydraLocalizer localizer = null,
            ILogger logger = null) : base(game)
        {
            this.repository = repository;
            this.client = client;
            this.localizer = localizer;
            this.logger = logger;
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

            if (!IsGameCurrentlyInstalled(forceReloadDb: true))
            {
                CompleteUninstallation();
                return;
            }

            if (client == null || !client.IsInstalled)
            {
                throw new Exception(GetLocalizedText("LOCPlayniteHydra_ErrorClientNotFound", "Hydra Launcher installation not found."));
            }

            if (Game != null && Game.Id != Guid.Empty)
            {
                activeUninstallGameIds[Game.Id] = 1;
            }

            client.Open();
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
                            logger.Warn(ex, "HydraUninstallController: Falha ao verificar status de desinstalação do jogo " + (Game != null ? Game.Name : string.Empty));
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
        /// Verifica se o jogo continua instalado checando primeiro o executável no disco
        /// e relendo o LevelDB apenas quando os arquivos do banco sofrem alteração.
        /// </summary>
        public bool IsGameCurrentlyInstalled(bool forceReloadDb = true)
        {
            if (Game == null)
                return false;

            if (Game.GameActions != null)
            {
                foreach (GameAction action in Game.GameActions)
                {
                    if (action != null && action.Type == GameActionType.File && !string.IsNullOrWhiteSpace(action.Path))
                    {
                        if (!File.Exists(action.Path))
                            return false;
                    }
                }
            }

            if (repository != null && !string.IsNullOrWhiteSpace(Game.GameId))
            {
                string currentSignature = repository.GetDatabaseStateSignature();
                if (!forceReloadDb &&
                    !string.IsNullOrEmpty(lastDatabaseSignature) &&
                    string.Equals(currentSignature, lastDatabaseSignature, StringComparison.Ordinal))
                {
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
                        lastDatabaseInstalledState = false;
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(matchingRecord.ExecutablePath) || !File.Exists(matchingRecord.ExecutablePath))
                    {
                        lastDatabaseInstalledState = false;
                        return false;
                    }

                    lastDatabaseInstalledState = true;
                    return true;
                }
            }

            if (Game.GameActions != null)
            {
                foreach (GameAction action in Game.GameActions)
                {
                    if (action != null && action.Type == GameActionType.File && !string.IsNullOrWhiteSpace(action.Path))
                    {
                        return File.Exists(action.Path);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(Game.InstallDirectory))
            {
                return Directory.Exists(Game.InstallDirectory);
            }

            return false;
        }

        private void CompleteUninstallation()
        {
            WasUninstalledInvoked = true;
            Dispose();
            try
            {
                InvokeOnUninstalled(new GameUninstalledEventArgs());
            }
            catch (NullReferenceException)
            {
            }
        }

        private string GetLocalizedText(string key, string fallback)
        {
            return localizer != null ? localizer.Get(key, fallback) : fallback;
        }
    }
}
