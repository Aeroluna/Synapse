using System.Buffers;
using System.Collections.Concurrent;
using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Synapse.Networking;
using Synapse.Networking.Models;
using Synapse.Server.Clients;
using Synapse.Server.Extras;

namespace Synapse.Server.Services;

public interface IListenerService
{
    public event Action<IClient>? ClientConnected;

    public event Action<IClient, string>? CommandReceived;

    public event Action<IClient, int, int, bool>? LeaderboardRequested;

    public event Action<IClient, ScoreSubmission>? ScoreSubmissionReceived;

    public event Action<IClient>? StatusRequested;

    public ConcurrentDictionary<IClient, byte> Chatters { get; }

    public ConcurrentDictionary<string, IClient> Clients { get; }

    public void AllClients(Func<IClient, Task> action);

    public void BanIp(IClient client);

    public void Blacklist(IClient client, string? reason, DateTime? banTime);

    public void BroadcastChatMessage(
        string id,
        string username,
        string? color,
        MessageType messageType,
        string message);

    public void BroadcastPriorityServerMessage([StructuredMessageTemplate] string message, params object?[] args);

    public void BroadcastServerMessage([StructuredMessageTemplate] string message, params object?[] args);

    public Task RunAsync();

    public Task Stop();

    public void Whitelist(IClient client);
}

public class ListenerService : IListenerService
{
    private readonly IBlacklistService _blacklistService;
    private readonly ILogger<ListenerService> _log;
    private readonly ITimeoutService _timeoutService;

    private readonly AsyncTcpListener _listener;
    private readonly int _maxPlayers;
    private readonly IServiceProvider _provider;
    private readonly IClient _serverClient;

    public ListenerService(
        ILogger<ListenerService> log,
        IConfiguration config,
        ITimeoutService timeoutService,
        IServiceProvider provider,
        IBlacklistService blacklistService,
        IClient serverClient)
    {
        _log = log;
        _timeoutService = timeoutService;
        _provider = provider;
        _blacklistService = blacklistService;
        _serverClient = serverClient;
        int port = config.GetValue<int>("Port");
        _listener = new AsyncTcpListener(port);
        _maxPlayers = config.GetValue<int>("MaxPlayers");
        log.LogInformation("Listening on [{Port}]", port);
    }

    public event Action<IClient>? ClientConnected;

    public event Action<IClient, string>? CommandReceived;

    public event Action<IClient, int, int, bool>? LeaderboardRequested;

    public event Action<IClient, ScoreSubmission>? ScoreSubmissionReceived;

    public event Action<IClient>? StatusRequested;

    public ConcurrentDictionary<IClient, byte> Chatters { get; } = new();

    public ConcurrentDictionary<string, IClient> Clients { get; } = new();

    public Task RunAsync()
    {
        _listener.ClientConnectedCallback = OnClientConnected;
        return _listener.RunAsync();
    }

    public void AllChatters(Func<IClient, Task> action)
    {
        foreach (IClient client in Chatters.Keys)
        {
            try
            {
                _ = action(client);
            }
            catch (Exception e)
            {
                _log.LogError(e, "Exception on {Chatter}", client);
            }
        }
    }

    public void AllClients(Func<IClient, Task> action)
    {
        foreach (IClient client in Clients.Values)
        {
            try
            {
                _ = action(client);
            }
            catch (Exception e)
            {
                _log.LogError(e, "Exception on {Client}", client);
            }
        }

        _ = action(_serverClient);
    }

    public void BanIp(IClient client)
    {
        string id = client.Id;
        if (!Clients.ContainsKey(id))
        {
            return;
        }

        string ip = client.Address.ToString();
        _blacklistService.AddBannedIp(ip);
    }

    public void Blacklist(IClient client, string? reason, DateTime? banTime)
    {
        string id = client.Id;
        if (!Clients.TryRemove(id, out _))
        {
            return;
        }

        string username = client.DisplayUsername;
        _blacklistService.AddBlacklist(id, username, reason, banTime);
        _ = client.Disconnect(DisconnectCode.Banned); // TODO: pass reason / bantime to this
        AllClients(n => n.Send(ClientOpcode.UserBanned, id));
    }

    public void BroadcastChatMessage(string id, string username, string? color, MessageType messageType, string message)
    {
        ChatMessage chatMessage = new(id, username, color, messageType, message);
        AllClients(n => n.SendChatMessage(chatMessage));
    }

    public void BroadcastPriorityServerMessage(string message, params object?[] args)
    {
        AllClients(n => n.SendPriorityServerMessage(message, args));
    }

    public void BroadcastServerMessage(string message, params object?[] args)
    {
        AllClients(n => n.SendServerMessage(message, args));
    }

    /*public void Broadcast(byte[] bytes)
    {
        AllClients(n => n.Send(new ArraySegment<byte>(bytes, 0, bytes.Length)));
    }*/

    public async Task Stop()
    {
        AllClients(n => n.Disconnect(DisconnectCode.ServerClosing));
        _log.LogInformation("Closing...");
        await Task.Delay(2000);
        _listener.Dispose();
    }

    public void Whitelist(IClient client)
    {
        string id = client.Id;
        if (!Clients.ContainsKey(id))
        {
            return;
        }

        string username = client.DisplayUsername;
        _blacklistService.AddWhitelist(id, username);
    }

