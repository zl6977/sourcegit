using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SourceGit.Contracts;

// Faults acknowledged closes after the real server has executed them, preserving
// the TCP channel so online release failures can be distinguished from disconnects.
internal sealed class LeaseReplyProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, byte> _closes = new();
    private readonly ConcurrentDictionary<string, byte> _stats = new();
    internal string? StatError { get; set; }
    private readonly Task _pump;
    private TcpClient? _gui, _server;
    private string? _handshakeId;
    private int _failReplies;
    internal ConcurrentQueue<string> ClosedLeases { get; } = new();
    internal int FailReplies { set => Volatile.Write(ref _failReplies, value); }
    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    internal LeaseReplyProxy(int serverPort, int failReplies, string? instanceOverride = null)
    {
        _failReplies = failReplies;
        _listener.Start();
        _pump = PumpAsync(serverPort, instanceOverride);
    }
    private async Task PumpAsync(int serverPort, string? instanceOverride)
    {
        try
        {
            _gui = await _listener.AcceptTcpClientAsync(_lifetime.Token);
            _server = new TcpClient();
            await _server.ConnectAsync(IPAddress.Loopback, serverPort, _lifetime.Token);
            using var toServer = new SemaphoreSlim(1);
            using var toGui = new SemaphoreSlim(1);
            await Task.WhenAll(RequestsAsync(), RepliesAsync());

            async Task RequestsAsync()
            {
                while (await Protocol.ReadAsync(_gui.GetStream(), _lifetime.Token) is { } request)
                {
                    if (request.Method == "Handshake") Volatile.Write(ref _handshakeId, request.Id);
                    if (request.Method == "File" && request.Data.GetProperty("action").GetString() == "stat" && StatError != null)
                        _stats[request.Id] = 0;
                    if (request.Method == "CloseRepository")
                    {
                        _closes[request.Id] = 0;
                        ClosedLeases.Enqueue(request.Data.Deserialize(RemoteJson.Default.IdRequest)!.Id);
                    }
                    await Protocol.WriteAsync(_server.GetStream(), request, toServer, _lifetime.Token);
                }
            }
            async Task RepliesAsync()
            {
                while (await Protocol.ReadAsync(_server.GetStream(), _lifetime.Token) is { } response)
                {
                    if (!response.Event && _stats.TryRemove(response.Id, out _))
                        response = response with { Data = Protocol.Null, Error = StatError };
                    if (!response.Event && response.Id == Volatile.Read(ref _handshakeId) && instanceOverride != null && response.Error == null)
                        response = response with { Data = Protocol.Json(response.Data.Deserialize(RemoteJson.Default.HandshakeResult)! with { InstanceId = instanceOverride }, RemoteJson.Default.HandshakeResult) };
                    if (!response.Event && _closes.TryRemove(response.Id, out _) && Volatile.Read(ref _failReplies) > 0 && Interlocked.Decrement(ref _failReplies) >= 0)
                        response = response with { Data = Protocol.Null, Error = "Injected close acknowledgement failure after execution" };
                    await Protocol.WriteAsync(_gui.GetStream(), response, toGui, _lifetime.Token);
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
