using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark;

internal sealed record SyncDiscoveryRoot(string Path, uint VolumeSerial, ulong FileId);
internal sealed record SyncDiscoveryItem(string RelativePath, ulong FileId, ulong ParentId, long Usn,
    FileAttributes Attributes, uint LinkCount, long Size, DateTime ModifiedUtc);
internal sealed record SyncDiscoveryView(SyncDiscoveryRoot Root, IReadOnlyDictionary<string, SyncDiscoveryItem> Items);
internal sealed record SyncDiscoveryProgress(uint Stage, ulong Sequence, ulong RecordsRead)
{
    internal string Description => Stage switch
    {
        1 => "Reading Drive Information", 2 => "Reading Folder Index",
        3 => "Selecting Folder Pair Metadata", 4 => "Reading File Index",
        5 => "Validating Folder Index", 6 => "Checking Index Continuity",
        7 => "Checking Selected File Metadata", _ => throw new InvalidDataException("Unknown Discovery stage.")
    };
}
internal sealed record SyncDiscoveryResult(IReadOnlyList<SyncDiscoveryView> Roots,
    NtfsUsnCheckpoint Before, NtfsUsnCheckpoint After)
{
    // ENUM_USN_DATA does not enumerate every hard-link name. Only the ordinary
    // scanner/journal-bound observation pipeline can establish deletion authority.
    internal bool IsCompletePathInventory => false;
}

internal static class SyncDiscoveryProtocol
{
    private static readonly byte[] Magic = "SSDISC01"u8.ToArray();
    private static readonly UnicodeEncoding Unicode = new(false, false, true);
    internal const int MaximumRoots = 128, MaximumRecords = 750_000, MaximumBytes = 256 * 1024 * 1024;
    internal const int MaximumPathUnits = 32760;
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan TaskDuration = TimeSpan.FromMinutes(31);

    internal static byte[] NewNonce() => RandomNumberGenerator.GetBytes(32);

