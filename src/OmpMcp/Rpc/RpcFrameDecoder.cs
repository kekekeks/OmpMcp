using System.Text;
using System.Text.Json;

namespace OmpMcp.Rpc;

internal sealed class RpcFrameDecoder
{
    public const int MaxFrameBytes = 1024 * 1024;
    public const int MaxLogicalBytes = 64 * 1024 * 1024;
    private MemoryStream? pending;
    private string? chunkId;
    private int index, count, length;

    public JsonElement? Push(string line)
    {
        var frame = JsonSerializer.Deserialize<JsonElement>(line);
        if (frame.ValueKind != JsonValueKind.Object) throw new InvalidDataException("RPC frame must be an object.");
        if (!frame.TryGetProperty("type", out var type) || type.GetString() != "rpc_chunk")
        {
            if (pending is not null) throw new InvalidDataException("RPC chunk sequence was interrupted.");
            return frame;
        }
        var nextId = frame.GetProperty("chunkId").GetString();
        var nextIndex = frame.GetProperty("index").GetInt32();
        var nextCount = frame.GetProperty("count").GetInt32();
        var nextLength = frame.GetProperty("byteLength").GetInt32();
        if (string.IsNullOrEmpty(nextId) || nextId.Length > 128 || nextCount is < 2 or > 256 ||
            nextIndex < 0 || nextIndex >= nextCount || nextLength < MaxFrameBytes || nextLength > MaxLogicalBytes)
            throw new InvalidDataException("Invalid RPC chunk metadata.");
        var encoded = frame.GetProperty("data").GetString()!;
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length is 0 or > 262144 || Convert.ToBase64String(bytes) != encoded)
            throw new InvalidDataException("Invalid RPC chunk data.");
        if (pending is null)
        {
            if (nextIndex != 0) throw new InvalidDataException("RPC chunk sequence must start at zero.");
            (chunkId, count, length, index) = (nextId, nextCount, nextLength, 0);
            pending = new MemoryStream();
        }
        if (nextId != chunkId || nextCount != count || nextLength != length || nextIndex != index++)
            throw new InvalidDataException("RPC chunk sequence mismatch.");
        pending.Write(bytes);
        if (pending.Length > length) throw new InvalidDataException("RPC chunk exceeds its declared length.");
        if (index != count) return null;
        if (pending.Length != length) throw new InvalidDataException("RPC chunk length mismatch.");
        var json = new UTF8Encoding(false, true).GetString(pending.GetBuffer(), 0, length);
        pending.Dispose();
        pending = null;
        var result = JsonSerializer.Deserialize<JsonElement>(json);
        if (result.ValueKind != JsonValueKind.Object || result.GetProperty("type").GetString() == "rpc_chunk")
            throw new InvalidDataException("Invalid reassembled RPC frame.");
        return result;
    }

    public void Finish()
    {
        if (pending is not null) throw new InvalidDataException("The process ended inside an RPC chunk sequence.");
    }
}
