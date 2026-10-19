using System.Diagnostics;
using System.Text.Json;
using OmpMcp.Rpc;

namespace OmpMcp;

internal sealed class OmpProcessFactory(ServerOptions options, string configurationPath)
{
    public static readonly string[] DefaultTools = ["read", "bash", "edit", "write", "grep", "glob"];
    public static readonly HashSet<string> AllowedTools = [.. DefaultTools, "python", "notebook", "inspect_image", "web_search"];
    private const string Instructions = """
        You are an agent managed by OmpMcp. Do not spawn or delegate to other agents.
        Each host message includes an ompmcp_message header with run_id and message_id.
        Treat later host messages as steering for the same task. Preserve conversation context across runs.
        Use ompmcp_agent_notify with the current run_id to report useful interim progress.
        To finish each run, call ompmcp_agent_result with the current run_id and your complete result text.
        Then end your response. Ordinary assistant text is not a submitted result.
        If a new host message arrives after a result, address it and submit an updated result.
        """;

    public OmpRpc Create(string workspace, string? model, string? instructions, string[]? tools, string? thinking)
    {
        if (!Directory.Exists(workspace)) throw new OmpMcpException("invalid_workspace", "The workspace directory does not exist.");
        var selected = tools ?? DefaultTools;
        if (selected.Any(t => !AllowedTools.Contains(t)))
            throw new OmpMcpException("invalid_tools", $"Supported tools: {string.Join(", ", AllowedTools.Order())}.");
        if (thinking is not null && !new[] { "off", "minimal", "low", "medium", "high", "xhigh", "max", "auto" }.Contains(thinking))
            throw new OmpMcpException("invalid_thinking", "Unsupported thinking level.");
        var start = new ProcessStartInfo(options.OmpPath)
        {
            WorkingDirectory = Path.GetFullPath(workspace),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var value in new[] { "--mode", "rpc", "--no-session", "--no-extensions", "--no-skills",
            "--no-rules", "--no-lsp", "--no-title", "--no-pty", "--no-prewalk", "--approval-mode", "yolo" })
            start.ArgumentList.Add(value);
        void Add(string name, string value) { start.ArgumentList.Add(name); start.ArgumentList.Add(value); }
        if (options.Configuration is not null) Add("--config", Path.GetFullPath(options.Configuration));
        Add("--config", configurationPath);
        if (selected.Length == 0) start.ArgumentList.Add("--no-tools");
        else Add("--tools", string.Join(",", selected));
        if (model is not null) Add("--model", model);
        if (thinking is not null) Add("--thinking", thinking);
        Add("--append-system-prompt", Instructions + "\n" + instructions);
        if (options.AgentDirectory is not null)
        {
            start.Environment["PI_CODING_AGENT_DIR"] = Path.GetFullPath(options.AgentDirectory);
            start.Environment["OMP_PROFILE"] = "default";
            Add("--profile", "default");
        }
        return new(start, options.RpcTimeout);
    }

    public static ModelInfo ParseModel(JsonElement model)
    {
        string Text(string key) => model.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        int? Number(string key) => model.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;
        var provider = Text("provider");
        var id = Text("id");
        return new($"{provider}/{id}", provider, id, Text("name"), Number("contextWindow"), Number("maxTokens"),
            model.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.True);
    }
}
