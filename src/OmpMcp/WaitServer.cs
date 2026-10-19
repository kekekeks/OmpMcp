using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmpMcp.Rpc;

namespace OmpMcp;

public sealed record WaitRequest(string SessionToken, string AgentId, string? RunId = null,
    string Mode = "completion", long? After = null, int TimeoutSeconds = 3600);

public sealed class WaitServer : IAsyncDisposable
{
    private readonly AgentManager manager;
    private readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<long, Task> clients = new();
    private readonly byte[] token = RandomNumberGenerator.GetBytes(32);
    private readonly Task acceptTask;
    private long nextClient;
    public string SocketPath { get; }
    public string SessionToken => Convert.ToHexString(token).ToLowerInvariant();

    public WaitServer(AgentManager manager)
    {
        this.manager = manager;
        SocketPath = Path.Combine(manager.RuntimeDirectory, "wait.sock");
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(64);
        acceptTask = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var socket = await listener.AcceptAsync(lifetime.Token);
                var id = Interlocked.Increment(ref nextClient);
                var task = HandleAsync(socket);
                clients[id] = task;
                _ = task.ContinueWith(_ => clients.TryRemove(id, out var ignored), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task HandleAsync(Socket socket)
    {
        using (socket)
        await using (var stream = new NetworkStream(socket, ownsSocket: false))
        using (var disconnected = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(disconnected.Token);
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                await using var lines = JsonLines.ReadAsync(stream, 16 * 1024, handshake.Token).GetAsyncEnumerator();
                if (!await lines.MoveNextAsync()) return;
                var request = JsonSerializer.Deserialize<WaitRequest>(lines.Current, Json.Options)
                    ?? throw new OmpMcpException("invalid_request", "Missing wait request.");
                byte[] supplied;
                try { supplied = Convert.FromHexString(request.SessionToken ?? ""); }
                catch (FormatException) { supplied = []; }
                if (!CryptographicOperations.FixedTimeEquals(token, supplied))
                    throw new OmpMcpException("unauthorized", "Invalid session token.");
                // Wait requests occupy one connection. EOF detaches the waiter only.
                var monitor = MonitorDisconnectAsync(stream, disconnected);
                try
                {
                    var reply = await manager.WaitAsync(request.AgentId, request.RunId, request.Mode, request.After,
                        TimeSpan.FromSeconds(request.TimeoutSeconds), disconnected.Token);
                    await WriteAsync(stream, new { ok = true, reply }, disconnected.Token);
                }
                finally
                {
                    await disconnected.CancelAsync();
                    await monitor;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is OmpMcpException or JsonException or InvalidDataException or ArgumentException)
            {
                try { await WriteAsync(stream, new { ok = false, error = new { code = (e as OmpMcpException)?.Code ?? "invalid_request", message = e.Message } }, lifetime.Token); }
                catch (Exception writeError) when (writeError is IOException or OperationCanceledException or SocketException) { }
            }
            catch (Exception e) when (e is IOException or SocketException) { }
        }
    }

    private static async Task MonitorDisconnectAsync(Stream stream, CancellationTokenSource cancellation)
    {
        try { _ = await stream.ReadAsync(new byte[1], cancellation.Token); }
        catch (Exception e) when (e is IOException or OperationCanceledException) { }
        await cancellation.CancelAsync();
    }

    private static async Task WriteAsync(Stream stream, object value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json.Options) + "\n");
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        listener.Dispose();
        await acceptTask;
        await Task.WhenAll(clients.Values);
        File.Delete(SocketPath);
        if (!Directory.EnumerateFileSystemEntries(manager.RuntimeDirectory).Any()) Directory.Delete(manager.RuntimeDirectory);
        lifetime.Dispose();
    }
}
