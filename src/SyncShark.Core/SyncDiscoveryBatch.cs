using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark;

// One disposable, physical-volume request per currently authorised sync batch.
// This does not retain roots from other accounts, prior runs or other host instances.
internal sealed class SyncDiscoveryBatch : IDisposable
{
    private static readonly AsyncLocal<SyncDiscoveryBatch?> Current = new();
    // Automatic enrichment is only an optimization. A cold volume index must not
    // hold a selected-folder scan for minutes; explicit metadata checks keep their
    // existing diagnostic budget. Cancellation closes the owned pipe, never kills a process.
    internal static readonly TimeSpan AutomaticBudget = TimeSpan.FromSeconds(2);
    private readonly SyncDiscoveryBatch? _previous;
    private readonly SyncPair[] _pairs;
    internal delegate Task<SyncDiscoveryResult?> MetadataReader(IReadOnlyList<SyncDiscoveryRoot> roots, CancellationToken token);
    private readonly MetadataReader? _reader;
    private readonly ConcurrentDictionary<string, Lazy<Task<SyncDiscoveryResult?>>> _volumes = new(StringComparer.OrdinalIgnoreCase);
    private SyncDiscoveryBatch(IEnumerable<SyncPair> pairs, MetadataReader? reader = null)
    { _previous = Current.Value; _pairs = pairs.Where(pair => !pair.FilesOnDemandEnabled).ToArray(); _reader = reader; Current.Value = this; }
    internal static IDisposable Begin(IEnumerable<SyncPair> pairs) => new SyncDiscoveryBatch(pairs);
    // Scoped lower-level input for bounded consumer checks. It never replaces
    // root leases, physical-volume selection, caching or journal admission.
    internal static IDisposable Begin(IEnumerable<SyncPair> pairs, MetadataReader reader) => new SyncDiscoveryBatch(pairs, reader);
    internal static bool IsAvailable => Current.Value?._reader is not null || SyncDiscoveryClient.IsInstalled;
    // A related pair commits bytes another pair shares. The volume read cached for THIS batch was taken
    // before that commit and must not be reused as a fresh view, so the cached request is dropped and the
    // next ordinary read re-reads the volume. Unrelated pairs never call this.
    internal static void InvalidateCurrentAfterRelatedCommit() => Current.Value?._volumes.Clear();
    public void Dispose() => Current.Value = _previous;

    internal static async Task<SyncDiscoveryView?> ReadViewAsync(SyncPair pair, PilotDirectoryLease root,
        NtfsUsnCheckpoint checkpoint, CancellationToken cancellationToken,
        Func<SyncDiscoveryProgress, Task>? onProgress = null)
    {
        if (!IsAvailable || pair.FilesOnDemandEnabled) return null;
        var batch = Current.Value;
        var physical = Volume(root.Handle); if (physical is null) return null;
        var read = batch is null
            ? ReadVolumeAsync([pair], pair, physical, cancellationToken, null, onProgress)
            : batch._volumes.GetOrAdd(physical, _ => new Lazy<Task<SyncDiscoveryResult?>>(
                () => ReadVolumeAsync(batch._pairs, pair, physical, cancellationToken, batch._reader, onProgress))).Value;
        SyncDiscoveryResult? result;
        try { result = await read.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The shared optional metadata request may have belonged to another
            // pair whose provider observation cancelled its local sibling. That
            // cancellation is not this caller's stop request. Keep the cached
            // failure (no repeated volume scan), but use ordinary root-scoped
            // inventory instead of cancelling an independent healthy pair.
            return null;
        }
        if (result is null || result.Before.JournalId != checkpoint.JournalId ||
            checkpoint.LowestValidUsn > result.Before.NextUsn || result.After.FirstUsn > checkpoint.NextUsn) return null;
        return result.Roots.FirstOrDefault(view => view.Root.FileId == pair.LocalRootFileId &&
            view.Root.VolumeSerial == pair.LocalVolumeSerialNumber &&
            string.Equals(view.Root.Path, root.Path, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<SyncDiscoveryResult?> ReadVolumeAsync(IEnumerable<SyncPair> pairs, SyncPair requested,
        string physical, CancellationToken token, MetadataReader? reader,
        Func<SyncDiscoveryProgress, Task>? onProgress)
    {
        var held = new List<PilotDirectoryLease>(); var roots = new List<SyncDiscoveryRoot>(); var ids = new HashSet<ulong>();
        using var budget = new CancellationTokenSource(AutomaticBudget);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token, budget.Token);
        try
        {
            foreach (var pair in new[] { requested }.Concat(pairs).DistinctBy(pair => pair.Id))
            {
                token.ThrowIfCancellationRequested(); if (roots.Count == SyncDiscoveryProtocol.MaximumRoots) break;
                if (pair.FilesOnDemandEnabled || pair.LocalVolumeSerialNumber != requested.LocalVolumeSerialNumber) continue;
                try
                {
                    var lease = PilotDirectoryLease.Open(pair.LocalRoot, new(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
                    if (!string.Equals(Volume(lease.Handle), physical, StringComparison.OrdinalIgnoreCase) || !ids.Add(pair.LocalRootFileId))
                    { lease.Dispose(); continue; }
                    held.Add(lease); roots.Add(new(lease.Path, pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                { if (pair.Id == requested.Id) return null; }
            }
            if (roots.Count == 0) return null;
            var result = reader is null
                ? await SyncDiscoveryClient.TryReadAsync(roots, bounded.Token, onProgress)
                : await reader(roots, bounded.Token);
            foreach (var lease in held) lease.VerifyBinding();
            return result;
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !token.IsCancellationRequested)
        { return null; } // No partial metadata becomes an inventory or deletion authority.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        finally { foreach (var lease in held) lease.Dispose(); }
    }

    private static string? Volume(SafeFileHandle handle)
    {
        var value = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, value, (uint)value.Capacity, 1);
        if (length is < 49 or >= 32768) return null;
        var path = value.ToString();
        return path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) && path[47] == '}' && path[48] == '\\'
            ? path[..48] : null;
    }
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint count, uint flags);
}
