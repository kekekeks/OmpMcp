using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace OmpMcp;

internal sealed class McpTools(AgentManager manager, ServerSession session)
{
    public IEnumerable<McpServerTool> CreateTools() => GetType().GetMethods()
        .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
        .Select(m => McpServerTool.Create(m, this, new() { SerializerOptions = Json.Options }));

    private static async Task<CallToolResult> Result(Func<Task<object>> action)
    {
        try
        {
            var result = Json.Element(await action());
            return new() { Content = [new TextContentBlock { Text = result.GetRawText() }], StructuredContent = result };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var error = Json.Element(new { error = new { code = (e as OmpMcpException)?.Code ?? "operation_failed", message = e.Message } });
            return new() { IsError = true, Content = [new TextContentBlock { Text = error.GetRawText() }], StructuredContent = error };
        }
    }

    [McpServerTool(Name = "models_list", ReadOnly = true), Description("List OMP models available through configured providers. Use an exact returned id in agent_create.")]
    public Task<CallToolResult> ModelsList(
        [Description("Optional case-insensitive filter on model identifier or name.")] string? filter = null,
        CancellationToken cancellationToken = default) => Result(async () => new { models = await manager.ModelsAsync(filter, cancellationToken) });

    [McpServerTool(Name = "agent_create"), Description("Create a persistent OMP agent. Returns its selected model and callback credentials. An optional prompt starts work immediately.")]
    public Task<CallToolResult> AgentCreate(
        [Description("Absolute path to the agent workspace.")] string workspace,
        [Description("Exact provider/model-id from models_list. Omit to use OMP's configured default.")] string? model = null,
        [Description("Additional system instructions for this agent.")] string? instructions = null,
        [Description("Initial work request. Omit to create an idle agent.")] string? prompt = null,
        [Description("Built-in tool allowlist. Default: read, bash, edit, write, grep, glob. Empty disables built-in tools.")] string[]? tools = null,
        [Description("Optional OMP thinking level: off, minimal, low, medium, high, xhigh, max, auto.")] string? thinking = null) => Result(async () =>
        {
            if (prompt is not null) Agent.ValidateMessage(prompt);
            var agent = await manager.CreateAsync(workspace, model, instructions, tools, thinking);
            var submission = prompt is null ? null : await manager.SendAsync(agent.AgentId, prompt);
            return new { agent = manager.Get(agent.AgentId), submission, session };
        });

    [McpServerTool(Name = "agent_send"), Description("Send work to an idle agent or steering to a running agent. Returns after acceptance, without waiting for completion.")]
    public Task<CallToolResult> AgentSend(string agentId, string message) => Result(async () => await manager.SendAsync(agentId, message));

    [McpServerTool(Name = "agents_list", ReadOnly = true), Description("List agents owned by this server, including closed and failed agents.")]
    public Task<CallToolResult> AgentsList() => Result(() => Task.FromResult<object>(new { agents = manager.List() }));

    [McpServerTool(Name = "agent_status", ReadOnly = true), Description("Read agent state and, optionally, a particular run and its retained result.")]
    public Task<CallToolResult> AgentStatus(string agentId, string? runId = null) => Result(() => Task.FromResult<object>(new
        { agent = manager.Get(agentId), run = runId is null ? null : manager.GetRun(agentId, runId) }));

    [McpServerTool(Name = "events_read", ReadOnly = true), Description("Read retained agent events after a cursor. Reports expired history explicitly. Reading does not consume events.")]
    public Task<CallToolResult> EventsRead(long after = 0, string? agentId = null, string? runId = null) =>
        Result(() => Task.FromResult<object>(manager.ReadEvents(after, agentId, runId)));

    [McpServerTool(Name = "agent_cancel"), Description("Cancel a run and preserve the agent conversation. Cancelling a waiter does not call this tool.")]
    public Task<CallToolResult> AgentCancel(string agentId, string runId) => Result(async () => await manager.CancelAsync(agentId, runId));

    [McpServerTool(Name = "agent_close"), Description("Close an agent and terminate its OMP process. Retained results remain readable.")]
    public Task<CallToolResult> AgentClose(string agentId) => Result(async () => await manager.CloseAsync(agentId));

    [McpServerTool(Name = "session_info", ReadOnly = true), Description("Get the Unix socket, session token, and executable for an elevated ompmcp wait command.")]
    public Task<CallToolResult> SessionInfo() => Result(() => Task.FromResult<object>(session));
}
