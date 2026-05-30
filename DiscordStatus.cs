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
    public override string ModuleVersion => "2.1.0";
    public override string ModuleAuthor => "Shinori";

    private DiscordSocketClient? _client;
    private PluginConfig _config = new();
    private string _lastStatus = "";
    private volatile bool _botReady = false;
    private System.Threading.Timer? _pollTimer;
    private string _logFile = "";
    private CancellationTokenSource? _cts;

    public override void Load(bool hotReload)
    {
        _logFile = Path.Combine(ModuleDirectory, "discord_status.log");
        _cts = new CancellationTokenSource();

        WriteLog("Plugin loading...");
        LoadConfig();

        if (string.IsNullOrEmpty(_config.BotToken))
        {
            WriteLog("ERROR: BotToken is empty! Set it in config.json");
            Logger.LogError("[DiscordStatus] BotToken is empty!");
            return;
        }

        WriteLog($"Config loaded: port={_config.ServerPort}, interval={_config.UpdateIntervalSeconds}s");

        _ = Task.Run(async () =>
        {
            try
            {
                await StartBot();
            }
            catch (Exception ex)
            {
                WriteLog($"FATAL: StartBot exception: {ex}");
            }
        });

        Logger.LogInformation("[DiscordStatus] Plugin loaded (v2.1 - A2S polling, port {Port})", _config.ServerPort);
    }

    public override void Unload(bool hotReload)
    {
        WriteLog("Plugin unloading...");
        _botReady = false;
        _cts?.Cancel();
        _pollTimer?.Dispose();
        _pollTimer = null;

        if (_client != null)
        {
            try
            {
                _client.SetStatusAsync(UserStatus.Invisible).Wait(3000);
                _client.StopAsync().Wait(3000);
                _client.Dispose();
            }
            catch (Exception ex)
            {
                WriteLog($"Unload cleanup error: {ex.Message}");
            }
        }

        _client = null;
        WriteLog("Plugin unloaded");
    }

    private async Task StartBot()
    {
        var socketConfig = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
            AlwaysDownloadUsers = false,
            HandlerTimeout = null,
            ConnectionTimeout = 30000,
            UseSystemClock = true,
        };

        _client = new DiscordSocketClient(socketConfig);

        _client.Log += msg =>
        {
            WriteLog($"[Discord.Net] {msg.Severity}: {msg.Message}{(msg.Exception != null ? $" | Exception: {msg.Exception}" : "")}");
            return Task.CompletedTask;
        };

        _client.Ready += () =>
        {
            _botReady = true;
            WriteLog($"Bot READY! Connected as {_client.CurrentUser?.Username}. Starting poll timer every {_config.UpdateIntervalSeconds}s");

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
            WriteLog($"Bot DISCONNECTED: {ex?.Message ?? "unknown reason"}");
            return Task.CompletedTask;
        };

        _client.Connected += () =>
        {
            WriteLog("Bot connected to gateway");
            return Task.CompletedTask;
        };

        WriteLog("Attempting login...");
        await _client.LoginAsync(TokenType.Bot, _config.BotToken);
        WriteLog("Login successful, starting...");
        await _client.StartAsync();
        WriteLog("StartAsync completed, waiting for Ready event...");

        // Keep the task alive so the bot stays connected
        try
        {
            await Task.Delay(Timeout.Infinite, _cts!.Token);
        }
        catch (OperationCanceledException)
        {
            WriteLog("Bot task cancelled (plugin unloading)");
        }
    }

    private void PollAndUpdate()
    {
        if (!_botReady || _client == null || _client.ConnectionState != ConnectionState.Connected)
            return;

        try
        {
            var info = QueryA2SInfo("127.0.0.1", _config.ServerPort);
            if (info == null)
            {
                WriteLog("A2S query returned null");
                return;
            }

            var statusText = $"{info.Value.players}/{info.Value.maxPlayers} on {info.Value.map}";

            if (statusText == _lastStatus)
                return;

            _lastStatus = statusText;
            WriteLog($"Updating status: {statusText}");

            _ = Task.Run(async () =>
            {
                try
                {
                    await _client!.SetGameAsync(statusText, null, ActivityType.Playing);
                    await _client.SetStatusAsync(UserStatus.Online);
                    WriteLog($"Status updated OK: {statusText}");
                }
                catch (Exception ex)
                {
                    WriteLog($"SetGameAsync failed: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            WriteLog($"Poll error: {ex.Message}");
        }
    }

    private (int players, int maxPlayers, string map)? QueryA2SInfo(string host, int port)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 3000;
            udp.Client.SendTimeout = 3000;

            var endpoint = new IPEndPoint(IPAddress.Parse(host), port);

            byte[] request = new byte[] {
                0xFF, 0xFF, 0xFF, 0xFF, 0x54,
                0x53, 0x6F, 0x75, 0x72, 0x63, 0x65, 0x20,
                0x45, 0x6E, 0x67, 0x69, 0x6E, 0x65, 0x20,
                0x51, 0x75, 0x65, 0x72, 0x79, 0x00
            };

            udp.Send(request, request.Length, endpoint);

            var remoteEp = new IPEndPoint(IPAddress.Any, 0);
            byte[] response = udp.Receive(ref remoteEp);

            // Challenge response (0x41)
            if (response.Length >= 9 && response[4] == 0x41)
            {
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

            int offset = 5;
            offset++; // Protocol

            SkipString(response, ref offset); // Name
            string map = ReadString(response, ref offset);
            SkipString(response, ref offset); // Folder
            SkipString(response, ref offset); // Game
            offset += 2; // Steam App ID
            int players = response[offset++];
            int maxPlayers = response[offset++];

            return (players, maxPlayers, map);
        }
        catch (Exception ex)
        {
            WriteLog($"A2S exception: {ex.Message}");
            return null;
        }
    }

    private string ReadString(byte[] data, ref int offset)
    {
        int start = offset;
        while (offset < data.Length && data[offset] != 0)
            offset++;
        var str = Encoding.UTF8.GetString(data, start, offset - start);
        offset++;
        return str;
    }

    private void SkipString(byte[] data, ref int offset)
    {
        while (offset < data.Length && data[offset] != 0)
            offset++;
        offset++;
    }

    private void LoadConfig()
    {
        var configPath = Path.Combine(ModuleDirectory, "config.json");

        if (!File.Exists(configPath))
        {
            _config = new PluginConfig();
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
            WriteLog($"Created default config at {configPath}");
            return;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            _config = JsonSerializer.Deserialize<PluginConfig>(json) ?? new PluginConfig();
            WriteLog($"Loaded config from {configPath}");
        }
        catch (Exception ex)
        {
            _config = new PluginConfig();
            WriteLog($"Config load error: {ex.Message}, using defaults");
        }
    }

    private void WriteLog(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n";
            File.AppendAllText(_logFile, line);
        }
        catch { }
    }
}
