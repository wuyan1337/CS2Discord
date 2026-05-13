using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CounterStrikeSharp.API.Core;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace DiscordStatus;

public class PluginConfig
{
    public string BotToken { get; set; } = "";
    public int UpdateIntervalSeconds { get; set; } = 15;
    public int ServerPort { get; set; } = 27015;
}

public class DiscordStatus : BasePlugin
{
    public override string ModuleName => "DiscordStatus";
    public override string ModuleVersion => "2.0.0";
    public override string ModuleAuthor => "Shinori";

    private DiscordSocketClient? _client;
    private PluginConfig _config = new();
    private string _lastStatus = "";
    private volatile bool _botReady = false;
    private System.Threading.Timer? _pollTimer;

    public override void Load(bool hotReload)
    {
        LoadConfig();

        if (string.IsNullOrEmpty(_config.BotToken))
        {
            Logger.LogError("[DiscordStatus] BotToken is empty! Set it in config.json");
            return;
        }

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

        Logger.LogInformation("[DiscordStatus] Plugin loaded (v2.0 - A2S polling, port {Port})", _config.ServerPort);
    }

    public override void Unload(bool hotReload)
    {
        _botReady = false;
        _pollTimer?.Dispose();
        _pollTimer = null;

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
            Logger.LogInformation("[DiscordStatus] Bot ready, starting A2S poll timer every {Sec}s", _config.UpdateIntervalSeconds);

            _pollTimer?.Dispose();
            _pollTimer = new System.Threading.Timer(
                _ => PollAndUpdate(),
                null,
                TimeSpan.FromSeconds(2),
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

    private void PollAndUpdate()
    {
        if (!_botReady || _client == null || _client.ConnectionState != ConnectionState.Connected)
            return;

        try
        {
            var info = QueryA2SInfo("127.0.0.1", _config.ServerPort);
            if (info == null)
                return;

            var statusText = $"{info.Value.players}/{info.Value.maxPlayers} Players | {info.Value.map}";

            if (statusText == _lastStatus)
                return;

            _lastStatus = statusText;
            Logger.LogInformation("[DiscordStatus] Status: {Status}", statusText);

            _ = Task.Run(async () =>
            {
                try
                {
                    await _client!.SetGameAsync(statusText, null, ActivityType.Playing);
                    await _client.SetStatusAsync(UserStatus.Online);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[DiscordStatus] Push failed: {Message}", ex.Message);
                }
            });
        }
        catch (Exception ex)
        {
            Logger.LogWarning("[DiscordStatus] Poll error: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// A2S_INFO query - works even when server is hibernating
    /// </summary>
    private (int players, int maxPlayers, string map)? QueryA2SInfo(string host, int port)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 3000;
            udp.Client.SendTimeout = 3000;

            var endpoint = new IPEndPoint(IPAddress.Parse(host), port);

            // A2S_INFO request: FF FF FF FF 54 + "Source Engine Query\0"
            byte[] request = new byte[] {
                0xFF, 0xFF, 0xFF, 0xFF, 0x54,
                0x53, 0x6F, 0x75, 0x72, 0x63, 0x65, 0x20,
                0x45, 0x6E, 0x67, 0x69, 0x6E, 0x65, 0x20,
                0x51, 0x75, 0x65, 0x72, 0x79, 0x00
            };

            udp.Send(request, request.Length, endpoint);

            var remoteEp = new IPEndPoint(IPAddress.Any, 0);
            byte[] response = udp.Receive(ref remoteEp);

            // Check if it's a challenge response (0x41)
            if (response.Length >= 9 && response[4] == 0x41)
            {
                // Resend with challenge
                byte[] challengeRequest = new byte[request.Length + 4];
                Array.Copy(request, challengeRequest, request.Length);
                challengeRequest[request.Length] = response[5];
                challengeRequest[request.Length + 1] = response[6];
                challengeRequest[request.Length + 2] = response[7];
                challengeRequest[request.Length + 3] = response[8];

                udp.Send(challengeRequest, challengeRequest.Length, endpoint);
                response = udp.Receive(ref remoteEp);
            }

            if (response.Length < 10 || response[4] != 0x49)
                return null;

            // Parse A2S_INFO response
            int offset = 5; // Skip header (FF FF FF FF 49)
            offset++; // Protocol

            // Name (null-terminated string)
            SkipString(response, ref offset);
            // Map
            string map = ReadString(response, ref offset);
            // Folder
            SkipString(response, ref offset);
            // Game
            SkipString(response, ref offset);
            // Steam App ID (short)
            offset += 2;
            // Players
            int players = response[offset++];
            // Max players
            int maxPlayers = response[offset++];

            return (players, maxPlayers, map);
        }
        catch
        {
            return null;
        }
    }

    private string ReadString(byte[] data, ref int offset)
    {
        int start = offset;
        while (offset < data.Length && data[offset] != 0)
            offset++;
        var str = Encoding.UTF8.GetString(data, start, offset - start);
        offset++; // skip null terminator
        return str;
    }

    private void SkipString(byte[] data, ref int offset)
    {
        while (offset < data.Length && data[offset] != 0)
            offset++;
        offset++; // skip null terminator
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
