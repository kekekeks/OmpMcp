using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace OmpMcp;

internal static class McpNotifications
{
    // Notification delivery is best effort. The event log and wait endpoint are authoritative.
    public static async Task RunAsync(McpServer server, EventStore events, CancellationToken cancellationToken)
    {
        long cursor = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                (EventPage Page, Task Changed, bool Stopped) snapshot;
                try { snapshot = events.Read(cursor); }
                catch (OmpMcpException e) when (e.Code == "expired_cursor")
                {
                    cursor = events.Cursor;
                    await server.SendNotificationAsync("notifications/message", new LoggingMessageNotificationParams
                    {
                        Level = LoggingLevel.Warning, Logger = "ompmcp",
                        Data = Json.Element(new { type = "notification_history_gap", cursor })
                    }, Json.Options, cancellationToken);
                    continue;
                }
                foreach (var item in snapshot.Page.Events)
                {
                    if (server.LoggingLevel is { } level && level > LoggingLevel.Info) continue;
                    await server.SendNotificationAsync("notifications/message", new LoggingMessageNotificationParams
                    {
                        Level = LoggingLevel.Info, Logger = "ompmcp", Data = Json.Element(item)
                    }, Json.Options, cancellationToken);
                }
                cursor = snapshot.Page.Cursor;
                if (snapshot.Stopped) return;
                await snapshot.Changed.WaitAsync(cancellationToken);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
    }
}
