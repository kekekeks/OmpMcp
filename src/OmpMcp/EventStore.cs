namespace OmpMcp;

public sealed class EventStore(int capacity)
{
    private readonly object sync = new();
    private readonly Queue<AgentEvent> events = new();
    private long cursor;
    private bool stopped;
    private TaskCompletionSource changed = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public long Cursor { get { lock (sync) return cursor; } }

    public void Add(string agentId, string? runId, string type, object data)
    {
        lock (sync)
        {
            events.Enqueue(new(++cursor, DateTimeOffset.UtcNow, agentId, runId, type, Json.Element(data)));
            while (events.Count > capacity) events.Dequeue();
            var signal = changed;
            changed = NewSignal();
            signal.TrySetResult();
        }
    }

    public (EventPage Page, Task Changed, bool Stopped) Read(long after, string? agentId = null, string? runId = null)
    {
        lock (sync)
        {
            var oldest = events.TryPeek(out var first) ? first.Cursor : cursor + 1;
            if (after < 0 || after > cursor)
                throw new OmpMcpException("invalid_cursor", "The event cursor is outside this server's history.");
            if (after < oldest - 1)
                throw new OmpMcpException("expired_cursor", $"Event history expired. The oldest available cursor is {oldest}.");
            var matches = events.Where(e => e.Cursor > after &&
                (agentId is null || e.AgentId == agentId) && (runId is null || e.RunId == runId)).ToArray();
            return (new(matches, cursor, oldest), changed.Task, stopped);
        }
    }

    public void Stop()
    {
        lock (sync)
        {
            stopped = true;
            changed.TrySetResult();
        }
    }
}
