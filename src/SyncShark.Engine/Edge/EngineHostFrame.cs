using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>One frame of the one-command engine host pipe (design 8.8, V3 C16): a one-byte kind, a four-byte
/// unsigned little-endian payload length and the payload. EngineHostServer and EngineHostClient read and write
/// frames through this type, and EnginePipeWriter emits its standard-output and standard-error chunks. Payload
/// length is bounded by the shared host-protocol bound AutoSyncHostProtocol.MaximumFrameBytes.</summary>
public readonly record struct EngineHostFrame(EngineHostFrameKind Kind, ReadOnlyMemory<byte> Payload)
{
    /// <summary>Wraps a UTF-8 JSON string array as an Arguments frame.</summary>
    public static EngineHostFrame CreateArguments(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new EngineHostFrame(EngineHostFrameKind.Arguments, JsonSerializer.SerializeToUtf8Bytes(arguments));
    }

    public static EngineHostFrame CreateStatus(HostStatusUpdate status) =>
        new(EngineHostFrameKind.Status, JsonSerializer.SerializeToUtf8Bytes(status));

    public HostStatusUpdate DecodeStatus() => Kind == EngineHostFrameKind.Status
        ? JsonSerializer.Deserialize<HostStatusUpdate>(Payload.Span) ?? throw new InvalidDataException(nameof(Payload))
        : throw new InvalidDataException(nameof(Kind));

    /// <summary>Encodes a signed 32-bit exit code as an Exit frame.</summary>
    public static EngineHostFrame CreateExit(int exitCode)
    {
        var payload = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(payload, exitCode);
        return new EngineHostFrame(EngineHostFrameKind.Exit, payload);
    }

    /// <summary>Decodes and validates the Arguments frame's UTF-8 JSON string array.</summary>
    public string[] DecodeArguments()
    {
        if (Kind != EngineHostFrameKind.Arguments) throw new InvalidDataException(nameof(Kind));
        if (!Utf8.IsValid(Payload.Span)) throw new InvalidDataException(nameof(Payload));
        using var document = JsonDocument.Parse(Payload);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException(nameof(Payload));
        var values = new List<string>(document.RootElement.GetArrayLength());
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String) throw new InvalidDataException(nameof(Payload));
            values.Add(element.GetString() ?? throw new InvalidDataException(nameof(Payload)));
        }
        return values.ToArray();
    }

    /// <summary>Decodes the Exit frame's signed 32-bit exit code.</summary>
    public int DecodeExitCode()
    {
        if (Kind != EngineHostFrameKind.Exit || Payload.Length != sizeof(int)) throw new InvalidDataException(nameof(Kind));
        return BinaryPrimitives.ReadInt32LittleEndian(Payload.Span);
    }

    /// <summary>Decodes a standard-output or standard-error frame's UTF-8 chunk.</summary>
    public string DecodeText()
    {
        if (Kind is not (EngineHostFrameKind.StandardOutput or EngineHostFrameKind.StandardError))
            throw new InvalidDataException(nameof(Kind));
        if (!Utf8.IsValid(Payload.Span)) throw new InvalidDataException(nameof(Payload));
        return Encoding.UTF8.GetString(Payload.Span);
    }

    /// <summary>Validates the frame and writes its header and payload in one write.</summary>
    public static async Task WriteAsync(Stream stream, EngineHostFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Validate(frame);
        var total = sizeof(byte) + sizeof(uint) + frame.Payload.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            FillHeader(buffer, frame, total);
            await stream.WriteAsync(buffer.AsMemory()[..total], ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Span work stays in a synchronous helper; a span local cannot cross an await in this async method.
    private static void FillHeader(byte[] buffer, EngineHostFrame frame, int total)
    {
        var span = buffer.AsSpan()[..total];
        span[0] = (byte)frame.Kind;
        BinaryPrimitives.WriteUInt32LittleEndian(span[sizeof(byte)..], (uint)frame.Payload.Length);
        frame.Payload.Span.CopyTo(span[(sizeof(byte) + sizeof(uint))..]);
    }

    /// <summary>Reads one frame; null only for a clean end of stream before any header byte.</summary>
    public static async Task<EngineHostFrame?> ReadAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(byte) + sizeof(uint)];
        var headerRead = await ReadUpToAsync(stream, header, ct).ConfigureAwait(false);
        if (headerRead == 0) return null;
        if (headerRead != header.Length) throw new InvalidDataException(nameof(Payload));
        var kind = (EngineHostFrameKind)header[0];
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan()[sizeof(byte)..]);
        if (length > AutoSyncHostProtocol.MaximumFrameBytes) throw new InvalidDataException(nameof(Payload));
        var payload = new byte[(int)length];
        if (await ReadUpToAsync(stream, payload, ct).ConfigureAwait(false) != payload.Length)
            throw new InvalidDataException(nameof(Payload));
        var frame = new EngineHostFrame(kind, payload);
        Validate(frame);
        return frame;
    }

    private static async Task<int> ReadUpToAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        if (buffer.IsEmpty) return buffer.Length;
        var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
        if (read == 0) return read;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (count == 0) break;
            read += count;
        }
        return read;
    }

    private static void Validate(EngineHostFrame frame)
    {
        if (!Enum.IsDefined(frame.Kind)) throw new InvalidDataException(nameof(Kind));
        if (frame.Payload.Length > AutoSyncHostProtocol.MaximumFrameBytes) throw new InvalidDataException(nameof(Payload));
        switch (frame.Kind)
        {
            case EngineHostFrameKind.Arguments:
                // The JSON shape is checked by DecodeArguments before the server executes the arguments.
                if (frame.Payload.IsEmpty || !Utf8.IsValid(frame.Payload.Span)) throw new InvalidDataException(nameof(Payload));
                break;
            case EngineHostFrameKind.Accepted:
            case EngineHostFrameKind.SubscribeStatus:
                if (!frame.Payload.IsEmpty) throw new InvalidDataException(nameof(Payload));
                break;
            case EngineHostFrameKind.Exit:
                if (frame.Payload.Length != sizeof(int)) throw new InvalidDataException(nameof(Payload));
                break;
            default:
                if (!Utf8.IsValid(frame.Payload.Span)) throw new InvalidDataException(nameof(Payload));
                break;
        }
    }
}
