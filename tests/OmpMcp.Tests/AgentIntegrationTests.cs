using System.Diagnostics;
using Xunit;

namespace OmpMcp.Tests;

public sealed class AgentIntegrationTests
{
    [Fact]
    public async Task Concurrent_agents_keep_results_and_events_separate()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var agents = await Task.WhenAll(env.AgentAsync(), env.AgentAsync("ompmcp-test/second"));
        var submissions = await Task.WhenAll(env.Manager.SendAsync(agents[0].AgentId, "Alpha task."),
            env.Manager.SendAsync(agents[1].AgentId, "Beta task."));
        var first = await env.Provider.NextAsync();
        var second = await env.Provider.NextAsync();
        await Task.WhenAll(first.ResultAsync(first.RunId), second.ResultAsync(second.RunId));
        await (await env.Provider.NextAsync()).EndAsync();
        await (await env.Provider.NextAsync()).EndAsync();
        foreach (var submission in submissions)
        {
            Assert.Equal(submission.RunId, (await env.CompleteAsync(submission)).Run!.Result);
            Assert.All(env.Manager.ReadEvents(0, submission.AgentId, submission.RunId).Events,
                e => Assert.Equal(submission.AgentId, e.AgentId));
        }
    }

    [Fact]
    public async Task Retained_results_outlive_event_history_and_agent_closure()
    {
        await using var env = await TestEnvironment.CreateAsync(eventCapacity: 2);
        var agent = await env.AgentAsync();
        var run = await env.Manager.SendAsync(agent.AgentId, "A retained result.");
        await env.FinishResultAsync("Still available.");
        Assert.Equal("succeeded", (await env.CompleteAsync(run)).Run!.Status);
        await env.Manager.CloseAsync(agent.AgentId);
        Assert.Equal("expired_cursor", Assert.Throws<OmpMcpException>(() => env.Manager.ReadEvents(0)).Code);
        Assert.Equal("Still available.", env.Manager.GetRun(agent.AgentId, run.RunId).Result);
        Assert.Equal("Still available.", (await env.CompleteAsync(run)).Run!.Result);
    }

    [Fact]
    public async Task Models_defaults_selection_and_unavailable_models()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var models = await env.Manager.ModelsAsync("ompmcp-test");
        Assert.Equal(new[] { "ompmcp-test/first", "ompmcp-test/second" }, models.Select(m => m.Id));
        var first = await env.AgentAsync();
        var second = await env.AgentAsync("ompmcp-test/second");
        Assert.Equal("ompmcp-test/first", first.Model!.Id);
        Assert.Equal("ompmcp-test/second", second.Model!.Id);
        Assert.Equal("modelRoles:\n  default: ompmcp-test/first\n", await File.ReadAllTextAsync(Path.Combine(env.AgentDirectory, "config.yml")));
        await Assert.ThrowsAnyAsync<Exception>(() => env.AgentAsync("ompmcp-test/missing"));
        Assert.Equal(2, env.Manager.List().Length);
    }

    [Fact]
    public async Task Results_notifications_context_and_multiple_waiters()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var agent = await env.AgentAsync();
        var first = await env.Manager.SendAsync(agent.AgentId, "Remember the marker ORCHID-731.");
        var waiters = Enumerable.Range(0, 3).Select(_ => env.CompleteAsync(first)).ToArray();
        var request = await env.Provider.NextAsync();
        Assert.DoesNotContain(request.Body.GetProperty("tools").EnumerateArray(), t => t.GetProperty("function").GetProperty("name").GetString() == "task");
        await request.NotifyAsync("Working on it.");
        await env.FinishResultAsync("Stored ORCHID-731.");
        foreach (var result in await Task.WhenAll(waiters)) Assert.Equal("succeeded", result.Run!.Status);
        Assert.Equal("Stored ORCHID-731.", (await env.CompleteAsync(first)).Run!.Result);
        Assert.Contains(env.Manager.ReadEvents(0).Events, e => e.Type == "notification");
        var second = await env.Manager.SendAsync(agent.AgentId, "Recall the marker.");
        var recall = await env.Provider.NextAsync();
        Assert.Contains(recall.UserMessages, text => text.Contains("ORCHID-731"));
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Equal(agent.ProcessId, env.Manager.Get(agent.AgentId).ProcessId);
        await recall.ResultAsync("ORCHID-731");
        await (await env.Provider.NextAsync()).EndAsync();
        Assert.Equal("ORCHID-731", (await env.CompleteAsync(second)).Run!.Result);
        Assert.Empty(Directory.EnumerateFiles(env.AgentDirectory, "*.jsonl", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Steering_during_streaming_is_delivered_and_invalidates_an_earlier_result()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var agent = await env.AgentAsync();
        var run = await env.Manager.SendAsync(agent.AgentId, "Start the task.");
        var request = await env.Provider.NextAsync();
        await request.TextAsync("Initial work.");
        var steering = await env.Manager.SendAsync(agent.AgentId, "Use the revised instruction.");
        Assert.Equal(run.RunId, steering.RunId);
        Assert.Equal("steer", steering.Mode);
        await request.ResultAsync("An outdated result.");
        var next = await env.Provider.NextAsync();
        Assert.Contains(next.UserMessages, m => m.Contains("revised instruction"));
        await next.ResultAsync("Revised result.");
        await (await env.Provider.NextAsync()).EndAsync();
        var result = (await env.CompleteAsync(run)).Run!;
        Assert.Equal("succeeded", result.Status);
        Assert.Equal("Revised result.", result.Result);
        Assert.Equal(2, result.DeliveredMessages);
    }

    [Fact]
    public async Task Steering_while_a_builtin_tool_is_pending()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var agent = await env.AgentAsync(tools: ["bash"]);
        var run = await env.Manager.SendAsync(agent.AgentId, "Run the controlled tool.");
        var request = await env.Provider.NextAsync();
        await request.ToolAsync("bash", new { command = "python3 -c \"import urllib.request; print(urllib.request.urlopen('" + env.Provider.Url + "/gate').read().decode())\"" });
        var gate = await env.Provider.NextGateAsync();
        var steering = await env.Manager.SendAsync(agent.AgentId, "Report the gate release.");
        Assert.Equal(run.RunId, steering.RunId);
        gate.TrySetResult();
        var next = await env.Provider.NextAsync();
        Assert.Contains(next.UserMessages, m => m.Contains("gate release"));
        await next.ResultAsync("The gate was released.");
        await (await env.Provider.NextAsync()).EndAsync();
        Assert.Equal("succeeded", (await env.CompleteAsync(run)).Run!.Status);
    }

    [Fact]
    public async Task Missing_and_duplicate_results_have_explicit_outcomes()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var agent = await env.AgentAsync();
        var missing = await env.Manager.SendAsync(agent.AgentId, "Do not call the result tool.");
        await (await env.Provider.NextAsync()).EndAsync("An ordinary response.");
        Assert.Equal("incomplete", (await env.CompleteAsync(missing)).Run!.Status);
        var duplicate = await env.Manager.SendAsync(agent.AgentId, "Provide a result.");
        await (await env.Provider.NextAsync()).ResultAsync("first result");
        await (await env.Provider.NextAsync()).ResultAsync("duplicate result");
        Assert.Equal("running", env.Manager.GetRun(agent.AgentId, duplicate.RunId).Status);
        await (await env.Provider.NextAsync()).EndAsync();
        Assert.Equal("first result", (await env.CompleteAsync(duplicate)).Run!.Result);
        var events = env.Manager.ReadEvents(0, agent.AgentId, duplicate.RunId).Events;
        Assert.Single(events, e => e.Type == "result_received");
        Assert.Single(events, e => e.Type == "run_completed");
    }

    [Fact]
    public async Task Wait_cancellation_timeout_run_cancellation_and_reuse_are_independent()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var agent = await env.AgentAsync();
        var run = await env.Manager.SendAsync(agent.AgentId, "Wait for steering.");
        _ = await env.Provider.NextAsync();
        using var cancel = new CancellationTokenSource();
        var waiter = env.Manager.WaitAsync(agent.AgentId, run.RunId, "completion", null, TimeSpan.FromSeconds(30), cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.Equal("running", env.Manager.GetRun(agent.AgentId, run.RunId).Status);
        Assert.Equal("timeout", (await env.Manager.WaitAsync(agent.AgentId, run.RunId, "completion", null, TimeSpan.FromMilliseconds(10))).Status);
        Assert.Equal("cancelled", (await env.Manager.CancelAsync(agent.AgentId, run.RunId)).Status);
        var resumed = await env.Manager.SendAsync(agent.AgentId, "A new request after cancellation.");
        await env.FinishResultAsync("Resumed.");
        Assert.Equal("succeeded", (await env.CompleteAsync(resumed)).Run!.Status);
    }

    [Fact]
    public async Task Process_failure_and_agent_close_release_waiters()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var agent = await env.AgentAsync();
        var run = await env.Manager.SendAsync(agent.AgentId, "Wait.");
        _ = await env.Provider.NextAsync();
        var waiter = env.CompleteAsync(run);
        Process.GetProcessById(agent.ProcessId!.Value).Kill(entireProcessTree: true);
        Assert.Equal("failed", (await waiter).Run!.Status);
        await Assert.ThrowsAsync<OmpMcpException>(() => env.Manager.SendAsync(agent.AgentId, "More work."));
        var second = await env.AgentAsync();
        var closed = await env.Manager.SendAsync(second.AgentId, "Wait again.");
        _ = await env.Provider.NextAsync();
        var closedWaiter = env.CompleteAsync(closed);
        await env.Manager.CloseAsync(second.AgentId);
        Assert.Equal("cancelled", (await closedWaiter).Run!.Status);
        await Assert.ThrowsAsync<OmpMcpException>(() => env.Manager.SendAsync(second.AgentId, "More work."));
    }
}