    private async Task OnClientConnected(AsyncTcpServerClient tcpClient, CancellationToken token)
    {
        ConnectedClient client = ActivatorUtilities.CreateInstance<ConnectedClient>(_provider, tcpClient);

        string ip = client.Address.ToString();

        /*if (RateLimiter.RateLimit(this, 4, 10000, ip))
        {
            await client.Disconnect(DisconnectCode.RateLimited);
            return;
        }*/

        if (CheckMaxConnection(client))
        {
            return;
        }

        if (_blacklistService.BannedIps.ContainsKey(ip))
        {
            await client.Disconnect(DisconnectCode.Banned);
            return;
        }

        client.Authenticated += OnJoined;
        client.Disconnected += OnDisconnected;
        client.ChatJoined += OnChatJoined;
        client.ChatLeft += OnChatLeft;
        client.ChatMessageReceived += OnChatMessageReceived;
        client.CommandReceived += OnCommandReceived;
        client.ScoreSubmissionReceived += OnScoreSubmissionReceived;
        client.LeaderboardRequested += OnLeaderboardRequested;
        client.StatusRequested += OnStatusRequested;
        await client.RunAsync();
        client.Authenticated -= OnJoined;
        client.Disconnected -= OnDisconnected;
        client.ChatJoined -= OnChatJoined;
        client.ChatLeft -= OnChatLeft;
        client.ChatMessageReceived -= OnChatMessageReceived;
        client.CommandReceived -= OnCommandReceived;
        client.ScoreSubmissionReceived -= OnScoreSubmissionReceived;
        client.LeaderboardRequested -= OnLeaderboardRequested;
        client.StatusRequested -= OnStatusRequested;
    }

    private void OnChatJoined(ConnectedClient client)
    {
        Chatters.TryAdd(client, 0);
        _log.LogInformation("{Username} has joined", client.DisplayUsername);
        using PacketBuilder packetBuilder = new((byte)ClientOpcode.UserJoin);
        packetBuilder.Write(client.DisplayUsername);
        ReadOnlySequence<byte> bytes = packetBuilder.ToBytes();
        AllChatters(n => n.Send(bytes));
        ////BroadcastServerMessage("{Username} has joined.", client.DisplayUsername);
        BroadcastPlayerCount();
    }

    private void OnChatLeft(ConnectedClient client)
    {
        Chatters.TryRemove(client, out _);
        _log.LogInformation("{Username} has left", client.DisplayUsername);
        using PacketBuilder packetBuilder = new((byte)ClientOpcode.UserLeave);
        packetBuilder.Write(client.DisplayUsername);
        ReadOnlySequence<byte> bytes = packetBuilder.ToBytes();
        AllChatters(n => n.Send(bytes));
        ////BroadcastServerMessage("{Username} has left.", client.DisplayUsername);
        BroadcastPlayerCount();
    }

    private void OnChatMessageReceived(ConnectedClient connectedClient, string message)
    {
        string censored = message.Sanitize().Trim();
        if (string.IsNullOrWhiteSpace(censored))
        {
            return;
        }

        ////_log.LogInformation("[{Client}]{Extra} {Message}", connectedClient, censored == message ? string.Empty : " (censored)", message);
        BroadcastChatMessage(
            connectedClient.Id,
            connectedClient.DisplayUsername,
            connectedClient.GetColor(),
            MessageType.Say,
            censored);
    }

    private void OnCommandReceived(ConnectedClient client, string command)
    {
        CommandReceived?.Invoke(client, command);
    }

    private void OnDisconnected(ConnectedClient client, string reason)
    {
        Clients.Remove(client.Id, out _);
    }

    private void OnJoined(ConnectedClient client)
    {
        if (Clients.TryGetValue(client.Id, out IClient? existing))
        {
            _ = existing.Disconnect(DisconnectCode.DuplicateConnection);
        }

        if (CheckMaxConnection(client))
        {
            return;
        }

        Clients.TryAdd(client.Id, client);
        _log.LogInformation("{Client} ({GameVersion}) connected", client, client.GameVersion);

        ClientConnected?.Invoke(client);
    }

    private void OnLeaderboardRequested(ConnectedClient connectedClient, int index, int division, bool showEliminated)
    {
        try
        {
            LeaderboardRequested?.Invoke(connectedClient, index, division, showEliminated);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Error receiving leaderboard request");
        }
    }

    private void OnScoreSubmissionReceived(ConnectedClient connectedClient, ScoreSubmission scoreSubmission)
    {
        ScoreSubmissionReceived?.Invoke(connectedClient, scoreSubmission);
    }

    private void OnStatusRequested(ConnectedClient connectedClient)
    {
        StatusRequested?.Invoke(connectedClient);
    }

    private bool CheckMaxConnection(ConnectedClient client)
    {
        if (Clients.Count + 1 > _maxPlayers)
        {
            return false;
        }

        _ = client.Disconnect(DisconnectCode.MaximumConnections);
        return true;
    }

    private void BroadcastPlayerCount()
    {
        _timeoutService.Timeout(
            () =>
            {
                using PacketBuilder packetBuilder = new((byte)ClientOpcode.PlayerCount);
                packetBuilder.Write((ushort)Chatters.Count);
                packetBuilder.Write((ushort)Clients.Count);
                ReadOnlySequence<byte> bytes = packetBuilder.ToBytes();
                AllClients(n => n.Send(bytes));
            },
            4000);
    }
}
