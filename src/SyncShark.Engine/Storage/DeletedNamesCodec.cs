using System.IO.Compression;
using System.Text;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Owns the v16 deleted-name block codec: one sorted zlib block of UTF-8 keys.</summary>
internal static class DeletedNamesCodec
{
    // v16 persisted capacity of discovery_deleted_block.name_count (std 2.4 persisted encoding).
    public const int DeletedBlockRows = 5000;
    // v16: at most three UTF-8 bytes per UTF-16 unit of a Windows extended path.
    private const int MaximumDeletedKeyBytes = 32768 * 3;
    private static readonly UTF8Encoding HistoryUtf8 = new(false, true);

    /// <summary>Pack one sorted block in the existing zlib format.</summary>
    public static byte[] EncodeDeletedNames(IReadOnlyList<string> keys)
    {
        if (keys.Count is <= 0 or > DeletedBlockRows) throw new InvalidDataException();
        using var output = new MemoryStream();
        using (var compressed = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new BinaryWriter(compressed, HistoryUtf8))
        {
            byte[]? previous = null;
            foreach (var key in keys)
            {
                if (HistoryUtf8.GetByteCount(key) is <= 0 or > MaximumDeletedKeyBytes) throw new InvalidDataException();
                var bytes = HistoryUtf8.GetBytes(key);
                ValidateDeletedKey(bytes, previous);
                writer.Write7BitEncodedInt(bytes.Length);
                writer.Write(bytes);
                previous = bytes;
            }
        }
        return output.ToArray();
    }

    // Yield one bounded key at a time, never allocate a corrupt declared length or a decoded block.
    /// <summary>Stream validated keys and check the block trailer on exhaustion.</summary>
    public static IEnumerable<string> DecodeDeletedNames(Stream input, int count, string lastKey, string? after)
    {
        if (count is <= 0 or > DeletedBlockRows) throw new InvalidDataException();
        // ZLibStream accepts a missing trailer; verify its Adler-32 explicitly before publishing any restored set.
        // RFC 1950: CMF + FLG, then a 32-bit Adler trailer.
        if (input.Length < 2 + sizeof(uint)) throw new InvalidDataException();
        var start = input.Position;
        input.Seek(-sizeof(uint), SeekOrigin.End);
        var trailer = new byte[sizeof(uint)];
        try { input.ReadExactly(trailer); }
        catch (EndOfStreamException error) { throw new InvalidDataException(null, error); }
        input.Position = start;
        var expectedChecksum = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(trailer);
        uint checksum = 1;
        using var compressed = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(compressed, HistoryUtf8, leaveOpen: true);
        byte[]? previous = after is null ? null : HistoryUtf8.GetBytes(after);
        string? last = null;
        for (var index = 0; index < count; index++)
        {
            int length;
            try { length = reader.Read7BitEncodedInt(); }
            catch (Exception error) when (error is EndOfStreamException or FormatException)
            { throw new InvalidDataException(null, error); }
            if (length is <= 0 or > MaximumDeletedKeyBytes) throw new InvalidDataException();
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new InvalidDataException();
            ValidateDeletedKey(bytes, previous);
            checksum = DeletedChecksum(checksum, bytes);
            try { last = HistoryUtf8.GetString(bytes); }
            catch (DecoderFallbackException error) { throw new InvalidDataException(null, error); }
            previous = bytes;
            yield return last;
        }
        if (last != lastKey || compressed.ReadByte() != -1 || checksum != expectedChecksum) throw new InvalidDataException();
    }

    private static uint DeletedChecksum(uint checksum, byte[] bytes)
    {
        // RFC 1950 Adler-32: two 16-bit sums modulo the largest prime below 2^16.
        const uint adlerModulus = 65521;
        const int halfBits = sizeof(ushort) * 8;
        const int lengthBits = 7; // BinaryWriter.Write7BitEncodedInt format.
        const int more = 1 << lengthBits;
        var a = checksum & ushort.MaxValue;
        var b = checksum >> halfBits;
        // The persisted length prefix is the canonical BinaryWriter seven-bit encoding.
        var remaining = bytes.Length;
        do
        {
            var value = (byte)(remaining & (more - 1));
            remaining >>= lengthBits;
            Add(remaining == 0 ? value : (byte)(value | more));
        } while (remaining != 0);
        foreach (var value in bytes) Add(value);
        return (b << halfBits) | a;

        void Add(byte value)
        {
            a = (a + value) % adlerModulus;
            b = (b + a) % adlerModulus;
        }
    }

    private static void ValidateDeletedKey(byte[] key, byte[]? previous)
    {
        if (key.Length is <= 0 or > MaximumDeletedKeyBytes ||
            previous is not null && previous.AsSpan().SequenceCompareTo(key) >= 0)
            throw new InvalidDataException();
    }
}
