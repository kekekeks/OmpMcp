using System.Runtime.CompilerServices;
using System.Text;

namespace OmpMcp.Rpc;

internal static class JsonLines
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async IAsyncEnumerable<string> ReadAsync(Stream stream, int maxBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new byte[8192];
        using var line = new MemoryStream();
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            var start = 0;
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != '\n') continue;
                if (line.Length + i - start + 1 > maxBytes) throw new InvalidDataException("JSON line exceeds the transport limit.");
                line.Write(buffer, start, i - start);
                var text = Utf8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
                line.SetLength(0);
                start = i + 1;
                if (text.Length != 0) yield return text;
            }
            if (line.Length + count - start >= maxBytes) throw new InvalidDataException("JSON line exceeds the transport limit.");
            line.Write(buffer, start, count - start);
        }
        if (line.Length != 0) throw new InvalidDataException("The stream ended inside a JSON line.");
    }
}
