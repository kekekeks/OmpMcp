using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using OmpMcp.Rpc;

namespace OmpMcp.Tests;

internal sealed class McpHarness : IAsyncDisposable
{
    private readonly Process process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writer = new(1);
    private readonly Task reader;
    private readonly Task<string> stderr;
    private long nextId;
    public Channel<JsonElement> Notifications { get; } = Channel.CreateUnbounded<JsonElement>();

    private McpHarness(Process process)
    {
        this.process = process;
        stderr = process.StandardError.ReadToEndAsync();
        reader = ReadAsync();
    }

    public static Process StartProcess(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "server", "ompmcp.dll"));
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    public static async Task<McpHarness> StartAsync(TestEnvironment env)
    {
        var result = new McpHarness(StartProcess("serve", "--agent-dir", env.AgentDirectory, "--omp", env.Options.OmpPath));
        try
        {
            await result.RequestAsync("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
            await result.WriteAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }

    public async Task<JsonElement> CallAsync(string name, object? arguments = null)
    {
        var response = await RequestAsync("tools/call", new { name, arguments = arguments ?? new { } });
        var result = response.GetProperty("result");
        if (result.TryGetProperty("isError", out var failed) && failed.GetBoolean()) throw new Exception(result.GetRawText());
        return result.GetProperty("structuredContent");
    }

    public async Task<JsonElement> RequestAsync(string method, object parameters)
    {
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters });
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private async Task WriteAsync(object value)
    {
        await writer.WaitAsync();
        try { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value, Json.Options)); await process.StandardInput.FlushAsync(); }
        finally { writer.Release(); }
    }

    private async Task ReadAsync()
    {
        try
        {
            await foreach (var line in JsonLines.ReadAsync(process.StandardOutput.BaseStream, RpcFrameDecoder.MaxLogicalBytes))
            {
                var frame = JsonSerializer.Deserialize<JsonElement>(line);
                if (frame.TryGetProperty("id", out var id) && pending.TryRemove(id.GetInt64(), out var completion)) completion.TrySetResult(frame);
                else await Notifications.Writer.WriteAsync(frame);
            }
            foreach (var completion in pending.Values) completion.TrySetException(new IOException("MCP server exited: " + await stderr));
        }
        catch (Exception e) { foreach (var completion in pending.Values) completion.TrySetException(e); }
    }

    public async Task CloseInputAsync()
    {
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (process.ExitCode != 0) throw new Exception("MCP shutdown failed: " + await stderr);
    }

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
        {
            process.StandardInput.Close();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        await reader;
        await stderr;
        process.Dispose();
    }
}
