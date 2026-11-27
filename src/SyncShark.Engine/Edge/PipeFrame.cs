using System.Buffers.Binary;
using System.Text;
using System.Text.Unicode;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>One kind byte and a little-endian payload length. The predecessor protocol caps payloads at one MiB.</summary>
public static class PipeFrame
{
    internal const byte Arguments = 0, Accepted = 1, Output = 2, Error = 3, Exit = 4;
    internal const int MaximumPayload = 1024 * 1024;
    internal const int Header = sizeof(byte) + sizeof(uint);

    /// <summary>Write the kind and payload of one message as a single frame.</summary>
    public static async Task WriteAsync(Stream pipe, ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        Validate(message.Span);
        var wire = new byte[sizeof(uint) + message.Length];
        wire[0] = message.Span[0];
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(1), (uint)(message.Length - 1));
        message[1..].CopyTo(wire.AsMemory(Header));
        await pipe.WriteAsync(wire, ct);
        await pipe.FlushAsync(ct);
    }

    /// <summary>Reject payloads over one MiB before allocation; return the kind followed by its payload.</summary>
    public static async Task<byte[]> ReadAsync(Stream pipe, CancellationToken ct)
    {
        var header = new byte[Header];
        await pipe.ReadExactlyAsync(header, ct);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(sizeof(byte)));
        if (size > MaximumPayload) throw new InvalidDataException(nameof(size));
        var message = new byte[1 + (int)size];
        message[0] = header[0];
        await pipe.ReadExactlyAsync(message.AsMemory(1), ct);
        Validate(message);
        return message;
    }

    internal static void Validate(ReadOnlySpan<byte> message)
    {
        if (message.IsEmpty || message.Length - 1 > MaximumPayload) throw new InvalidDataException(nameof(message));
        var payload = message[sizeof(byte)..];
        if (!(message[0] switch {
            Arguments => !payload.IsEmpty && Utf8.IsValid(payload), Accepted => payload.IsEmpty,
            Output or Error => Utf8.IsValid(payload), Exit => payload.Length == sizeof(int), _ => false
        })) throw new InvalidDataException(nameof(message));
    }
}
