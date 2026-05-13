using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Timers;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace DiscordStatus;

public class PluginConfig
{
    public string BotToken { get; set; } = "";
    public int UpdateIntervalSeconds { get; set; } = 30;
}

public class DiscordStatus : BasePlugin
{
    public override string ModuleName => "DiscordStatus";
    public override string ModuleVersion => "1.5.0";
    public override string ModuleAuthor => "Shinori";

    private DiscordSocketClient? _client;
    private PluginConfig _config = new();
    private string _lastStatus = "";
    private volatile bool _botReady = false;
    private System.Threading.Timer? _discordTimer;

    // Server data - updated on game events
    private int _playerCount = 0;
    private int _maxPlayers = 64;
    private string _mapName = "unknown";

    public override void Load(bool hotReload)
    {
        LoadConfig();

        if (string.IsNullOrEmpty(_config.BotToken))
        {
            Logger.LogError("[DiscordStatus] BotToken is empty! Set it in config.json");
            return;
        }

        RegisterEventHandler<EventPlayerConnect>(OnPlayerConnect);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);

        if (hotReload)
            CollectData();

        Task.Run(async () =>
        {
            try
            {
                await StartBot();
            }
            catch (Exception ex)
            {
                Logger.LogError("[DiscordStatus] Failed to start bot: {Message}", ex.Message);
            }
        });

        Logger.LogInformation("[DiscordStatus] Plugin loaded");
    }

    public override void Unload(bool hotReload)
    {
        _botReady = false;
        _discordTimer?.Dispose();
        _discordTimer = null;

        if (_client != null)
        {
            try
            {
                _client.SetStatusAsync(UserStatus.Invisible).Wait(3000);
                _client.StopAsync().Wait(3000);
            }
            catch { }
        }

        Logger.LogInformation("[DiscordStatus] Plugin unloaded");
    }

    private void OnMapStart(string mapName)
    {
        _mapName = mapName;
        PushStatusUpdate();
    }

    private HookResult OnPlayerConnect(EventPlayerConnect @event, GameEventInfo info)
    {
        AddTimer(2.0f, () => { CollectData(); PushStatusUpdate(); });
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        AddTimer(1.0f, () => { CollectData(); PushStatusUpdate(); });
        return HookResult.Continue;
    }

    private void CollectData()
    {
        try
        {
            _playerCount = Utilities.GetPlayers()
                .Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false })
                .Count();
            _maxPlayers = Server.MaxPlayers;
            var map = Server.MapName;
            if (!string.IsNullOrEmpty(map))
                _mapName = map;
        }
        catch { }
    }

    private async Task StartBot()
    {
        var socketConfig = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
            AlwaysDownloadUsers = false,
            HandlerTimeout = null,
        };

        _client = new DiscordSocketClient(socketConfig);

        _client.Log += msg =>
        {
            Logger.LogInformation("[DiscordStatus] {Message}", msg.Message);
            return Task.CompletedTask;
        };

        _client.Ready += () =>
        {
            _botReady = true;
            _discordTimer?.Dispose();
            _discordTimer = new System.Threading.Timer(
                _ => PushStatusUpdate(),
                null,
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(_config.UpdateIntervalSeconds));
            return Task.CompletedTask;
        };

        _client.Disconnected += ex =>
        {
            _botReady = false;
            return Task.CompletedTask;
        };

        await _client.LoginAsync(TokenType.Bot, _config.BotToken);
        await _client.StartAsync();
    }

    private void PushStatusUpdate()
    {
        if (!_botReady || _client == null || _client.ConnectionState != ConnectionState.Connected)
            return;

        var statusText = $"{_playerCount}/{_maxPlayers} Players | {_mapName}";

        if (statusText == _lastStatus)
            return;

        _lastStatus = statusText;

        _ = Task.Run(async () =>
        {
            try
            {
                await _client!.SetGameAsync(statusText, null, ActivityType.Playing);
                await _client.SetStatusAsync(UserStatus.Online);
            }
            catch { }
        });
    }

    private void LoadConfig()
    {
        var configPath = Path.Combine(ModuleDirectory, "config.json");

        if (!File.Exists(configPath))
        {
            _config = new PluginConfig();
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
            return;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            _config = JsonSerializer.Deserialize<PluginConfig>(json) ?? new PluginConfig();
        }
        catch
        {
            _config = new PluginConfig();
        }
    }
}
