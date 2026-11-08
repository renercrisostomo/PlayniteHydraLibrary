# PlayniteHydraLibrary

Readme in [🇺🇸 English](../README.md) | [🇧🇷 Português](README.pt-BR.md)

Плагин библиотеки для [Playnite](https://playnite.link/), который импортирует ваши игры из [Hydra Launcher](https://github.com/hydralauncher/hydra).

## Возможности

- Автоматически импортирует ваши игры (включая Recomps) напрямую из локальных файлов Hydra, без необходимости входа в аккаунт.
- Запускает игры через Hydra (`hydralauncher://run`) для работы облачных сохранений (Cloud Save) и достижений Hydra, либо напрямую через `.exe`.
- Синхронизирует время в игре, последнюю сессию и пользовательские медиафайлы (обложки, иконки и фоновые изображения).
- Игнорирует интеграции сторонних магазинов внутри Hydra (Steam, Epic и эмуляторы), чтобы не дублировать вашу библиотеку в Playnite.

## Установка

1. Скачайте последний файл `.pext` на странице [Releases](https://github.com/renercrisostomo/PlayniteHydraLibrary/releases).
2. Откройте файл `.pext` (или перетащите его в окно Playnite) для установки.

## Перевод

Языковые файлы находятся в папке `Localization/` (`en_US.xaml`, `pt_BR.xaml` и `ru_RU.xaml`). Чтобы добавить новый язык, скопируйте файл `en_US.xaml` с кодом нужной локали (например, `es_ES.xaml`) и переведите строки.
