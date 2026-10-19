using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OmpMcp.Rpc;
using Xunit;

namespace OmpMcp.Tests;

public sealed class RpcTests
{
    private static OmpRpc Fake(string scenario)
    {
        var start = new ProcessStartInfo("python3") { RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake_rpc.py"));
        start.ArgumentList.Add(scenario);
        return new(start, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Terminal_boundary_preserves_accepted_messages_and_nonterminal_end_does_not_complete()
    {
        var events = new EventStore(100);
        await using var rpc = Fake("boundary");
        await using var agent = new Agent("test-agent", Path.GetTempPath(), rpc, events);
        await agent.InitializeAsync("fake/model", CancellationToken.None);
        var first = await agent.SendAsync("First request.", CancellationToken.None);
        var second = await agent.SendAsync("Steering at the completion boundary.", CancellationToken.None);
        Assert.Equal(first.RunId, second.RunId);
        long cursor = 0;
        while (true)
        {
            var snapshot = events.Read(cursor);
            if (snapshot.Page.Events.Any(e => e.Type == "result_received")) break;
            cursor = snapshot.Page.Cursor;
            await snapshot.Changed.WaitAsync(TimeSpan.FromSeconds(5));
        }
        // A command response is a fence after the fake peer's nonterminal frame.
        await rpc.CommandAsync("get_state");
        Assert.Equal("running", agent.GetRun(first.RunId).Status);
        await rpc.CommandAsync("finish");
        var result = agent.GetRun(first.RunId);
        Assert.Equal("succeeded", result.Status);
        Assert.Equal(2, result.DeliveredMessages);
        Assert.Single(events.Read(0).Page.Events, e => e.Type == "run_completed");
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("crash")]
    public async Task Fake_child_failures_are_explicit(string scenario)
    {
        await using var rpc = Fake(scenario);
        if (scenario == "malformed")
        {
            await Assert.ThrowsAnyAsync<Exception>(() => rpc.StartAsync(CancellationToken.None));
            return;
        }
        await rpc.StartAsync(CancellationToken.None);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.Failed = e => failure.TrySetResult(e);
        await rpc.CommandAsync("prompt", new { message = "crash" });
        Assert.IsAssignableFrom<IOException>(await failure.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Chunk_decoder_is_lossless_and_rejects_interleaving_and_invalid_order()
    {
        var expected = Json.Element(new { type = "test", text = new string('x', 1_100_000) }).GetRawText();
        var bytes = Encoding.UTF8.GetBytes(expected);
        var count = (bytes.Length + 262143) / 262144;
        var chunks = Enumerable.Range(0, count).Select(i => Json.Element(new
        {
            type = "rpc_chunk", chunkId = "chunk-1", index = i, count, byteLength = bytes.Length,
            data = Convert.ToBase64String(bytes.AsSpan(i * 262144, Math.Min(262144, bytes.Length - i * 262144)))
        }).GetRawText()).ToArray();
        var decoder = new RpcFrameDecoder();
        for (var i = 0; i < chunks.Length - 1; i++) Assert.Null(decoder.Push(chunks[i]));
        Assert.Equal(expected, decoder.Push(chunks[^1])!.Value.GetRawText());
        decoder = new();
        Assert.Null(decoder.Push(chunks[0]));
        Assert.Throws<InvalidDataException>(() => decoder.Push("{\"type\":\"agent_end\"}"));
        Assert.Throws<InvalidDataException>(() => new RpcFrameDecoder().Push(chunks[1]));
        var incomplete = new RpcFrameDecoder();
        incomplete.Push(chunks[0]);
        Assert.Throws<InvalidDataException>(incomplete.Finish);
    }

    [Fact]
    public async Task Json_lines_reject_oversized_or_incomplete_frames()
    {
        async Task Read(string input, int limit)
        {
            await foreach (var _ in JsonLines.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(input)), limit)) { }
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(new string('x', 20) + "\n", 10));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read("{\"type\":\"unfinished\"}", 100));
    }

    [Fact]
    public void Expired_event_cursors_are_explicit_and_reads_do_not_consume_events()
    {
        var events = new EventStore(2);
        events.Add("a", "r", "one", new { });
        events.Add("a", "r", "two", new { });
        Assert.Equal(2, events.Read(0).Page.Events.Count);
        Assert.Equal(2, events.Read(0).Page.Events.Count);
        events.Add("a", "r", "three", new { });
        Assert.Equal("expired_cursor", Assert.Throws<OmpMcpException>(() => events.Read(0)).Code);
        Assert.Equal(new[] { "two", "three" }, events.Read(1).Page.Events.Select(e => e.Type));
    }
}
