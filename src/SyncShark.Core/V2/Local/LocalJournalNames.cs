using System.ComponentModel;

namespace Massdex.SyncShark.V2;

/// <summary>Names redacted from non-admin USN records, resolved by streaming direct-parent metadata.
/// Retains only IDs in this bounded journal batch, never a directory or root inventory.</summary>
internal sealed class LocalJournalNames
{
    private readonly RootDef _root;
    private readonly IStateStore _known;
    private readonly HashSet<ulong> _wanted;
    private readonly Dictionary<string, Dictionary<ulong, string>> _parents = new(StringComparer.Ordinal);
    private readonly int _limit;
    private int _retained;

    public LocalJournalNames(RootDef root, IStateStore known, IReadOnlyList<NtfsUsnChange> changes, int limit)
    {
        _root = root;
        _known = known;
        _wanted = changes.Select(change => change.FileId).ToHashSet();
        _limit = limit;
    }

    internal static Action? LookupForTest { get; set; }
    public string? LastFailure { get; private set; }
    private IReadOnlyList<string>? Failed(string reason) { LastFailure = reason; return null; }
    public bool Ambiguous(ulong id, ItemState? known) => Ambiguous(id, known, false);

    public bool Ambiguous(ulong id, ItemState? known, bool requirePresent)
    {
        if (_known.HasMultipleNames(_root.RootId, SyncLocalEndpointStore.ObjectId(LocalItems.ParseObjectId(_root.RootObjectId).Volume, id)))
        {
            LastFailure = $"multiple stored hard-link names for object {id:x16}";
            return true;
        }
        return known is { Kind: ItemKind.File } && !SingleLink(known.Path, id, requirePresent);
    }

    private bool SingleLink(string relative, ulong id, bool requirePresent = false)
    {
        var path = Path.Combine(_root.RootPath, relative.Replace('/', Path.DirectorySeparatorChar));
        using var handle = OpenMetadata(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (!requirePresent && error is 2 or 3) return true; // disappearance is rechecked by the guarded refresh
            LastFailure = $"metadata open failed for {relative}: Windows error {error}";
            return false;
        }
        if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var info))
        {
            LastFailure = $"metadata identity read failed for {relative}: Windows error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";
            return false;
        }
        if (info.VolumeSerialNumber != LocalItems.ParseObjectId(_root.RootObjectId).Volume ||
            (((ulong)info.FileIndexHigh << 32) | info.FileIndexLow) != id)
        { LastFailure = $"metadata identity changed for {relative}"; return false; }
        if (info.NumberOfLinks != 1)
        { LastFailure = $"{relative} has {info.NumberOfLinks} live hard links"; return false; }
        return true;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenMetadata(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    // null is never absence evidence. Caller falls back on any inaccessible/replaced parent or ambiguous ID.
    public IReadOnlyList<string>? Find(NtfsUsnChange change, string parent, CancellationToken ct)
    {
        LookupForTest?.Invoke();
        if (!_parents.TryGetValue(parent, out var names))
        {
            if (_parents.Count >= _limit) return Failed($"parent lookup limit reached at {parent}");
            try
            {
                // GuardRefreshPaths holds every ancestor, including the requested parent, through enumeration.
                // The synthetic child is not opened; the outer inventory already holds the root identity lease.
                var child = parent.Length == 0 ? ".journal-name-probe" : parent + "/.journal-name-probe";
                using var guard = LocalItems.GuardRefreshPaths(_root.RootPath, _root.RootId, _known, [child]);
                if (guard.Incomplete.Count != 0 || guard.Safe.Count != 1) return Failed($"parent identity unavailable or changed at {parent}");
                names = new Dictionary<ulong, string>();
                var directory = parent.Length == 0 ? _root.RootPath :
                    Path.Combine(_root.RootPath, parent.Replace('/', Path.DirectorySeparatorChar));
                foreach (var entry in WindowsDirectoryListing.List(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    var id = unchecked((ulong)entry.FileId);
                    if (!_wanted.Contains(id)) continue;
                    var path = parent.Length == 0 ? entry.Name : parent + "/" + entry.Name;
                    if ((entry.Attributes & FileAttributes.Directory) == 0 && !SingleLink(path, id)) return null;
                    if (++_retained > _limit) return Failed($"retained-name limit reached at {parent}");
                    if (!names.TryAdd(id, path)) return Failed($"multiple names for object {id:x16} under {parent}");
                }
                _parents.Add(parent, names);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                Win32Exception or ArgumentException)
            {
                return Failed($"parent listing failed at {parent}: {error.Message}");
            }
        }
        return names.TryGetValue(change.FileId, out var foundPath) ? [foundPath] : [];
    }
}
