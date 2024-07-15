﻿using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using JetBrains.Annotations;
using SiraUtil.Logging;
using Synapse.Models;

namespace Synapse.Managers;

internal sealed class MessageManager : IDisposable
{
    private readonly NetworkManager _networkManager;
    private readonly PingManager _pingManager;

    [UsedImplicitly]
    private MessageManager(SiraLog log, NetworkManager networkManager, PingManager pingManager)
    {
        _networkManager = networkManager;
        _pingManager = pingManager;
        networkManager.Closed += OnClosed;
        networkManager.Connecting += OnConnecting;
        networkManager.ChatReceived += OnChatMessageReceived;
        networkManager.MotdUpdated += OnMotdUpdated;
        pingManager.Finished += RelaySystemMessage;
    }

    internal event Action<ChatMessage>? MessageReceived;

    public void Dispose()
    {
        _networkManager.Closed -= OnClosed;
        _networkManager.Connecting -= OnConnecting;
        _networkManager.ChatReceived -= OnChatMessageReceived;
        _networkManager.MotdUpdated -= OnMotdUpdated;
        _pingManager.Finished -= RelaySystemMessage;
    }

    internal void RefreshMotd()
    {
        OnMotdUpdated(_networkManager.Status.Motd);
    }

    internal void SendMessage(string message)
    {
        _ = SendMessageAsync(message);
    }

    internal async Task SendMessageAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (message.StartsWith("/"))
        {
            if (message.Length > 1)
            {
                message = message.Substring(1);
                if (message == "ping")
                {
                    _pingManager.Start();
                    await _networkManager.Send(ServerOpcode.Ping);
                }
                else
                {
                    await _networkManager.Send(ServerOpcode.Command, message);
                }
            }
        }
        else
        {
            await _networkManager.Send(ServerOpcode.ChatMessage, message);
        }
    }

    private void OnChatMessageReceived(ChatMessage messages)
    {
        MessageReceived?.Invoke(messages);
    }

    private void OnClosed(ClosedReason closedReason)
    {
        RelaySystemMessage("Connection closed unexpectedly, reconnecting...");
    }

    private void OnConnecting(Stage stage, int retries)
    {
        switch (stage)
        {
            case Stage.Failed:
            case Stage.Connecting:
                return;
        }

        string text = stage switch
        {
            ////Stage.Connecting => "Connecting...",
            Stage.Authenticating => "Authenticating...",
            Stage.ReceivingData => "Receiving data...",
            Stage.Timeout => "Connection timed out, retrying...",
            Stage.Refused => "Connection refused, retrying...",
            _ => $"{(SocketError)stage}, retrying..."
        };

        if (retries > 0)
        {
            text += $" ({retries + 1})";
        }

        RelaySystemMessage(text);
    }

    private void OnMotdUpdated(string message)
    {
        MessageReceived?.Invoke(new ChatMessage(string.Empty, string.Empty, null, MessageType.System, message));
    }

    private void RelaySystemMessage(string message)
    {
        ////message = $"<color=\"yellow\">{message}</color>";
        MessageReceived?.Invoke(new ChatMessage(string.Empty, string.Empty, "yellow", MessageType.System, message));
    }
}
