using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace OmpMcp.Tests;

internal sealed class FakeProvider : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly Channel<ModelRequest> requests = Channel.CreateUnbounded<ModelRequest>();
    private readonly Channel<TaskCompletionSource> gates = Channel.CreateUnbounded<TaskCompletionSource>();
    public string Url => app.Urls.Single();

    private FakeProvider(WebApplication app)
    {
        this.app = app;
        app.MapPost("/v1/chat/completions", async context =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body);
            var request = new ModelRequest(context, body);
            await requests.Writer.WriteAsync(request);
            try { await request.Completed.Task.WaitAsync(context.RequestAborted); }
            catch (OperationCanceledException) { }
        });
        app.MapGet("/gate", async context =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await gates.Writer.WriteAsync(gate);
            await gate.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("tool released", context.RequestAborted);
        });
    }

    public static async Task<FakeProvider> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        var result = new FakeProvider(builder.Build());
        await result.app.StartAsync();
        return result;
    }

    public async Task<ModelRequest> NextAsync() => await requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
    public async Task<TaskCompletionSource> NextGateAsync() => await gates.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
}

internal sealed class ModelRequest(HttpContext context, JsonElement body)
{
    public JsonElement Body { get; } = body;
    public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string[] UserMessages => Body.GetProperty("messages").EnumerateArray()
        .Where(m => m.GetProperty("role").GetString() == "user").Select(m =>
            m.GetProperty("content").ValueKind == JsonValueKind.String ? m.GetProperty("content").GetString()! :
            string.Join("\n", m.GetProperty("content").EnumerateArray().Where(c => c.GetProperty("type").GetString() == "text")
                .Select(c => c.GetProperty("text").GetString()))).ToArray();
    public string RunId => Regex.Match(UserMessages.Last(), "run_id=\"([^\"]+)\"").Groups[1].Value;

    public Task TextAsync(string text) => DeltaAsync(new { role = "assistant", content = text });

    public async Task ToolAsync(string name, object arguments)
    {
        await DeltaAsync(new { role = "assistant", tool_calls = new[]
        {
            new { index = 0, id = "call_" + Guid.NewGuid().ToString("N"), type = "function",
                function = new { name, arguments = JsonSerializer.Serialize(arguments, Json.Options) } }
        } });
        await FinishAsync("tool_calls");
    }

    public Task ResultAsync(string text) => ToolAsync("ompmcp_agent_result", new { run_id = RunId, text });
    public Task NotifyAsync(string text) => ToolAsync("ompmcp_agent_notify", new { run_id = RunId, text });
    public async Task EndAsync(string text = "Done.") { await TextAsync(text); await FinishAsync("stop"); }

    private async Task DeltaAsync(object delta, string? finish = null)
    {
        Assert.True(Body.GetProperty("stream").GetBoolean());
        if (!context.Response.HasStarted) context.Response.ContentType = "text/event-stream";
        var chunk = new { id = "chatcmpl-test", @object = "chat.completion.chunk", created = 1,
            model = Body.GetProperty("model").GetString(), choices = new[] { new { index = 0, delta, finish_reason = finish } } };
        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(chunk) + "\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private async Task FinishAsync(string reason)
    {
        await DeltaAsync(new { }, reason);
        await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
        Completed.TrySetResult();
    }
}

internal sealed class TestEnvironment : IAsyncDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "ompmcp-test-" + Guid.NewGuid().ToString("N"));
    public string AgentDirectory => Path.Combine(DirectoryPath, "agent");
    public string Workspace => Path.Combine(DirectoryPath, "workspace");
    public FakeProvider Provider { get; private set; } = null!;
    public AgentManager Manager { get; private set; } = null!;
    public ServerOptions Options { get; private set; } = null!;

    public static async Task<TestEnvironment> CreateAsync(int eventCapacity = 4096)
    {
        var env = new TestEnvironment();
        Directory.CreateDirectory(env.AgentDirectory);
        Directory.CreateDirectory(env.Workspace);
        env.Provider = await FakeProvider.StartAsync();
        await File.WriteAllTextAsync(Path.Combine(env.AgentDirectory, "models.yml"), JsonSerializer.Serialize(new
        {
            providers = new Dictionary<string, object>
            {
                ["ompmcp-test"] = new { baseUrl = env.Provider.Url + "/v1", api = "openai-completions", auth = "none",
                    models = new[] { new { id = "first", reasoning = false, input = new[] { "text" }, contextWindow = 32000, maxTokens = 2048 },
                        new { id = "second", reasoning = false, input = new[] { "text" }, contextWindow = 32000, maxTokens = 2048 } } }
            }
        }));
        await File.WriteAllTextAsync(Path.Combine(env.AgentDirectory, "config.yml"), "modelRoles:\n  default: ompmcp-test/first\n");
        env.Options = new() { AgentDirectory = env.AgentDirectory, EventCapacity = eventCapacity,
            OmpPath = Environment.GetEnvironmentVariable("OMPMCP_TEST_OMP") ?? "omp" };
        env.Manager = new(env.Options);
        return env;
    }

    public Task<AgentInfo> AgentAsync(string? model = null, string[]? tools = null) =>
        Manager.CreateAsync(Workspace, model, tools: tools ?? [], thinking: "off");

    public Task<WaitReply> CompleteAsync(Submission submission) => Manager.WaitAsync(submission.AgentId, submission.RunId,
        "completion", null, TimeSpan.FromSeconds(30));

    public async Task FinishResultAsync(string text)
    {
        var request = await Provider.NextAsync();
        await request.ResultAsync(text);
        await (await Provider.NextAsync()).EndAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Manager.DisposeAsync();
        await Provider.DisposeAsync();
        if (Directory.Exists(Manager.RuntimeDirectory)) Directory.Delete(Manager.RuntimeDirectory, true);
        Directory.Delete(DirectoryPath, true);
    }
}
