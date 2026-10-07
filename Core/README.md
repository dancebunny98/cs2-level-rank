# LevelsRanksCore

LevelsRanksCore provides player ranks, experience, statistics, and an in-game
menu for CounterStrikeSharp. It requires .NET 10, a MySQL database, and the
shared `LevelsRanksApi` assembly.

## Installation

Install the release artifact under `addons/counterstrikesharp/`. Keep
`LevelsRanksApi.dll` in `shared/LevelsRanksApi/` and the optional
`MenuManagerApi.dll` in `shared/MenuManagerApi/`. Do not place either contract
inside the `LevelsRanksCore` plugin folder. Edit the core configuration and
rank definitions before starting the server.

## Menus

The `!lvl` command opens the rank menu through PanoramaMenuManagerCS2. Menu
actions report success, warnings, and errors with Panorama notifications;
other menu types receive the same text in chat. The `!rank` command prints
player statistics in chat.

## Build

```sh
dotnet build "Core/LVL Core.csproj" -c Release
```

The release workflow publishes the core with its `lang/en.json` and
`lang/ru.json` files and this README.
