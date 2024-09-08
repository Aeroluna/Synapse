﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Synapse.Networking;

public class AsyncTcpListener : IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private readonly int _port;

    private bool _active;

    public AsyncTcpListener(int port)
    {
        _port = port;
    }

    public Func<AsyncTcpServerClient, CancellationToken, Task>? ClientConnectedCallback { get; set; }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
    }

    public async Task RunAsync()
    {
        if (_active)
        {
            throw new InvalidOperationException("Listener already active.");
        }

        _active = true;
        CancellationToken token = _cancellationTokenSource.Token;
        token.ThrowIfCancellationRequested();
        IPEndPoint endPoint = new(IPAddress.Any, _port);
        using Socket socket = new(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(endPoint);
        socket.Listen((int)SocketOptionName.MaxConnections);
        while (!token.IsCancellationRequested)
        {
            _ = ConnectClient(await socket.AcceptAsync(), token);
        }
    }

    private async Task ConnectClient(Socket socket, CancellationToken token)
    {
        try
        {
            if (ClientConnectedCallback != null)
            {
                using AsyncTcpServerClient client = new(socket, token);
                await ClientConnectedCallback(client, token);
            }
        }
        finally
        {
            socket.Dispose();
        }
    }
}
