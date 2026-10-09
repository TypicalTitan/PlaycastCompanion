namespace PlaycastCompanion.LogAnalysis;

internal sealed class BoundedLogLineReader(int maxLineBytes)
{
    public async Task ReadAsync(Stream stream, ImportAccumulator state,
        Action<ReadOnlyMemory<byte>, int, bool> accept, CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        using var line = new MemoryStream();
        var lineNumber = 1;
        var oversized = false;
        while (!state.Stopped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.RemainingBytes == 0)
            {
                // One probe distinguishes an exact-size file from growth without retaining bytes.
                if (await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) > 0)
                { state.ByteLimitReached = true; state.Stopped = true; }
                break;
            }
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, state.RemainingBytes)),
                cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            state.BytesRead += count;
            for (var index = 0; index < count && !state.Stopped; index++)
            {
                if (buffer[index] == (byte)'\n')
                {
                    accept(line.GetBuffer().AsMemory(0, (int)line.Length), lineNumber++, oversized);
                    line.SetLength(0);
                    oversized = false;
                }
                else if (!oversized)
                {
                    if (line.Length >= maxLineBytes) { oversized = true; line.SetLength(0); }
                    else line.WriteByte(buffer[index]);
                }
            }
        }
        if (!state.Stopped && (line.Length > 0 || oversized))
            accept(line.GetBuffer().AsMemory(0, (int)line.Length), lineNumber, oversized);
    }
}
