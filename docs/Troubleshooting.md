# Troubleshooting

## Games aren't showing up in Playnite

* Make sure **Hydra Launcher** is installed and the games appear in its library.
* Check that the game has an `.exe` path set in Hydra (showing **Play** instead of **Download**), or enable **Import not installed games** in the plugin settings.
* Press **F5** (Update Game Library) in Playnite.

## Why are my Steam / Epic / emulated games from Hydra missing?

On purpose. Playnite already has native plugins for Steam, Epic, GOG, and emulators, so this plugin skips them to avoid duplicates. Everything else (including **Recomp** PC ports) imports normally.

## Recomp games keep asking which version to launch

Open the game once inside Hydra, pick the **PC version**, and check **Don't ask again**. You can also change this under **Properties → General → Discs** in Hydra.

## Uninstalling doesn't finish or feels stuck

Hydra doesn't let external apps uninstall games silently, so Playnite opens Hydra for you to remove it there:

* **Downloaded via Hydra:** click **Remove files** (or **Remove from library**).
* **Added manually:** clear the executable path under **Properties → Locations → Clear**, or remove it from the library.
* **Recomp games:** after removing the downloaded files, also remove the PC build under **Properties → General → Discs → Remove**.

> If you enabled **Keep games marked as installed if their files still exist on disk**, Playnite will only mark the game as uninstalled once the files are actually deleted from your drive.

## Playtime isn't syncing

* Check if **Sync playtime with Hydra automatically** is enabled.
* Playnite only updates playtime when Hydra has a higher total (it never lowers your existing hours).

## Game install size looks wrong

The plugin reads the size straight from Hydra's local database — it doesn't scan your folders. If a game shows a huge size, Hydra probably saved a shared parent folder's size; fix the game's executable path in Hydra or edit the **Install Size** field in Playnite.

## Reporting an issue

If something still doesn't work, you can open an [issue on GitHub](https://github.com/renercrisostomo/PlayniteHydraLibrary/issues) and attach your `extensions.log` (`Help → Diagnostics → Open Log Directory` in Playnite). Pull requests are also welcome.