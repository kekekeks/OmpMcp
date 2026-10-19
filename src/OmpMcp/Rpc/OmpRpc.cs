using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace OmpMcp.Rpc;

internal sealed class OmpRpc(ProcessStartInfo startInfo, TimeSpan timeout) : IAsyncDisposable
{
    private readonly Process process = new() { StartInfo = startInfo };
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> requests = new();
    private readonly TaskCompletionSource<JsonElement> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim writer = new(1);
    private readonly CancellationTokenSource ioStop = new();
    private Task? readTask, stderrTask;
    private Exception? failure;
    private int disposing;
    private readonly object disposeSync = new();
    private Task? disposeTask;
    private readonly StringBuilder stderr = new();
    public Func<JsonElement, Task>? Frame { get; set; }
    public Action<Exception>? Failed { get; set; }
    public int ProcessId => process.Id;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (disposeSync)
        {
            ObjectDisposedException.ThrowIf(disposing != 0, this);
            if (!process.Start()) throw new IOException("Cannot start OMP.");
            readTask = ReadAsync();
            stderrTask = DrainErrorAsync();
        }
        var hello = await ready.Task.WaitAsync(timeout, cancellationToken);
        if (!hello.TryGetProperty("supportedProtocolVersions", out var versions) ||
            !versions.EnumerateArray().Any(v => v.GetInt32() == 2))
            throw new OmpMcpException("incompatible_rpc", "OMP must support RPC protocol version 2 and host tools (tested with 17.4.1).");
        await CommandAsync("negotiate_protocol", new { protocolVersion = 2 }, cancellationToken);
    }

    public async Task<JsonElement> CommandAsync(string type, object? fields = null,
        CancellationToken cancellationToken = default, string? requestId = null)
    {
        if (failure is not null) throw new IOException("OMP RPC is unavailable.", failure);
        var id = requestId ?? Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!requests.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate RPC request identifier.");
        try
        {
            var command = fields is null ? new Dictionary<string, JsonElement>() :
                Json.Element(fields).EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
            command["id"] = Json.Element(id);
            command["type"] = Json.Element(type);
            await SendAsync(command, cancellationToken);
            var response = await completion.Task.WaitAsync(timeout, cancellationToken);
            if (!response.GetProperty("success").GetBoolean())
                throw new OmpMcpException("rpc_error", response.GetProperty("error").GetString() ?? "OMP rejected the command.");
            return response.TryGetProperty("data", out var data) ? data : Json.Element(new { });
        }
        catch (TimeoutException e)
        {
            Fail(new IOException($"OMP did not acknowledge {type} within {timeout.TotalSeconds} seconds.", e));
            throw;
        }
        finally { requests.TryRemove(id, out _); }
    }

    public async Task SendAsync(object frame, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame, Json.Options) + "\n");
        if (bytes.Length > RpcFrameDecoder.MaxFrameBytes)
            throw new OmpMcpException("message_too_large", "The RPC command exceeds the one MiB frame limit.");
        await writer.WaitAsync(cancellationToken);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
        }
        finally { writer.Release(); }
    }

    private async Task ReadAsync()
    {
        try
        {
            var decoder = new RpcFrameDecoder();
            await foreach (var line in JsonLines.ReadAsync(process.StandardOutput.BaseStream, RpcFrameDecoder.MaxFrameBytes, ioStop.Token))
            {
                if (decoder.Push(line) is not { } frame) continue;
                switch (frame.GetProperty("type").GetString())
                {
                    case "ready": ready.TrySetResult(frame); break;
                    case "response":
                        if (frame.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                            requests.TryRemove(id.GetString()!, out var completion)) completion.TrySetResult(frame);
                        // Prompt scheduling can fail after its first successful acknowledgement.
                        if (!frame.GetProperty("success").GetBoolean() && Frame is not null) await Frame(frame);
                        break;
                    default:
                        if (Frame is not null) await Frame(frame);
                        break;
                }
            }
            decoder.Finish();
            if (Volatile.Read(ref disposing) == 0)
                throw new IOException("OMP closed its RPC output unexpectedly.");
        }
        catch (Exception e) { Fail(e); }
    }

    private async Task DrainErrorAsync()
    {
        var buffer = new char[2048];
        try
        {
            int count;
            while ((count = await process.StandardError.ReadAsync(buffer.AsMemory(), ioStop.Token)) > 0)
            {
                lock (stderr)
                {
                    stderr.Append(buffer, 0, count);
                    if (stderr.Length > 8192) stderr.Remove(0, stderr.Length - 8192);
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException) { }
    }

    public string ErrorTail { get { lock (stderr) return stderr.ToString(); } }

    private void Fail(Exception e)
    {
        if (Interlocked.CompareExchange(ref failure, e, null) is not null) return;
        ready.TrySetException(e);
        foreach (var request in requests.Values) request.TrySetException(e);
        if (Volatile.Read(ref disposing) == 0)
        {
            Failed?.Invoke(e);
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeSync) return new(disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref disposing, 1);
        try
        {
            process.StandardInput.Close();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
        catch (InvalidOperationException) { }
        finally
        {
            Fail(new IOException("OMP RPC closed."));
            await ioStop.CancelAsync();
            if (readTask is not null) await readTask;
            if (stderrTask is not null) await stderrTask;
            process.Dispose();
            ioStop.Dispose();
        }
    }
}
