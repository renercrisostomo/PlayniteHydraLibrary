# PlayniteHydraLibrary

Readme in [🇺🇸 English](../README.md) | [🇧🇷 Português](README.pt-BR.md)

Плагин библиотеки для [Playnite](https://playnite.link/), который автоматически импортирует ваши игры из [Hydra Launcher](https://github.com/hydralauncher/hydra).

## Возможности

- Автоматически импортирует установленные (и при желании неустановленные) игры напрямую из локальной базы данных Hydra (`%APPDATA%\hydra\hydra-db`), без необходимости авторизации.
- Запускает игры через Hydra (`hydralauncher://run`) для работы облачных сохранений (Hydra Cloud Saves) и достижений, либо напрямую через исполняемый файл (`.exe`).
- Синхронизирует время в игре, дату последнего запуска и пользовательские изображения (обложки, иконки и фоны), настроенные в Hydra.
- Игнорирует внешние интеграции магазинов внутри Hydra (Steam, Epic и эмуляторы), чтобы избежать дубликатов в библиотеке Playnite.

## Установка

1. Скачайте последний файл `.pext` на странице [Releases](https://github.com/renercrisostomo/PlayniteHydraLibrary/releases).
2. Откройте файл `.pext` (или перетащите его в окно Playnite) для установки.

## Перевод

Языковые файлы находятся в папке `Localization/` (`en_US.xaml`, `pt_BR.xaml` и `ru_RU.xaml`). Чтобы добавить новый язык, скопируйте файл `en_US.xaml` с кодом нужной локали (например, `es_ES.xaml`) и переведите строки.
