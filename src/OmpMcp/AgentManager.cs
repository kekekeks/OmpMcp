using System.Collections.Concurrent;
using OmpMcp.Rpc;

namespace OmpMcp;

public sealed class AgentManager : IAsyncDisposable
{
    private readonly ServerOptions options;
    private readonly ConcurrentDictionary<string, Agent> agents = new();
    private readonly HashSet<OmpRpc> discoveryProcesses = new();
    private readonly OmpProcessFactory factory;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private bool stopping;
    public string RuntimeDirectory { get; }
    public EventStore Events { get; }

    public AgentManager(ServerOptions options)
    {
        this.options = options;
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This version requires Unix sockets.");
        if (options.EventCapacity < 1) throw new ArgumentOutOfRangeException(nameof(options));
        RuntimeDirectory = Path.Combine(Path.GetTempPath(), "ompmcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RuntimeDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var overlay = Path.Combine(RuntimeDirectory, "agent.yml");
        File.WriteAllText(overlay, "interruptMode: wait\ntask:\n  maxRecursionDepth: 0\nadvisor:\n  enabled: false\nmcp:\n  enableProjectConfig: false\nasync:\n  enabled: false\n");
        factory = new(options, overlay);
        Events = new(options.EventCapacity);
    }

    public async Task<AgentInfo> CreateAsync(string workspace, string? model = null, string? instructions = null,
        string[]? tools = null, string? thinking = null)
    {
        var requestedModel = model ?? options.DefaultModel;
        if (requestedModel is not null && (requestedModel.IndexOf('/') <= 0 || requestedModel.EndsWith('/')))
            throw new OmpMcpException("invalid_model", "Use an exact provider/model-id from models_list.");
        var fullWorkspace = Path.GetFullPath(workspace);
        Agent agent;
        lock (sync)
        {
            RequireRunning();
            agent = new(Guid.NewGuid().ToString("N"), fullWorkspace,
                factory.Create(fullWorkspace, requestedModel, instructions, tools, thinking), Events);
            agents.TryAdd(agent.Id, agent);
        }
        try
        {
            await agent.InitializeAsync(requestedModel, lifetime.Token);
            return agent.Info;
        }
        catch
        {
            await agent.DisposeAsync();
            agents.TryRemove(agent.Id, out _);
            throw;
        }
    }

    public async Task<ModelInfo[]> ModelsAsync(string? filter = null, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        OmpRpc rpc;
        lock (sync)
        {
            RequireRunning();
            rpc = factory.Create(RuntimeDirectory, null, null, [], null);
            discoveryProcesses.Add(rpc);
        }
        try
        {
            await rpc.StartAsync(linked.Token);
            var data = await rpc.CommandAsync("get_available_models", cancellationToken: linked.Token);
            return data.GetProperty("models").EnumerateArray().Select(OmpProcessFactory.ParseModel)
                .Where(m => string.IsNullOrEmpty(filter) || m.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
        }
        finally
        {
            await rpc.DisposeAsync();
            lock (sync) discoveryProcesses.Remove(rpc);
        }
    }

    public AgentInfo[] List() => agents.Values.Select(a => a.Info).OrderBy(a => a.AgentId).ToArray();
    public AgentInfo Get(string agentId) => Find(agentId).Info;
    public RunInfo GetRun(string agentId, string runId) => Find(agentId).GetRun(runId);
    public Task<Submission> SendAsync(string agentId, string message) => Find(agentId).SendAsync(message, lifetime.Token);
    public Task<RunInfo> CancelAsync(string agentId, string runId) => Find(agentId).CancelAsync(runId, lifetime.Token);
    public async Task<AgentInfo> CloseAsync(string agentId)
    {
        var agent = Find(agentId);
        await agent.DisposeAsync();
        return agent.Info;
    }

    public EventPage ReadEvents(long after, string? agentId = null, string? runId = null)
    {
        ValidateFilter(agentId, runId);
        return Events.Read(after, agentId, runId).Page;
    }

    public async Task<WaitReply> WaitAsync(string agentId, string? runId, string mode, long? after,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ValidateFilter(agentId, runId);
        if (mode is not ("completion" or "events")) throw new OmpMcpException("invalid_mode", "Wait mode must be completion or events.");
        if (mode == "completion" && runId is null) throw new OmpMcpException("missing_run", "A completion wait requires a run identifier.");
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(12))
            throw new OmpMcpException("invalid_timeout", "The timeout must be greater than zero and at most 12 hours.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var cursor = after ?? Events.Cursor;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (EventPage Page, Task Changed, bool Stopped) snapshot;
            try { snapshot = Events.Read(cursor, agentId, runId); }
            catch (OmpMcpException e) when (e.Code == "expired_cursor" && mode == "completion" && after is null)
            {
                cursor = Events.Cursor;
                snapshot = Events.Read(cursor, agentId, runId);
            }
            var run = runId is null ? null : GetRun(agentId, runId);
            if (run is not null && run.Status != "running") return new("completed", run, snapshot.Page);
            if (mode == "events" && snapshot.Page.Events.Count > 0) return new("events", run, snapshot.Page);
            if (snapshot.Stopped) return new("server_shutdown", run, snapshot.Page);
            if (Get(agentId).Status is "closed" or "failed") return new("agent_closed", run, snapshot.Page);
            if (deadline.IsCancellationRequested) return new("timeout", run, snapshot.Page);
            try { await snapshot.Changed.WaitAsync(deadline.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new("timeout", run, snapshot.Page);
            }
            // A completion wait does not retain unrelated events indefinitely.
            if (mode == "completion") cursor = snapshot.Page.Cursor;
        }
    }

    private void ValidateFilter(string? agentId, string? runId)
    {
        if (runId is not null && agentId is null) throw new OmpMcpException("missing_agent", "A run filter requires an agent identifier.");
        if (agentId is not null) _ = runId is null ? (object)Get(agentId) : GetRun(agentId, runId);
    }

    private Agent Find(string agentId) => agents.TryGetValue(agentId, out var agent) ? agent :
        throw new OmpMcpException("unknown_agent", "Unknown agent identifier.");
    private void RequireRunning()
    {
        if (stopping) throw new OmpMcpException("server_shutdown", "The server is shutting down.");
    }

    public async ValueTask DisposeAsync()
    {
        Agent[] owned;
        OmpRpc[] discoveries;
        lock (sync)
        {
            if (stopping) return;
            stopping = true;
            lifetime.Cancel();
            owned = agents.Values.ToArray();
            discoveries = discoveryProcesses.ToArray();
        }
        try { await Task.WhenAll(owned.Select(async a => await a.DisposeAsync()).Concat(discoveries.Select(async d => await d.DisposeAsync()))); }
        finally
        {
            Events.Stop();
            // The callback server removes its socket before this directory is removed.
            foreach (var file in Directory.EnumerateFiles(RuntimeDirectory, "*.yml")) File.Delete(file);
        }
    }
}
