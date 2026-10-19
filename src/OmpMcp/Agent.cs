using System.Text.Json;
using OmpMcp.Rpc;

namespace OmpMcp;

internal sealed class Agent(string id, string workspace, OmpRpc rpc, EventStore events) : IAsyncDisposable
{
    private sealed class Run(string id)
    {
        public string Id { get; } = id;
        public string Status { get; set; } = "running";
        public string? Result { get; set; }
        public string? Error { get; set; }
        public Dictionary<string, bool> Messages { get; } = new();
        public int ResultRevision { get; set; } = -1;
        public bool CancelRequested { get; set; }
    }

    private readonly object sync = new();
    private readonly SemaphoreSlim operations = new(1);
    private readonly Dictionary<string, Run> runs = new();
    private Run? active;
    private string status = "starting";
    private string? error;
    private ModelInfo? model;
    private int? processId;
    private bool closed;

    public string Id => id;
    public AgentInfo Info { get { lock (sync) return new(id, status, workspace, model, active?.Id, processId, error); } }

    public async Task InitializeAsync(string? expectedModel, CancellationToken cancellationToken)
    {
        await operations.WaitAsync(cancellationToken);
        try
        {
            rpc.Frame = HandleFrameAsync;
            rpc.Failed = Fail;
            await rpc.StartAsync(cancellationToken);
            processId = rpc.ProcessId;
            var state = await rpc.CommandAsync("get_state", cancellationToken: cancellationToken);
            if (state.TryGetProperty("model", out var selected) && selected.ValueKind == JsonValueKind.Object)
                model = OmpProcessFactory.ParseModel(selected);
            if (model is null || (expectedModel is not null && model.Id != expectedModel))
                throw new OmpMcpException("model_unavailable", $"OMP did not select the requested model: {expectedModel ?? "configured default"}.");
            if (state.GetProperty("dumpTools").EnumerateArray().Any(t => t.GetProperty("name").GetString() is "task" or "advisor"))
                throw new OmpMcpException("delegation_enabled", "OMP exposed a delegation tool despite the agent configuration.");
            var parameters = new
            {
                type = "object",
                properties = new { run_id = new { type = "string" }, text = new { type = "string" } },
                required = new[] { "run_id", "text" },
                additionalProperties = false
            };
            await rpc.CommandAsync("set_host_tools", new
            {
                tools = new[]
                {
                    new { name = "ompmcp_agent_result", description = "Submit the complete result for the current run, then end your response.", loadMode = "essential", parameters },
                    new { name = "ompmcp_agent_notify", description = "Send useful interim progress to the host without ending the run.", loadMode = "essential", parameters }
                }
            }, cancellationToken);
            lock (sync)
            {
                if (closed || status == "failed") throw new IOException("Agent closed during startup.");
                status = "idle";
                events.Add(id, null, "agent_ready", new { model });
            }
        }
        finally { operations.Release(); }
    }

    public async Task<Submission> SendAsync(string message, CancellationToken lifetime)
    {
        ValidateMessage(message);
        await operations.WaitAsync(lifetime);
        Run run;
        string messageId, mode;
        try
        {
            lock (sync)
            {
                RequireUsable();
                mode = active is null ? "start" : "steer";
                if (active is null)
                {
                    active = new(Guid.NewGuid().ToString("N"));
                    runs.Add(active.Id, active);
                    status = "running";
                    events.Add(id, active.Id, "run_started", new { });
                }
                run = active;
                messageId = Guid.NewGuid().ToString("N");
                run.Messages.Add(messageId, false);
                events.Add(id, run.Id, "message_accepted", new { messageId, mode });
            }
            try
            {
                await rpc.CommandAsync("prompt", new
                {
                    message = $"<ompmcp_message run_id=\"{run.Id}\" message_id=\"{messageId}\">\n{message}\n</ompmcp_message>",
                    streamingBehavior = "steer"
                }, lifetime, messageId);
            }
            catch (Exception e)
            {
                Fail(e);
                throw;
            }
            return new(id, run.Id, messageId, true, mode);
        }
        finally { operations.Release(); }
    }