    internal static byte[] Request(IReadOnlyList<SyncDiscoveryRoot> roots, ReadOnlySpan<byte> nonce)
    {
        ValidateRoots(roots, nonce.Length);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Unicode, true);
        writer.Write(Magic); writer.Write(1u); writer.Write(nonce); writer.Write((uint)roots.Count); writer.Write(roots[0].VolumeSerial);
        foreach (var root in roots)
        {
            var bytes = Unicode.GetBytes(root.Path);
            writer.Write(root.FileId); writer.Write((uint)(bytes.Length / 2)); writer.Write(bytes);
        }
        return output.ToArray();
    }

    internal static async Task<SyncDiscoveryResult> ReadAsync(Stream input, IReadOnlyList<SyncDiscoveryRoot> roots,
        ReadOnlyMemory<byte> nonce, CancellationToken cancellationToken, bool acknowledgePipe = false,
        Action<SyncDiscoveryProgress>? onProgress = null, Action? onActivity = null,
        Func<SyncDiscoveryProgress, Task>? onProgressAsync = null)
    {
        ValidateRoots(roots, nonce.Length);
        var header = new byte[52];
        await input.ReadExactlyAsync(header, cancellationToken);
        if (!header.AsSpan(0, 8).SequenceEqual(Magic) || U32(header, 8) != 1 ||
            !CryptographicOperations.FixedTimeEquals(header.AsSpan(12, 32), nonce.Span) ||
            U32(header, 44) != roots.Count || U32(header, 48) != roots[0].VolumeSerial)
            throw new InvalidDataException("Discovery returned a different request or volume identity.");
        onActivity?.Invoke();
        var items = roots.Select(_ => new Dictionary<string, SyncDiscoveryItem>(StringComparer.OrdinalIgnoreCase)).ToArray();
        var fileIds = roots.Select(_ => new HashSet<ulong>()).ToArray();
        var lengthBytes = new byte[4];
        var records = 0; long bytesRead = header.Length, estimatedMemory = 0;
        SyncDiscoveryProgress? previousProgress = null; var progressFrames = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await input.ReadExactlyAsync(lengthBytes, cancellationToken);
            var length = U32(lengthBytes, 0);
            if (length < 8 || length > 60 + MaximumPathUnits * 2 || (bytesRead += 4 + length) > MaximumBytes)
                throw new InvalidDataException("Discovery exceeded its bounded result size.");
            var frame = new byte[length]; await input.ReadExactlyAsync(frame, cancellationToken);
            var kind = U32(frame, 0);
            if (kind == 4)
            {
                if (length != 24 || ++progressFrames > 4096)
                    throw new InvalidDataException("Discovery returned invalid or excessive progress.");
                var progress = new SyncDiscoveryProgress(U32(frame, 4), U64(frame, 8), U64(frame, 16));
                if (progress.Stage is < 1 or > 7 || progress.Sequence == 0 ||
                    (records != 0 && progress.Stage != 7) ||
                    (previousProgress is not null && (progress.Stage < previousProgress.Stage ||
                        progress.Sequence <= previousProgress.Sequence || progress.RecordsRead < previousProgress.RecordsRead)))
                    throw new InvalidDataException("Discovery progress did not advance consistently.");
                previousProgress = progress;
                onActivity?.Invoke(); onProgress?.Invoke(progress);
                if (onProgressAsync is not null) await onProgressAsync(progress);
                continue; // Progress is not a result, an item or deletion authority.
            }
            if (kind == 3 && length == 8)
                throw new IOException("Discovery could not complete the read-only inventory (Windows " + U32(frame, 4) + ").");
            if (kind == 2)
            {
                if (length != 72 || U32(frame, 4) != records)
                    throw new InvalidDataException("Discovery did not finish the complete framed response.");
                var before = Checkpoint(frame, 8); var after = Checkpoint(frame, 40);
                if (before.JournalId == 0 || after.FirstUsn > before.NextUsn || after.RequiresFullReconciliation(before.Cursor))
                    throw new IOException("The change journal reset during Discovery; use ordinary scanning.");
                foreach (var (root, index) in roots.Select((root, index) => (root, index)))
                    foreach (var item in items[index].Values)
                    {
                        var slash = item.RelativePath.LastIndexOf('/');
                        var parentId = root.FileId;
                        if (slash >= 0)
                        {
                            if (!items[index].TryGetValue(item.RelativePath[..slash], out var directory) ||
                                (directory.Attributes & FileAttributes.Directory) == 0)
                                throw new InvalidDataException("Discovery omitted a parent directory.");
                            parentId = directory.FileId;
                        }
                        if (item.ParentId != parentId)
                            throw new InvalidDataException("Discovery returned inconsistent directory ancestry.");
                    }
                var extra = new byte[1];
                if (acknowledgePipe)
                {
                    // The worker waits for this acknowledgement before closing its
                    // endpoint. No result is published until the subsequent EOF check.
                    await input.WriteAsync(new byte[] { 0xa5 }, cancellationToken);
                    await input.FlushAsync(cancellationToken);
                }
                if (await input.ReadAsync(extra, cancellationToken) != 0)
                    throw new InvalidDataException("Discovery sent data after its completed response.");
                return new(roots.Select((root, index) => new SyncDiscoveryView(root, items[index])).ToArray(), before, after);
            }
            if (kind != 1 || length < 62 || ++records > MaximumRecords)
                throw new InvalidDataException("Discovery returned an unsupported or oversized record stream.");
            var rootIndex = U32(frame, 4); var id = U64(frame, 8); var parent = U64(frame, 16); var usn = I64(frame, 24);
            var attributes = (FileAttributes)U32(frame, 32); var links = U32(frame, 36);
            var size = I64(frame, 40); var modified = I64(frame, 48); var units = U32(frame, 56);
            if (rootIndex >= roots.Count || id == 0 || id == roots[(int)rootIndex].FileId || parent == 0 || usn < 0 ||
                links == 0 || size < 0 || units == 0 || units > MaximumPathUnits || length != 60 + units * 2 ||
                PilotPath.IsUnsafeAttributes(attributes))
                throw new InvalidDataException("Discovery returned unsafe item metadata.");
            string path;
            DateTime time;
            try { path = Unicode.GetString(frame, 60, checked((int)units * 2)); time = DateTime.FromFileTimeUtc(modified); }
            catch (Exception error) when (error is DecoderFallbackException or ArgumentOutOfRangeException)
            { throw new InvalidDataException("Discovery returned an invalid filename or time.", error); }
            ValidateRelativePath(path);
            var canonical = path.Replace('\\', '/');
            if (!fileIds[rootIndex].Add(id) || !items[rootIndex].TryAdd(canonical,
                    new(canonical, id, parent, usn, attributes, links, size, time)))
                throw new InvalidDataException("Discovery returned an ambiguous path or file reference.");
            estimatedMemory += 512L + path.Length * 4L;
            if (estimatedMemory > MaximumBytes)
                throw new IOException("The Discovery metadata cache is full; use ordinary scanning.");
            onActivity?.Invoke();
        }
    }

    private static NtfsUsnCheckpoint Checkpoint(byte[] frame, int offset)
    {
        var result = new NtfsUsnCheckpoint(U64(frame, offset), I64(frame, offset + 8), I64(frame, offset + 16), I64(frame, offset + 24));
        if (result.FirstUsn < 0 || result.NextUsn < result.FirstUsn || result.LowestValidUsn < 0 || result.LowestValidUsn > result.NextUsn)
            throw new InvalidDataException("Discovery returned an invalid journal checkpoint.");
        return result;
    }

    private static void ValidateRoots(IReadOnlyList<SyncDiscoveryRoot> roots, int nonceLength)
    {
        if (roots.Count is < 1 or > MaximumRoots || nonceLength != 32 || roots.Any(root => root.FileId == 0 ||
                root.VolumeSerial != roots[0].VolumeSerial) || roots.Select(root => root.FileId).Distinct().Count() != roots.Count)
            throw new InvalidDataException("Discovery requires bounded, distinct roots on one volume.");
        foreach (var root in roots)
        {
            var path = root.Path;
            if (path.Length is < 3 or > MaximumPathUnits - 512 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
                throw new InvalidDataException("Discovery requires an ordinary absolute local root.");
            if (path.Length > 3) ValidateRelativePath(path[3..]);
        }
    }

    private static void ValidateRelativePath(string path)
    {
        var components = path.Split('\\');
        if (components.Length > 512 || path.Any(char.IsControl) || path.IndexOfAny(['/', ':', '"', '<', '>', '|', '*', '?']) >= 0 ||
            components.Any(part => part.Length is < 1 or > 255 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
            throw new InvalidDataException("Discovery returned a noncanonical relative path.");
    }

    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    private static ulong U64(byte[] data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
    private static long I64(byte[] data, int offset) => BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset));
}
