using System.Buffers.Binary;
using System.Text;

namespace Massdex.SyncShark;

// A metadata acceleration hint, never a deletion/absence proof. File references include the
// NTFS sequence bits. Contents, ACL visibility, hard-link names and concurrent changes still
// require the ordinary user's observation/effect guards. This class performs no native access.
internal sealed record NtfsMftEntry(ulong FileId, ulong ParentFileId, long Usn, FileAttributes Attributes, string Name);
internal sealed record NtfsMftRootView(ulong RootFileId, IReadOnlyDictionary<string, NtfsMftEntry> Entries,
    IReadOnlyList<string> RequiresOrdinaryInspection);

internal sealed class NtfsMftCatalog
{
    private const long MaximumEstimatedBytes = 256L * 1024 * 1024;
    private static readonly UnicodeEncoding StrictUnicode = new(false, false, true);
    private readonly Dictionary<ulong, NtfsMftEntry> _entries = [];
    private readonly ulong[] _rootIds;
    private long _estimatedBytes;
    private ulong _cursor;
    private bool _invalid, _sealed;

    internal NtfsMftCatalog(IEnumerable<ulong> admittedRootIds)
    {
        _rootIds = admittedRootIds.ToArray();
        if (_rootIds.Length is < 1 or > 128 || _rootIds.Any(id => id == 0) || _rootIds.Distinct().Count() != _rootIds.Length)
            throw new InvalidDataException("A bounded, distinct set of admitted root identities is required.");
    }

    internal void AddEnumerationPage(ReadOnlySpan<byte> page)
    {
        if (_invalid || _sealed) throw new InvalidOperationException("This MFT catalogue is no longer writable.");
        try
        {
            var entries = ParseEnumerationPage(page, _cursor, out var next);
            foreach (var entry in entries)
            {
                _estimatedBytes = checked(_estimatedBytes + 256 + entry.Name.Length * 2L);
                if (_entries.Count >= 750_000 || _estimatedBytes > MaximumEstimatedBytes)
                    throw new IOException("The bounded MFT catalogue is full; use the ordinary scanner.");
                if (!_entries.TryAdd(entry.FileId, entry))
                    throw new IOException("An MFT identity appeared more than once; a concurrent change or alias needs ordinary inspection.");
            }
            _cursor = next;
        }
        catch { _invalid = true; _entries.Clear(); throw; }
    }

    // Called only after the native producer confirms enumeration EOF and journal continuity.
    // It does NOT promote enumeration to a complete pathname inventory: MFT records do not
    // establish all hard-link aliases. No caller may infer a deletion from an absent entry.
    internal IReadOnlyList<NtfsMftRootView> Complete(NtfsUsnCheckpoint before, NtfsUsnCheckpoint after,
        CancellationToken cancellationToken = default)
    {
        if (_invalid || _sealed) throw new InvalidOperationException("An incomplete or already consumed catalogue cannot be published.");
        _sealed = true;
        if (before.JournalId == 0 || after.RequiresFullReconciliation(before.Cursor))
            throw new IOException("The journal changed during MFT enumeration; discard the catalogue.");
        var children = _entries.Values.Where(entry => entry.FileId != entry.ParentFileId)
            .GroupBy(entry => entry.ParentFileId).ToDictionary(group => group.Key, group => group.ToArray());
        var result = new List<NtfsMftRootView>();
        foreach (var root in _rootIds)
        {
            if (!_entries.TryGetValue(root, out var anchor) || (anchor.Attributes & FileAttributes.Directory) == 0 ||
                PilotPath.IsUnsafeAttributes(anchor.Attributes))
                throw new IOException("An admitted MFT root is absent or no longer a regular directory.");
            var paths = new Dictionary<string, NtfsMftEntry>(StringComparer.OrdinalIgnoreCase);
            var uncertain = new List<string>(); var visited = new HashSet<ulong> { root };
            var pending = new Stack<(ulong Id, string Path, int Depth)>(); pending.Push((root, "", 0));
            while (pending.TryPop(out var parent))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var entry in children.GetValueOrDefault(parent.Id) ?? [])
                {
                    var path = parent.Path.Length == 0 ? entry.Name : parent.Path + "/" + entry.Name;
                    if (path.Length > 32760 || parent.Depth >= 512 || !visited.Add(entry.FileId) || !paths.TryAdd(path, entry))
                        throw new IOException("The MFT tree is ambiguous or exceeds the path/depth limit.");
                    if (PilotPath.IsUnsafeAttributes(entry.Attributes)) { uncertain.Add(path); continue; }
                    if ((entry.Attributes & FileAttributes.Directory) != 0) pending.Push((entry.FileId, path, parent.Depth + 1));
                }
            }
            result.Add(new(root, paths, uncertain));
        }
        return result;
    }

    internal static IReadOnlyList<NtfsMftEntry> ParseEnumerationPage(ReadOnlySpan<byte> page, ulong previousCursor, out ulong nextCursor)
    {
        if (page.Length is < 8 or > 1024 * 1024) throw new IOException("The MFT page length is invalid.");
        nextCursor = BinaryPrimitives.ReadUInt64LittleEndian(page);
        if (nextCursor <= previousCursor) throw new IOException("The MFT enumeration cursor did not advance.");
        var result = new List<NtfsMftEntry>(); var data = page[8..];
        while (!data.IsEmpty)
        {
            if (data.Length < 60) throw new IOException("An MFT record is truncated.");
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data);
            if (length is < 64 or > 4096 || length > data.Length || (length & 7) != 0 ||
                BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) != 2 || BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) != 0)
                throw new IOException("Unsupported or malformed MFT record; use the ordinary scanner.");
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data[56..]);
            var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(data[58..]);
            if (nameLength is < 2 or > 510 || (nameLength & 1) != 0 || nameOffset < 60 || (nameOffset & 1) != 0 || nameOffset + nameLength > length)
                throw new IOException("An MFT filename is malformed.");
            string name;
            try { name = StrictUnicode.GetString(data.Slice(nameOffset, nameLength)); }
            catch (DecoderFallbackException error) { throw new IOException("An MFT filename is not valid UTF-16.", error); }
            var id = BinaryPrimitives.ReadUInt64LittleEndian(data[8..]);
            var parent = BinaryPrimitives.ReadUInt64LittleEndian(data[16..]);
            var usn = BinaryPrimitives.ReadInt64LittleEndian(data[24..]);
            var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(data[52..]);
            if (id == 0 || parent == 0 || usn < 0 || name.Any(char.IsControl) || name.IndexOfAny(['/', '\\', ':']) >= 0 ||
                name == ".." || name == "." && (id != parent || (attributes & FileAttributes.Directory) == 0))
                throw new IOException("An MFT record has an unsafe identity or name.");
            result.Add(new(id, parent, usn, attributes, name)); data = data[(int)length..];
        }
        return result;
    }
}
