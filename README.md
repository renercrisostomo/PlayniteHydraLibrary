# PlayniteHydraLibrary

Readme in [🇧🇷 Português](docs/README.pt-BR.md) | [🇷🇺 Русский](docs/README.ru-RU.md)

Library plugin for [Playnite](https://playnite.link/) that imports your games from [Hydra Launcher](https://github.com/hydralauncher/hydra).

## Features

- Automatically imports your games (including Recomps) directly from your local Hydra files, without requiring login.
- Launches games through Hydra (`hydralauncher://run`) to keep Hydra Cloud Saves and achievements working, or directly via the `.exe`.
- Syncs playtime, last played session, and custom media (covers, icons, and background images).
- Skips external store integrations inside Hydra (Steam, Epic, and emulators) to avoid duplicating your Playnite library.

## Installation

1. Download the latest `.pext` file from the [Releases](https://github.com/renercrisostomo/PlayniteHydraLibrary/releases) page.
2. Open the `.pext` file (or drag and drop it into Playnite) to install.

## Translations

Language files are located in the `Localization/` folder (`en_US.xaml`, `pt_BR.xaml`, and `ru_RU.xaml`). To add a new language, copy `en_US.xaml` using the target locale name (e.g. `es_ES.xaml`) and translate the values.
