using System.Net;
using System.Net.Sockets;
using SourceGit.Contracts;

// Drops the response after the real server received the request, without replaying it.
sealed class DropReplyProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _pump;
    private TcpClient? _gui, _server;
    private string? _droppedId;
    private readonly bool _dropCompletion;
    private readonly bool _dropCancellation;
    private int _writeRequests;
    public int WriteRequests => Volatile.Read(ref _writeRequests);
    public TaskCompletionSource Dropped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public DropReplyProxy(string host, int port, string method, bool dropCompletion = false, bool dropCancellation = false)
    {
        _dropCompletion = dropCompletion;
        _dropCancellation = dropCancellation;
        _listener.Start();
        _pump = PumpAsync(host, port, method);
    }

    private async Task PumpAsync(string host, int port, string method)
    {
        try
        {
            _gui = await _listener.AcceptTcpClientAsync(_lifetime.Token);
            _server = new TcpClient();
            await _server.ConnectAsync(host, port, _lifetime.Token);
            using var toServer = new SemaphoreSlim(1);
            using var toGui = new SemaphoreSlim(1);
            await Task.WhenAll(ForwardRequests(), ForwardReplies());

            async Task ForwardRequests()
            {
                while (await Protocol.ReadAsync(_gui.GetStream(), _lifetime.Token) is { } message)
                {
                    if (_dropCancellation && message.Method == "CancelRequest") continue;
                    if (message.Method is "ExecuteGit" or "ExecuteCustomCommand" or "File") Interlocked.Increment(ref _writeRequests);
                    if (message.Method == method) Volatile.Write(ref _droppedId, message.Id);
                    await Protocol.WriteAsync(_server.GetStream(), message, toServer, _lifetime.Token);
                }
            }
            async Task ForwardReplies()
            {
                while (await Protocol.ReadAsync(_server.GetStream(), _lifetime.Token) is { } message)
                {
                    if (_dropCompletion && message.Event && message.Method == "OperationCompleted") continue;
                    if (!message.Event && message.Id == Volatile.Read(ref _droppedId))
                    {
                        _gui.Close(); _server.Close();
                        Dropped.TrySetResult();
                        return;
                    }
                    await Protocol.WriteAsync(_gui.GetStream(), message, toGui, _lifetime.Token);
                }
            }
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel(); _listener.Stop(); _gui?.Dispose(); _server?.Dispose();
        await _pump;
        _lifetime.Dispose();
    }
}
