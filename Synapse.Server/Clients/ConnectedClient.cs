﻿using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Synapse.Networking;
using Synapse.Networking.Models;
using Synapse.Server.Extras;
using Synapse.Server.Models;
using Synapse.Server.Services;

namespace Synapse.Server.Clients;

public class ConnectedClient(
    ILogger<ConnectedClient> log,
    IBlacklistService blacklistService,
    ITimeService timeService,
    IRoleService roleService,
    IAuthService authService,
    IListingService listingService,
    AsyncTcpServerClient client)
    : IClient
{
    private Authentication _authentication = Authentication.None;
    private bool _disposing;

    public event Action<ConnectedClient>? ChatJoined;

    public event Action<ConnectedClient>? ChatLeft;

    public event Action<ConnectedClient, string>? ChatMessageReceived;

    public event Action<ConnectedClient, string>? CommandReceived;

    public event Action<ConnectedClient>? Connected;

    public event Action<ConnectedClient, string>? Disconnected;

    public event Action<ConnectedClient, int>? LeaderboardRequested;

    public event Action<ConnectedClient, ScoreSubmission>? ScoreSubmissionReceived;

    internal enum Authentication
    {
        None,
        Authenticated
    }

    public IPAddress Address { get; } =
        (client.Socket.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv4() ?? IPAddress.None;

    public bool Chatter { get; private set; }

    public string Id { get; private set; } = "N/A";

    public string Username { get; private set; } = "N/A";

    public async Task RunAsync()
    {
        log.LogInformation("Client connecting from [{Address}]", Address);
        _ = TimeoutAuthentication();
        client.ReceivedCallback = OnReceived;
        try
        {
            await client.RunAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (AsyncTcpSocketException e)
        {
            log.LogError(e, "Connection from [{Address}] closed unexpectedly", Address);
            await Disconnect("Connection closed unexpectedly", false);
        }
        catch (Exception e)
        {
            log.LogCritical(e, "An unexpected exception from [{Address}] has occurred", Address);
            await Disconnect("An unexpected error has occured", false);
        }
    }

    public async Task Disconnect(string reason, bool local = true)
    {
        try
        {
            if (_disposing)
            {
                return;
            }

            _disposing = true;

            if (_authentication == Authentication.Authenticated)
            {
                Disconnected?.Invoke(this, reason);
                if (Chatter)
                {
                    ChatLeft?.Invoke(this);
                }
            }
            else
            {
                log.LogInformation("Closed connection from [{Address}] ({Reason})", Address, reason);
            }

            if (local && client.IsConnected)
            {
                await SendString(ClientOpcode.Disconnected, reason);
            }

            client.Dispose();
        }
        catch (Exception e)
        {
            log.LogError(e, "Exception while disconnecting client [{Client}]", this);
        }
    }

    public string? GetColor()
    {
        return roleService.TryGetRoleData(Id, out RoleService.RoleData? roleData) ? roleData.Color : null;
    }

    public int GetImmunity()
    {
        return roleService.TryGetRoleData(Id, out RoleService.RoleData? roleData) ? roleData.Immunity : 0;
    }

    public bool HasPermission(Permission permission)
    {
        if (roleService.TryGetRoleData(Id, out RoleService.RoleData? roleData))
        {
            return (roleData.Permission & permission) > 0;
        }

        return false;
    }

    public async Task SendChatMessage(ChatMessage message)
    {
        if (!Chatter)
        {
            return;
        }

        await SendString(ClientOpcode.ChatMessage, JsonSerializer.Serialize(message, JsonUtils.Settings));
    }

    public async Task SendOpcode(ClientOpcode opcode)
    {
        using PacketBuilder packetBuilder = new((byte)opcode);
        await client.Send(packetBuilder.ToArray());
    }

    public async Task SendRefusal(string reason)
    {
        if (_authentication == Authentication.Authenticated)
        {
            log.LogInformation("Refused packet from [{Username}] ({Reason})", Username, reason);
        }

        using PacketBuilder packetBuilder = new((byte)ClientOpcode.RefusedPacket);
        packetBuilder.Write(reason);
        await client.Send(packetBuilder.ToArray());
    }

    public async Task SendServerMessage(string message, params object?[] args)
    {
        await SendString(
            ClientOpcode.ChatMessage,
            JsonSerializer.Serialize(
                new ChatMessage(
                    string.Empty,
                    string.Empty,
                    "yellow",
                    MessageType.System,
                    NamedFormatter.Format(message, args)),
                JsonUtils.Settings));
    }

    public async Task SendString(ClientOpcode opcode, string value)
    {
        using PacketBuilder packetBuilder = new((byte)opcode);
        packetBuilder.Write(value);
        await client.Send(packetBuilder.ToArray());
    }

    public override string ToString()
    {
        return $"({Id}) {Username}";
    }

    private async Task OnReceived(byte byteOpcode, BinaryReader reader, CancellationToken cancelToken)
    {
        if (RateLimiter.RateLimit(this, 100, 10000))
        {
            await SendRefusal("Too many packets");
            return;
        }

        ServerOpcode opcode = (ServerOpcode)byteOpcode;

        switch (opcode)
        {
            case ServerOpcode.Disconnect:
                await Disconnect("Disconnect by user", false);

                return;
        }

        switch (_authentication)
        {
            case Authentication.None:
                if (opcode != ServerOpcode.Authentication)
                {
                    await SendRefusal($"Invalid opcode ({opcode}), must send authentication first");
                    break;
                }

                string id = reader.ReadString();
                string username = reader.ReadString();
                Platform platform = (Platform)reader.ReadByte();
                string token = reader.ReadString();
                string listing = reader.ReadString();

                // we'll just make sure nothing is too sus
                if (string.IsNullOrWhiteSpace(id) ||
                    id.Any(n => !char.IsDigit(n)) ||
                    string.IsNullOrWhiteSpace(username) ||
                    platform > Platform.Steam ||
                    string.IsNullOrWhiteSpace(token) ||
                    string.IsNullOrWhiteSpace(listing))
                {
                    await Disconnect("Invalid parameters");
                    break;
                }

                if (platform != Platform.Test && listingService.Listing.Guid != listing)
                {
                    await Disconnect("Listing mismatch, try restarting your game");
                    break;
                }

                if (!await authService.Authenticate(token, platform, id))
                {
                    await Disconnect("Authentication failed, try restarting your game");
                    break;
                }

                Id = $"{id}_{platform}";

                if (!blacklistService.Whitelist?.ContainsKey(Id) ?? false)
                {
                    log.LogInformation("[({Id}) {Username}] was not whitelisted", Id, username);
                    await Disconnect("Not whitelisted");
                    break;
                }

                if (blacklistService.Blacklist.ContainsKey(Id))
                {
                    log.LogInformation("[({Id}) {Username}] was blacklisted", Id, username);
                    await Disconnect("Banned");
                    break;
                }


                _authentication = Authentication.Authenticated;
                Username = username;

                await SendOpcode(ClientOpcode.Authenticated);

                Connected?.Invoke(this);

                break;

            default:
                switch (opcode)
                {
                    case ServerOpcode.Authentication:
                        await SendRefusal("Already authenticated");
                        break;

                    case ServerOpcode.Ping:
                    {
                        float clientTime = reader.ReadSingle();
                        using PacketBuilder packetBuilder = new((byte)ClientOpcode.Ping);
                        packetBuilder.Write(clientTime);
                        packetBuilder.Write(timeService.Time);
                        await client.Send(packetBuilder.ToArray(), cancelToken);

                        break;
                    }

                    case ServerOpcode.SetChatter:
                        bool chatter = reader.ReadBoolean();
                        if (RateLimiter.RateLimit(this, 4, 5000, ServerOpcode.SetChatter.ToString()))
                        {
                            await SendRefusal("Too many requests");
                            break;
                        }

                        switch (Chatter)
                        {
                            case false when chatter:
                                ChatJoined?.Invoke(this);
                                break;
                            case true when !chatter:
                                ChatLeft?.Invoke(this);
                                break;
                        }

                        Chatter = chatter;
                        break;

                    case ServerOpcode.ChatMessage:
                        string message = reader.ReadString();
                        if (!Chatter)
                        {
                            break;
                        }

                        if (RateLimiter.RateLimit(this, 20, 10000, ServerOpcode.SetChatter.ToString()))
                        {
                            await SendServerMessage("Too many messages, slow down!");
                            break;
                        }

                        ChatMessageReceived?.Invoke(this, message);
                        break;

                    case ServerOpcode.Command:
                        string command = reader.ReadString();
                        if (command.Length > 0)
                        {
                            CommandReceived?.Invoke(this, command);
                        }

                        break;

                    case ServerOpcode.ScoreSubmission:
                        string scoreSubmission = reader.ReadString();
                        ScoreSubmission? score = JsonSerializer.Deserialize<ScoreSubmission>(
                            scoreSubmission,
                            JsonUtils.Settings);
                        if (score != null)
                        {
                            ScoreSubmissionReceived?.Invoke(this, score);
                        }
                        else
                        {
                            await SendRefusal("Invalid score submission");
                        }

                        break;

                    case ServerOpcode.LeaderboardRequest:
                        int index = reader.ReadInt32();
                        LeaderboardRequested?.Invoke(this, index);
                        break;

                    default:
                        throw new InvalidOperationException("Invalid opcode");
                }

                break;
        }
    }

    private async Task TimeoutAuthentication()
    {
        await Task.Delay(20 * 1000);
        if (_authentication == Authentication.None)
        {
            await Disconnect("Authentication timed out");
        }
    }
}
