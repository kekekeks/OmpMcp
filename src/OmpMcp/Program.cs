using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using OmpMcp.Rpc;

namespace OmpMcp;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        using var termination = !OperatingSystem.IsWindows() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM,
            context => { context.Cancel = true; stop.Cancel(); }) : null;
        try
        {
            if (args.Contains("--help") || args.Contains("-h")) { Console.WriteLine(Help); return 0; }
            if (args.Length > 0 && args[0] == "wait") return await WaitAsync(Parse(args[1..]), stop.Token);
            if (args.Length > 0 && args[0] == "serve") args = args[1..];
            var values = Parse(args);
            RequireKnown(values, "omp", "agent-dir", "model", "config", "event-capacity");
            var options = new ServerOptions
            {
                OmpPath = values.GetValueOrDefault("omp") ?? "omp",
                AgentDirectory = values.GetValueOrDefault("agent-dir"),
                DefaultModel = values.GetValueOrDefault("model"),
                Configuration = values.GetValueOrDefault("config"),
                EventCapacity = values.TryGetValue("event-capacity", out var capacity) ? int.Parse(capacity) : 4096
            };
            var manager = new AgentManager(options);
            WaitServer? waits = null;
            try
            {
                waits = new(manager);
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the current executable.");
                var prefix = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? new[] { typeof(Program).Assembly.Location } : Array.Empty<string>();
                var session = new ServerSession(waits.SocketPath, waits.SessionToken, executable, prefix);
                var tools = new McpTools(manager, session);
                var serverOptions = new McpServerOptions
                {
                    ServerInfo = new() { Name = "ompmcp", Version = "0.1.0" },
                    Capabilities = new() { Logging = new() },
                    ServerInstructions = "Create agents with agent_create, send steering with agent_send, and read results with agent_status or events_read. " +
                        "For background waits, invoke the session_info wait executable with wait --socket SOCKET --token TOKEN --agent AGENT --run RUN. " +
                        "The host must allow socket access for that shell command. Agent processes use the MCP server's permissions.",
                    ToolCollection = [.. tools.CreateTools()]
                };
                await using var transport = new StdioServerTransport("ompmcp");
                await using var server = McpServer.Create(transport, serverOptions);
                using var notificationsStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                var notifications = McpNotifications.RunAsync(server, manager.Events, notificationsStop.Token);
                try { await server.RunAsync(stop.Token); }
                finally
                {
                    await notificationsStop.CancelAsync();
                    await notifications;
                }
            }
            finally
            {
                await manager.DisposeAsync();
                if (waits is not null) await waits.DisposeAsync();
                if (Directory.Exists(manager.RuntimeDirectory) && !Directory.EnumerateFileSystemEntries(manager.RuntimeDirectory).Any())
                    Directory.Delete(manager.RuntimeDirectory);
            }
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 130; }
        catch (Exception e)
        {
            Console.Error.WriteLine($"ompmcp: {e.Message}");
            return 1;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument: {args[i]}");
            var key = args[i][2..];
            if (key == "stream") { if (!values.TryAdd(key, "true")) throw new ArgumentException("Duplicate --stream."); continue; }
            if (++i == args.Length) throw new ArgumentException($"Missing value for --{key}.");
            if (!values.TryAdd(key, args[i])) throw new ArgumentException($"Duplicate --{key}.");
        }
        return values;
    }

    private static void RequireKnown(Dictionary<string, string> values, params string[] names)
    {
        foreach (var name in values.Keys)
            if (!names.Contains(name, StringComparer.Ordinal)) throw new ArgumentException($"Unknown flag: --{name}.");
    }

    private static async Task<int> WaitAsync(Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        RequireKnown(values, "socket", "token", "agent", "run", "mode", "after", "timeout", "stream");
        string Required(string name) => values.GetValueOrDefault(name) ?? throw new ArgumentException($"Missing --{name}.");
        var socketPath = Required("socket");
        var token = values.GetValueOrDefault("token") ?? Environment.GetEnvironmentVariable("OMPMCP_SESSION_TOKEN")
            ?? throw new ArgumentException("Set --token or OMPMCP_SESSION_TOKEN.");
        var streamEvents = values.ContainsKey("stream");
        var timeout = values.TryGetValue("timeout", out var seconds) ? int.Parse(seconds) : 3600;
        if (timeout is <= 0 or > 43200) throw new ArgumentException("--timeout must be between 1 and 43200 seconds.");
        var deadline = Stopwatch.StartNew();
        var request = new WaitRequest(token, Required("agent"), values.GetValueOrDefault("run"),
            streamEvents ? "events" : values.GetValueOrDefault("mode") ?? "completion",
            values.TryGetValue("after", out var after) ? long.Parse(after) : null, timeout);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeout));
        try
        {
            while (true)
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeoutSource.Token);
                await using var stream = new NetworkStream(socket);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, Json.Options) + "\n");
                await stream.WriteAsync(bytes, timeoutSource.Token);
                await using var lines = JsonLines.ReadAsync(stream, RpcFrameDecoder.MaxLogicalBytes, timeoutSource.Token).GetAsyncEnumerator();
                if (!await lines.MoveNextAsync()) throw new IOException("The server closed the wait connection.");
                var envelope = JsonSerializer.Deserialize<JsonElement>(lines.Current);
                if (!envelope.GetProperty("ok").GetBoolean()) { Console.WriteLine(lines.Current); return 1; }
                var reply = envelope.GetProperty("reply").Deserialize<WaitReply>(Json.Options)!;
                Console.WriteLine(JsonSerializer.Serialize(reply, Json.Options));
                if (reply.Status == "completed") return reply.Run?.Status == "succeeded" ? 0 : 1;
                if (reply.Status == "timeout") return 2;
                if (!streamEvents) return reply.Status == "events" ? 0 : 1;
                if (reply.Status != "events") return 1;
                var remaining = timeout - (int)deadline.Elapsed.TotalSeconds;
                if (remaining <= 0) break;
                request = request with { After = reply.Page.Cursor, TimeoutSeconds = remaining };
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        Console.WriteLine(JsonSerializer.Serialize(new { status = "timeout" }, Json.Options));
        return 2;
    }

    private const string Help = """
        OmpMcp — persistent OMP agents over MCP

        ompmcp [serve] [--omp PATH] [--agent-dir DIR] [--model PROVIDER/MODEL]
                      [--config FILE] [--event-capacity COUNT]
        ompmcp wait --socket PATH --agent ID --run ID [--token TOKEN]
                    [--timeout SECONDS] [--after CURSOR] [--mode completion|events] [--stream]

        serve uses MCP over stdin/stdout. Logs go to stderr.
        --model sets the server default. Omit it to retain OMP's configured default.
        --agent-dir uses an isolated OMP directory with models.yml and the default profile.
        wait requires host permission to access the local Unix socket.
        OMPMCP_SESSION_TOKEN supplies the token when --token is omitted.
        --stream prints event pages as JSON lines until the run ends or the timeout expires.
        Wait exit codes: 0 success/events, 1 failure, 2 timeout, 130 interruption.
        """;
}
