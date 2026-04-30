using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Newtonsoft.Json;
using SiraUtil.Logging;
using Synapse.Extras;
using Synapse.Networking;
using Synapse.Networking.Models;
#if LATEST
using OculusStudios.Platform.Core;
#endif

namespace Synapse.Managers;

internal enum ConnectingStage
{
    Connecting,
    Authenticating,
    ReceivingData,
    Timeout,
    Refused
}

internal class NetworkManager : IDisposable
{
    private const int AUTH_SUBMISSION_INTERVAL = 2000;
    private const int AUTH_SUBMISSION_ATTEMPTS = 4;

    private readonly Config _config;
    private readonly ListingManager _listingManager;
    private readonly SiraLog _log;
#if LATEST
    private readonly IPlatform _platformUserModel;
#else
    private readonly IPlatformUserModel _platformUserModel;
#endif

    private readonly List<byte[]> _queuedPackets = [];
    private readonly Task<AuthenticationToken?> _tokenTask;

    private string _address = string.Empty;
    private AsyncTcpClient? _client;
    private bool _authenticated;

    [UsedImplicitly]
    private NetworkManager(
        SiraLog log,
        Config config,
#if LATEST
        IPlatform platformUserModel,
#else
        IPlatformUserModel platformUserModel,
#endif
        ListingManager listingManager)
    {
        _log = log;
        _config = config;
        _platformUserModel = platformUserModel;
        _listingManager = listingManager;
        _tokenTask = GetToken();
    }

    internal event Action<ChatMessage>? ChatReceived;

    internal event Action<bool, string>? JoinLeaveReceived;

    ////internal event Action<FailReason>? ConnectionFailed;

    internal event Action? Closed;

    internal event Action<ConnectingStage, int>? Connecting;

    internal event Action<string>? Disconnected;

    internal event Action<string>? IntroUrlUpdated;

    internal event Action<string>? FinishUrlUpdated;

    internal event Action<float>? IntroStartTimeUpdated;

    internal event Action<int>? InvalidateScores;

    internal event Action<LeaderboardScores>? LeaderboardReceived;

    internal event Action<int, Map>? MapUpdated;

    internal event Action<string>? MotdUpdated;

    internal event Action<PlayerScore?>? PlayerScoreUpdated;

    internal event Action? EliminatedUpdated;

    internal event Action<int, int>? PlayerCountUpdated;

    internal event Action<float, float>? PongReceived;

    internal event Action<IStageStatus>? StageUpdated;

    internal event Action<float>? StartTimeUpdated;

    internal event Action? StopLevelReceived;

    internal event Action<string>? UserBanned;

    internal Status Status { get; private set; } = new();

    internal ConcurrentDictionary<int, int> AcknowledgedScores { get; } = new();

    internal bool Running => _client != null;

    public void Dispose()
    {
        _ = Disconnect(DisconnectCode.ClientDisposed);
    }

    public async Task Send(byte[] data, CancellationToken cancellationToken = default)
    {
        if (_client is not { IsConnected: true })
        {
            _log.Warn("Client not connected! Delaying sending packet");
            _queuedPackets.Add(data);
            return;
        }

        await _client.Send(data, cancellationToken);
    }

    public async Task Send(ServerOpcode opcode, bool value)
    {
        using PacketBuilder packetBuilder = new((byte)opcode);
        packetBuilder.Write(value);
        await Send(packetBuilder.ToBytes());
    }

    public async Task Send(ServerOpcode opcode, int value)
    {
        using PacketBuilder packetBuilder = new((byte)opcode);
        packetBuilder.Write(value);
        await Send(packetBuilder.ToBytes());
    }

    public async Task Send(ServerOpcode opcode, float value)
    {
        using PacketBuilder packetBuilder = new((byte)opcode);
        packetBuilder.Write(value);
        await Send(packetBuilder.ToBytes());
    }

    public async Task Send(ServerOpcode opcode, string value)
    {
        using PacketBuilder packetBuilder = new((byte)opcode);
        packetBuilder.Write(value);
        await Send(packetBuilder.ToBytes());
    }

