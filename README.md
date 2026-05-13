# DiscordStatus - CS2 Plugin

A CounterStrikeSharp plugin that displays your CS2 server's player count and current map as a Discord bot's status.

## Features

- Shows real-time player count and map name as bot activity (e.g. `Playing 3/64 Players | de_mirage`)
- Updates instantly when players join/leave or map changes
- Periodic sync every 30 seconds (configurable)
- Bot goes offline when server stops

## Requirements

- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) installed on your CS2 server
- .NET 8.0 SDK (for building)
- A Discord Bot Token ([create one here](https://discord.com/developers/applications))

## Build

```bash
dotnet publish -c Release -o ./publish
```

## Install

1. Copy all DLLs from `publish/` to your CS2 server:
   ```
   game/csgo/addons/counterstrikesharp/plugins/DiscordStatus/
   ```
   Required DLLs: `DiscordStatus.dll`, `Discord.Net.Core.dll`, `Discord.Net.Rest.dll`, `Discord.Net.WebSocket.dll`, `Newtonsoft.Json.dll`, `System.Interactive.Async.dll`, `System.Reactive.dll`

2. Create `config.json` in the same folder (or let the plugin generate one on first run):
   ```json
   {
     "BotToken": "YOUR_DISCORD_BOT_TOKEN",
     "UpdateIntervalSeconds": 30
   }
   ```

3. Restart your CS2 server.

## Configuration

| Key | Description | Default |
|-----|-------------|---------|
| `BotToken` | Your Discord bot token | `""` |
| `UpdateIntervalSeconds` | How often to sync status to Discord | `30` |

## How It Works

- Uses game events (`player_connect`, `player_disconnect`, `OnMapStart`) for instant updates
- Background timer ensures Discord status stays in sync even during idle periods
- Only pushes to Discord when the status text actually changes (avoids rate limits)

## License

MIT
