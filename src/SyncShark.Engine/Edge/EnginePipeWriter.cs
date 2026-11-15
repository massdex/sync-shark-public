using System.Buffers;
using System.IO.Pipes;
using System.Text;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>Streams one host command's standard output or standard error to the engine host pipe as UTF-8 chunks
/// in EngineHostFrame frames (design 8.8, V3 C16). Text is written as it arrives; nothing accumulates.</summary>
public sealed class EnginePipeWriter : TextWriter
{
    private readonly Stream _pipe;
    private readonly EngineHostFrameKind _kind;
    private readonly int _payloadCapacity;
    private readonly object _gate = new();
    private Task _previous = Task.CompletedTask;

    /// <summary>Creates the writer for one output stream of the host pipe; the chunk size follows the pipe's buffer.</summary>
    public EnginePipeWriter(Stream pipe, EngineHostFrameKind kind)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (kind is not (EngineHostFrameKind.StandardOutput or EngineHostFrameKind.StandardError))
            throw new ArgumentOutOfRangeException(nameof(kind));
        _pipe = pipe;
        _kind = kind;
        _payloadCapacity = PayloadCapacity(pipe);
    }

    /// <summary>The pipe carries UTF-8 text frames.</summary>
    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value) => Write(value.ToString());

    public override void Write(string? value)
    {
        if (!string.IsNullOrEmpty(value)) WriteTextAsync(value).GetAwaiter().GetResult();
    }

    public override void WriteLine(string? value) => Write(value + NewLine);

    public override Task WriteAsync(char value) => WriteAsync(value.ToString());

    public override Task WriteAsync(string? value) =>
        string.IsNullOrEmpty(value) ? Task.CompletedTask : WriteTextAsync(value);

    public override Task WriteLineAsync(string? value) => WriteAsync(value + NewLine);

    /// <summary>Each frame is written and flushed when it is emitted, so nothing is buffered here.</summary>
    public override Task FlushAsync() => Task.CompletedTask;

    // TextWriter's synchronous callbacks write on the command worker; blocking here is the pipe backpressure
    // point, not an asynchronous path.
    private async Task WriteTextAsync(string value)
    {
        Task previous;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            previous = _previous;
            _previous = completion.Task;
        }
        await previous.ConfigureAwait(false);
        try
        {
            var payload = ArrayPool<byte>.Shared.Rent(_payloadCapacity);
            try
            {
                var encoder = Encoding.UTF8.GetEncoder();
                var offset = 0;
                while (offset < value.Length)
                {
                    var (charsUsed, bytesUsed, completed) = ConvertChunk(encoder, value, offset, payload, _payloadCapacity);
                    if (bytesUsed == 0) throw new InvalidOperationException();
                    await EngineHostFrame.WriteAsync(_pipe, new EngineHostFrame(_kind, payload.AsMemory(..bytesUsed)),
                        CancellationToken.None).ConfigureAwait(false);
                    offset += charsUsed;
                    if (completed) break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
        finally
        {
            completion.SetResult();
        }
    }

    // Span work stays in a synchronous helper; a span local cannot cross an await in this async method.
    private static (int CharsUsed, int BytesUsed, bool Completed) ConvertChunk(Encoder encoder, string value,
        int offset, byte[] payload, int capacity)
    {
        encoder.Convert(value.AsSpan(offset), payload.AsSpan()[..capacity], flush: true,
            out var charsUsed, out var bytesUsed, out var completed);
        return (charsUsed, bytesUsed, completed);
    }

    private static int PayloadCapacity(Stream pipe)
    {
        var buffer = pipe is PipeStream stream && stream.OutBufferSize > 0
            ? stream.OutBufferSize
            : AutoSyncHostProtocol.MaximumFrameBytes;
        // A frame's payload cannot exceed the shared protocol bound even when the pipe buffer is larger.
        return Math.Min(buffer, AutoSyncHostProtocol.MaximumFrameBytes);
    }
}
