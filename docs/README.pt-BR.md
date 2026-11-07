# PlayniteHydraLibrary

Readme in [🇺🇸 English](../README.md) | [🇷🇺 Русский](README.ru-RU.md)

Plugin de biblioteca para o [Playnite](https://playnite.link/) que importa seus jogos do [Hydra Launcher](https://github.com/hydralauncher/hydra).

## Funcionalidades

- Importa automaticamente seus jogos instalados (e opcionalmente não instalados) direto dos arquivos locais do Hydra, sem precisar de login.
- Inicia os jogos pelo Hydra (`hydralauncher://run`) para manter o Cloud Save e as conquistas do Hydra funcionando, ou direto pelo `.exe`.
- Sincroniza tempo de jogo, última sessão e mídias personalizadas (capas, ícones e imagens de fundo).
- Ignora integrações de outras lojas dentro do Hydra (Steam, Epic e emuladores) para não duplicar sua biblioteca no Playnite.

## Instalação

1. Baixe o arquivo `.pext` mais recente na aba [Releases](https://github.com/renercrisostomo/PlayniteHydraLibrary/releases).
2. Abra o arquivo `.pext` (ou arraste-o para dentro da janela do Playnite).

## Traduções

Os arquivos de tradução ficam na pasta `Localization/` (`en_US.xaml`, `pt_BR.xaml` e `ru_RU.xaml`). Para adicionar outro idioma, basta copiar o `en_US.xaml` com o código do idioma (ex: `es_ES.xaml`) e traduzir os textos.
