using System.Runtime.CompilerServices;

namespace PlaycastCompanion;

/// <summary>Reads UTF-8 newline frames with a hard limit before allocating a complete frame.</summary>
public sealed class DiagnosticFrameReader
{
    public async IAsyncEnumerable<byte[]> ReadAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new byte[16 * 1024];
        using var frame = new MemoryStream();
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (frame.Length != 0)
                    throw new InvalidDataException("Diagnostic feed disconnected before the final newline.");
                yield break;
            }
            var position = 0;
            while (position < count)
            {
                var newline = Array.IndexOf(buffer, (byte)'\n', position, count - position);
                var end = newline < 0 ? count : newline;
                var length = end - position;
                if (frame.Length + length > SessionDiagnosticsListener.MaxFrameBytes)
                    throw new InvalidDataException("Diagnostic frame exceeded the 1 MiB limit.");
                frame.Write(buffer, position, length);
                position = end + (newline >= 0 ? 1 : 0);
                if (newline >= 0)
                {
                    if (frame.Length > 0)
                        yield return frame.ToArray();
                    frame.SetLength(0);
                }
            }
        }
    }
}
