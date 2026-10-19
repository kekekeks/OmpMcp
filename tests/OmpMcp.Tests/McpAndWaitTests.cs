using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OmpMcp.Rpc;
using Xunit;

namespace OmpMcp.Tests;

public sealed class McpAndWaitTests
{
    [Fact]
    public async Task Wait_helper_enforces_timeout_when_server_does_not_respond()
    {
        var directory = Directory.CreateTempSubdirectory("ompmcp-timeout-");
        var path = Path.Combine(directory.FullName, "wait.sock");
        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            using var waiter = McpHarness.StartProcess("wait", "--socket", path, "--token", "test",
                "--agent", "test", "--run", "test", "--timeout", "1");
            var output = waiter.StandardOutput.ReadToEndAsync();
            var errors = waiter.StandardError.ReadToEndAsync();
            using var accepted = await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await waiter.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, waiter.ExitCode);
            Assert.Equal("timeout", JsonSerializer.Deserialize<JsonElement>(await output).GetProperty("status").GetString());
            Assert.Empty(await errors);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task Mcp_model_discovery_agent_tools_notifications_and_wait_executable()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await using var mcp = await McpHarness.StartAsync(env);
        var toolList = await mcp.RequestAsync("tools/list", new { });
        Assert.Contains(toolList.GetProperty("result").GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == "models_list");
        var models = await mcp.CallAsync("models_list", new { filter = "ompmcp-test" });
        Assert.Equal(2, models.GetProperty("models").GetArrayLength());
        var created = await mcp.CallAsync("agent_create", new { workspace = env.Workspace,
            model = "ompmcp-test/second", prompt = "Return a result.", tools = Array.Empty<string>(), thinking = "off" });
        var agent = created.GetProperty("agent").Deserialize<AgentInfo>(Json.Options)!;
        var submission = created.GetProperty("submission").Deserialize<Submission>(Json.Options)!;
        var session = created.GetProperty("session").Deserialize<ServerSession>(Json.Options)!;
        Assert.Equal("ompmcp-test/second", agent.Model!.Id);
        using var wait = McpHarness.StartProcess("wait", "--socket", session.Socket, "--token", session.SessionToken,
            "--agent", agent.AgentId, "--run", submission.RunId, "--stream", "--timeout", "30");
        var output = wait.StandardOutput.ReadToEndAsync();
        var errors = wait.StandardError.ReadToEndAsync();
        await (await env.Provider.NextAsync()).NotifyAsync("Interim progress.");
        JsonElement notification;
        do { notification = await mcp.Notifications.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
        while (!notification.TryGetProperty("params", out var p) || !p.TryGetProperty("data", out var d) ||
            !d.TryGetProperty("type", out var t) || t.GetString() != "notification");
        await env.FinishResultAsync("MCP result.");
        await wait.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(wait.ExitCode == 0, await errors);
        Assert.Contains("MCP result.", await output);
        var status = await mcp.CallAsync("agent_status", new { agentId = agent.AgentId, runId = submission.RunId });
        Assert.Equal("succeeded", status.GetProperty("run").GetProperty("status").GetString());
        await mcp.CallAsync("agent_close", new { agentId = agent.AgentId });
        await mcp.CloseInputAsync();
        Assert.False(File.Exists(session.Socket));
    }

    [Fact]
    public async Task Callback_authentication_concurrent_clients_disconnect_and_retained_results()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await using var callback = new WaitServer(env.Manager);
        var agent = await env.AgentAsync();
        var run = await env.Manager.SendAsync(agent.AgentId, "Wait for completion.");
        var request = new WaitRequest(callback.SessionToken, agent.AgentId, run.RunId, TimeoutSeconds: 30);
        var denied = await Query(callback.SocketPath, request with { SessionToken = "bad-token" });
        Assert.False(denied.GetProperty("ok").GetBoolean());
        Assert.Equal("unauthorized", denied.GetProperty("error").GetProperty("code").GetString());
        var waiters = Enumerable.Range(0, 5).Select(_ => Query(callback.SocketPath, request)).ToArray();
        using (var disconnect = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            await disconnect.ConnectAsync(new UnixDomainSocketEndPoint(callback.SocketPath));
            await disconnect.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, Json.Options) + "\n"));
        }
        Assert.Equal("running", env.Manager.GetRun(agent.AgentId, run.RunId).Status);
        await env.FinishResultAsync("Shared result.");
        foreach (var result in await Task.WhenAll(waiters))
            Assert.Equal("Shared result.", result.GetProperty("reply").GetProperty("run").GetProperty("result").GetString());
        Assert.Equal("completed", (await Query(callback.SocketPath, request)).GetProperty("reply").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Host_eof_terminates_owned_agents_and_releases_waiter()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await using var mcp = await McpHarness.StartAsync(env);
        var created = await mcp.CallAsync("agent_create", new { workspace = env.Workspace, prompt = "Wait.", tools = Array.Empty<string>() });
        var agent = created.GetProperty("agent").Deserialize<AgentInfo>(Json.Options)!;
        var submission = created.GetProperty("submission").Deserialize<Submission>(Json.Options)!;
        var session = created.GetProperty("session").Deserialize<ServerSession>(Json.Options)!;
        _ = await env.Provider.NextAsync();
        using var child = Process.GetProcessById(agent.ProcessId!.Value);
        using var waiter = McpHarness.StartProcess("wait", "--socket", session.Socket, "--token", session.SessionToken,
            "--agent", agent.AgentId, "--run", submission.RunId, "--timeout", "30");
        var output = waiter.StandardOutput.ReadToEndAsync();
        var errors = waiter.StandardError.ReadToEndAsync();
        await mcp.CloseInputAsync();
        await waiter.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(0, waiter.ExitCode);
        Assert.False(File.Exists(session.Socket));
        _ = await output;
        _ = await errors;
    }

    private static async Task<JsonElement> Query(string path, WaitRequest request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token);
        await using var stream = new NetworkStream(socket);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, Json.Options) + "\n"), timeout.Token);
        await using var lines = JsonLines.ReadAsync(stream, RpcFrameDecoder.MaxLogicalBytes, timeout.Token).GetAsyncEnumerator();
        Assert.True(await lines.MoveNextAsync());
        return JsonSerializer.Deserialize<JsonElement>(lines.Current);
    }
}