    internal Task Disconnect(DisconnectCode code, Exception? exception = null, bool notify = true)
    {
        return Disconnect(code.ToReason(), exception, notify ? code : null);
    }

    internal async Task Disconnect(string reason, Exception? exception = null, DisconnectCode? notifyCode = null)
    {
        if (_client == null)
        {
            return;
        }

        if (exception != null)
        {
            _log.Error($"{reason}\n{exception}");
        }
        else
        {
            _log.Debug(reason);
        }

        AsyncTcpClient client = _client;
        _client = null;
        Disconnected?.Invoke(reason);

        await client.Disconnect(notifyCode);
    }

    internal async Task RunAsync()
    {
        if (_client != null)
        {
            _log.Error("Client still running, disposing");
            _client.Dispose();
        }

        string? stringAddress = _listingManager.Listing?.IpAddress;
        if (stringAddress == null)
        {
            _log.Error("No IP found");
            return;
        }

        _authenticated = false;
        Status = new Status();
        int portIdx = stringAddress.LastIndexOf(':');
        IPAddress address = IPAddress.Parse(stringAddress.Substring(0, portIdx));
        int port = int.Parse(stringAddress.Substring(portIdx + 1));
        _address = $"{address}:{port}";
        using AsyncTcpLocalClient client = new(address, port, 3);
        _log.Info($"Connecting to {_address}");
        client.Message += OnMessageReceived;
        client.ConnectedCallback = OnConnected;
        client.ReceivedCallback = OnReceived;
        _client = client;
        try
        {
            await client.RunAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (AsyncTcpFailedAfterRetriesException e)
        {
            await Disconnect($"Connection failed after {e.ReconnectTries} tries", e.InnerException);
        }
        catch (AsyncTcpSocketException e)
        {
            await Disconnect(DisconnectCode.ConnectionClosedUnexpectedly, e, false);
        }
        catch (Exception e)
        {
            await Disconnect(DisconnectCode.UnexpectedException, e, false);
        }

        client.Message -= OnMessageReceived;
        client.ConnectedCallback = null;
        client.ReceivedCallback = null;
    }

    private async Task<AuthenticationToken?> GetToken()
    {
#if LATEST
        UserInfo.Platform platform = _platformUserModel.key switch
        {
            "steam" => UserInfo.Platform.Steam,
            "oculus" => UserInfo.Platform.Oculus,
            _ => UserInfo.Platform.Test
        };
        UserInfo userInfo = new(
            platform,
            _platformUserModel.user.userId.ToString(),
            _platformUserModel.user.displayName);
#else
#if !V1_29_1
        UserInfo? userInfo = await _platformUserModel.GetUserInfo(CancellationToken.None);
#else
        UserInfo? userInfo = await _platformUserModel.GetUserInfo();
#endif
        if (userInfo == null)
        {
            return null;
        }
#endif

        PlatformAuthenticationTokenProvider provider = new(_platformUserModel, userInfo);
        return await provider.GetAuthenticationToken();
    }

    private async Task OnConnected(CancellationToken cancelToken)
    {
        try
        {
            if (_client is not { IsConnected: true })
            {
                throw new InvalidOperationException("Client not connected.");
            }

            _log.Debug($"Successfully connected to {_address}");
            Connecting?.Invoke(ConnectingStage.Authenticating, -1);

            _ = SubmitAuth(cancelToken);
        }
        catch (Exception e)
        {
            await Disconnect(DisconnectCode.UnexpectedException, e);
        }
    }

    private async Task SubmitAuth(CancellationToken cancelToken)
    {
        try
        {
            if (_client is not { IsConnected: true })
            {
                throw new InvalidOperationException("Client not connected.");
            }

            AuthenticationToken? authTokenResult = await _tokenTask;
            if (authTokenResult == null)
            {
                await Disconnect(DisconnectCode.Unauthenticated);
                return;
            }

            AuthenticationToken authToken = authTokenResult.Value;
            using PacketBuilder packetBuilder = new((byte)ServerOpcode.Authentication);
            packetBuilder.Write(authToken.userId);
            packetBuilder.Write(authToken.userName);
            packetBuilder.Write((byte)authToken.platform);
            packetBuilder.Write(authToken.sessionToken);
            packetBuilder.Write(Plugin.GameVersion);
            packetBuilder.Write(
                _listingManager.Listing?.Guid ?? throw new InvalidOperationException("No listing loaded."));
            byte[] bytes = packetBuilder.ToBytes();

            int submissionTry = 0;
            while (!cancelToken.IsCancellationRequested)
            {
                await _client.Send(bytes, cancelToken);
                await Task.Delay(AUTH_SUBMISSION_INTERVAL, cancelToken);
                cancelToken.ThrowIfCancellationRequested();
                if (++submissionTry >= AUTH_SUBMISSION_ATTEMPTS)
                {
                    break;
                }

                if (!_authenticated)
                {
                    continue;
                }

                byte[][] queued = _queuedPackets.ToArray();
                _queuedPackets.Clear();
                foreach (byte[] data in queued)
                {
                    _ = _client.Send(data, cancelToken);
                }

                return;
            }

            throw new InvalidOperationException($"Failed to authenticate, attempted [{submissionTry}] times.");
        }
        catch (Exception e)
        {
            await Disconnect(DisconnectCode.UnexpectedException, e);
        }
    }

    private void OnMessageReceived(object _, AsyncTcpMessageEventArgs args)
    {
        if (args.Exception != null)
        {
            _log.Error($"{args.Message}\n{args.Exception}");
        }
        else
        {
            _log.Debug(args.Message);
        }

        switch (args.Message)
        {
            case Message.Connecting:
                Connecting?.Invoke(ConnectingStage.Connecting, args.ReconnectTries);
                break;

            case Message.ConnectionFailed:
                switch (args.Exception)
                {
                    case AsyncTcpConnectTimeoutException:
                        Connecting?.Invoke(ConnectingStage.Timeout, args.ReconnectTries);
                        break;

                    case AsyncTcpConnectFailedException:
                    case AsyncTcpMessageException:
                        Connecting?.Invoke(ConnectingStage.Refused, args.ReconnectTries);
                        break;
                }

                break;

            case Message.ConnectionClosed:
                Closed?.Invoke();
                break;

            case Message.PacketException:
                _log.Error("Exception while processing packet");
                if (args.Exception != null)
                {
                    _log.Error(args.Exception.ToString());
                }

                break;
        }
    }

    private async Task OnReceived(byte opcode, BinaryReader reader, CancellationToken cancelToken)
    {
        switch ((ClientOpcode)opcode)
        {
            case ClientOpcode.Authenticated:
            {
                _log.Debug($"Authenticated {_address}");
                _authenticated = true;
                Connecting?.Invoke(ConnectingStage.ReceivingData, -1);
                if (_config.JoinChat ?? false)
                {
                    _ = Send(ServerOpcode.SetChatter, true);
                }

                if (_config.LastEvent.Division != null)
                {
                    _ = Send(ServerOpcode.SetDivision, _config.LastEvent.Division.Value);
                }

                break;
            }

            case ClientOpcode.Disconnect:
            {
                DisconnectCode disconnectCode = (DisconnectCode)reader.ReadByte();
                if (disconnectCode == DisconnectCode.ListingMismatch)
                {
                    _listingManager.Clear();
                }

                await Disconnect($"Disconnected by server\n{disconnectCode.ToReason()}");

                break;
            }

            case ClientOpcode.RefusedPacket:
            {
                string refusal = reader.ReadString();
                _log.Warn($"Packet refused by server ({refusal})");

                break;
            }

            case ClientOpcode.PlayerCount:
                ushort chatterCount = reader.ReadUInt16();
                ushort totalCount = reader.ReadUInt16();
                PlayerCountUpdated?.Invoke(chatterCount, totalCount);
                break;

            case ClientOpcode.Ping:
                float clientTime = reader.ReadSingle();
                float serverTime = reader.ReadSingle();
                PongReceived?.Invoke(clientTime, serverTime);
                break;

            case ClientOpcode.Status:
            {
                string fullStatus = reader.ReadString();
                Status status = JsonConvert.DeserializeObject<Status>(fullStatus, JsonSettings.Settings)!;
                Status lastStatus = Status;
                Status = status;

                if (lastStatus.Motd != status.Motd)
                {
                    MotdUpdated?.Invoke(status.Motd);
                }

                if (status.Stage.GetType() != lastStatus.Stage.GetType())
                {
                    StageUpdated?.Invoke(status.Stage);
                }

                switch (status.Stage)
                {
                    case IntroStatus introStatus:
                        if (lastStatus.Stage is not IntroStatus lastIntroStatus)
                        {
                            lastIntroStatus = new IntroStatus();
                        }

                        if (lastIntroStatus.Url != introStatus.Url)
                        {
                            IntroUrlUpdated?.Invoke(introStatus.Url);
                        }

                        if (Math.Abs(lastIntroStatus.StartTime - introStatus.StartTime) > 0.001)
                        {
                            IntroStartTimeUpdated?.Invoke(introStatus.StartTime);
                        }

                        break;

                    case PlayStatus playStatus:
                        if (lastStatus.Stage is not PlayStatus lastPlayStatus)
                        {
                            lastPlayStatus = new PlayStatus();
                        }

                        if (lastPlayStatus.Index != playStatus.Index)
                        {
                            MapUpdated?.Invoke(playStatus.Index, playStatus.Map);
                        }

                        // i hate floats
                        if (Math.Abs(lastPlayStatus.StartTime - playStatus.StartTime) > 0.001)
                        {
                            StartTimeUpdated?.Invoke(playStatus.StartTime);
                        }

                        if (lastPlayStatus.PlayerScore != playStatus.PlayerScore)
                        {
                            PlayerScoreUpdated?.Invoke(playStatus.PlayerScore);
                        }

                        if (lastPlayStatus.Eliminated != playStatus.Eliminated)
                        {
                            EliminatedUpdated?.Invoke();
                        }

                        break;

                    case FinishStatus finishStatus:
                        if (lastStatus.Stage is not FinishStatus lastFinishStatus)
                        {
                            lastFinishStatus = new FinishStatus();
                        }

                        if (lastFinishStatus.Url != finishStatus.Url)
                        {
                            FinishUrlUpdated?.Invoke(finishStatus.Url);
                        }

                        break;
                }

                break;
            }

            case ClientOpcode.ChatMessage:
            {
                string message = reader.ReadString();
                ChatReceived?.Invoke(JsonConvert.DeserializeObject<ChatMessage>(message, JsonSettings.Settings));

                break;
            }

            case ClientOpcode.UserJoin:
            {
                string username = reader.ReadString();
                JoinLeaveReceived?.Invoke(true, username);

                break;
            }

            case ClientOpcode.UserLeave:
            {
                string username = reader.ReadString();
                JoinLeaveReceived?.Invoke(false, username);

                break;
            }

            case ClientOpcode.UserBanned:
            {
                string message = reader.ReadString();
                UserBanned?.Invoke(message);

                break;
            }

            case ClientOpcode.AcknowledgeScore:
            {
                byte index = reader.ReadByte();
                int score = reader.ReadInt32();
                AcknowledgedScores[index] = score;

                break;
            }

            case ClientOpcode.InvalidateScores:
            {
                byte index = reader.ReadByte();
                InvalidateScores?.Invoke(index);

                break;
            }

            case ClientOpcode.LeaderboardScores:
            {
                string message = reader.ReadString();
                LeaderboardReceived?.Invoke(
                    JsonConvert.DeserializeObject<LeaderboardScores>(message, JsonSettings.Settings)!);

                break;
            }

            case ClientOpcode.StopLevel:
                StopLevelReceived?.Invoke();
                break;

            default:
                _log.Warn($"Unhandled opcode: ({opcode})");
                return;
        }
    }
}
