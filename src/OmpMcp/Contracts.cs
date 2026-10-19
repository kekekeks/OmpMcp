using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace OmpMcp;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Options);
}

public sealed class OmpMcpException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record ModelInfo(string Id, string Provider, string ModelId, string Name,
    int? ContextWindow, int? MaxTokens, bool Reasoning);
public sealed record AgentInfo(string AgentId, string Status, string Workspace, ModelInfo? Model,
    string? ActiveRunId, int? ProcessId, string? Error);
public sealed record RunInfo(string AgentId, string RunId, string Status, string? Result,
    string? Error, int AcceptedMessages, int DeliveredMessages);
public sealed record Submission(string AgentId, string RunId, string MessageId, bool Accepted, string Mode);
public sealed record AgentEvent(long Cursor, DateTimeOffset Time, string AgentId, string? RunId,
    string Type, JsonElement Data);
public sealed record EventPage(IReadOnlyList<AgentEvent> Events, long Cursor, long OldestCursor);
public sealed record WaitReply(string Status, RunInfo? Run, EventPage Page);
public sealed record ServerSession(string Socket, string SessionToken, string WaitExecutable,
    string[] WaitPrefixArguments);

public sealed record ServerOptions
{
    public string OmpPath { get; init; } = "omp";
    public string? AgentDirectory { get; init; }
    public string? DefaultModel { get; init; }
    public string? Configuration { get; init; }
    public int EventCapacity { get; init; } = 4096;
    public TimeSpan RpcTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