    internal static void ValidateMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new OmpMcpException("invalid_message", "The message cannot be empty.");
        // Bound escaped JSON too, before accepting work into the run.
        if (Json.Element(message).GetRawText().Length > 200_000)
            throw new OmpMcpException("message_too_large", "The message exceeds the 200,000-character JSON limit.");
    }

    public RunInfo GetRun(string runId)
    {
        lock (sync)
        {
            if (!runs.TryGetValue(runId, out var run)) throw new OmpMcpException("unknown_run", "Unknown run identifier.");
            return Snapshot(run);
        }
    }

    public async Task<RunInfo> CancelAsync(string runId, CancellationToken lifetime)
    {
        await operations.WaitAsync(lifetime);
        try
        {
            lock (sync)
            {
                if (!runs.TryGetValue(runId, out var run)) throw new OmpMcpException("unknown_run", "Unknown run identifier.");
                if (run.Status != "running") return Snapshot(run);
                run.CancelRequested = true;
                events.Add(id, run.Id, "cancellation_requested", new { });
            }
            try { await rpc.CommandAsync("abort", cancellationToken: lifetime); }
            catch (Exception e) { Fail(e); throw; }
            lock (sync)
            {
                // Abort acknowledgement is also a boundary if no agent_end was emitted.
                if (active?.Id == runId) Finish(active, "cancelled", "The host cancelled the run.");
                return Snapshot(runs[runId]);
            }
        }
        finally { operations.Release(); }
    }

    private async Task HandleFrameAsync(JsonElement frame)
    {
        object? response = null;
        lock (sync)
        {
            var type = frame.GetProperty("type").GetString();
            if (type == "host_tool_call") response = HandleTool(frame);
            else if (type == "extension_ui_request" && frame.TryGetProperty("id", out var requestId))
                response = new { type = "extension_ui_response", id = requestId.GetString(), cancelled = true };
            else if (active is { } run)
            {
                switch (type)
                {
                    case "message_end":
                        var message = frame.GetProperty("message");
                        var role = message.GetProperty("role").GetString();
                        if (role == "user")
                        {
                            var text = message.GetProperty("content").GetRawText();
                            foreach (var messageId in run.Messages.Keys.ToArray())
                                if (!run.Messages[messageId] && text.Contains(messageId, StringComparison.Ordinal))
                                {
                                    run.Messages[messageId] = true;
                                    events.Add(id, run.Id, "message_delivered", new { messageId });
                                }
                        }
                        else if (role == "assistant" && message.TryGetProperty("stopReason", out var reason) && reason.GetString() == "error")
                            run.Error = message.TryGetProperty("errorMessage", out var failure) ? failure.GetString() : "The model request failed.";
                        break;
                    case "agent_end" when !frame.TryGetProperty("isTerminal", out var terminal) || terminal.ValueKind != JsonValueKind.False:
                        if (run.CancelRequested) Finish(run, "cancelled", "The host cancelled the run.");
                        else if (run.Messages.Values.All(delivered => delivered))
                        {
                            if (run.Error is not null) Finish(run, "failed", run.Error);
                            else if (run.Result is not null && run.ResultRevision == run.Messages.Count)
                                Finish(run, "succeeded", null);
                            else Finish(run, "incomplete", "OMP finished without a result for the latest message.");
                        }
                        // A prompt accepted at this boundary can start another OMP cycle.
                        // Its delivery receipt keeps this run open until that cycle ends.
                        break;
                    case "response":
                        if (frame.TryGetProperty("id", out var commandId) && commandId.ValueKind == JsonValueKind.String &&
                            run.Messages.ContainsKey(commandId.GetString()!))
                            Finish(run, "failed", frame.GetProperty("error").GetString());
                        break;
                    case "prompt_result" when frame.TryGetProperty("agentInvoked", out var invoked) && invoked.ValueKind == JsonValueKind.False:
                        Finish(run, "incomplete", "OMP accepted the prompt without invoking the agent.");
                        break;
                    case "extension_error":
                        events.Add(id, run.Id, "runtime_warning", new { error = frame.GetProperty("error").GetString() });
                        break;
                }
            }
        }
        if (response is not null) await rpc.SendAsync(response);
    }

    private object HandleTool(JsonElement frame)
    {
        var args = frame.GetProperty("arguments");
        var tool = frame.GetProperty("toolName").GetString();
        var runId = args.TryGetProperty("run_id", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        var text = args.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var valid = active is not null && active.Id == runId && !active.CancelRequested &&
            !string.IsNullOrWhiteSpace(text) && tool is "ompmcp_agent_result" or "ompmcp_agent_notify";
        var reply = "The tool requires the active run_id and nonempty text.";
        if (valid && active is { } run)
        {
            if (tool == "ompmcp_agent_notify")
            {
                events.Add(id, run.Id, "notification", new { text });
                reply = "Notification delivered.";
            }
            else
            {
                var revision = run.Messages.Values.Count(delivered => delivered);
                if (run.ResultRevision == revision) reply = "Result already recorded. End your response.";
                else
                {
                    run.Result = text;
                    run.ResultRevision = revision;
                    events.Add(id, run.Id, "result_received", new { text, revision });
                    reply = "Result recorded. End your response unless new host steering requires further work.";
                }
            }
        }
        return new { type = "host_tool_result", id = frame.GetProperty("id").GetString(), isError = !valid,
            result = new { content = new[] { new { type = "text", text = reply } } } };
    }

    private void RequireUsable()
    {
        if (closed) throw new OmpMcpException("closed_agent", "The agent is closed.");
        if (status is "failed" or "starting") throw new OmpMcpException("agent_unavailable", error ?? "The agent is not ready.");
    }

    private RunInfo Snapshot(Run run) => new(id, run.Id, run.Status, run.Result, run.Error,
        run.Messages.Count, run.Messages.Values.Count(delivered => delivered));

    private void Finish(Run run, string outcome, string? failure)
    {
        if (run.Status != "running") return;
        run.Status = outcome;
        run.Error = failure;
        active = null;
        if (!closed && status != "failed") status = "idle";
        events.Add(id, run.Id, "run_completed", Snapshot(run));
    }

    private void Fail(Exception exception)
    {
        lock (sync)
        {
            if (closed || status == "failed") return;
            status = "failed";
            error = exception.Message;
            if (active is { } run) Finish(run, "failed", error);
            events.Add(id, null, "agent_failed", new { error });
        }
    }

    public async ValueTask DisposeAsync()
    {
        await operations.WaitAsync();
        try
        {
            lock (sync)
            {
                if (closed) return;
                closed = true;
                status = "closed";
                if (active is { } run) Finish(run, "cancelled", "The agent closed.");
                events.Add(id, null, "agent_closed", new { });
            }
            await rpc.DisposeAsync();
        }
        finally { operations.Release(); }
    }
}
