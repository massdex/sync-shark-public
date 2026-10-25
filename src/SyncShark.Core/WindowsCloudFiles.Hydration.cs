namespace Massdex.SyncShark;

public sealed partial class WindowsCloudFilesProvider
{
    // Only explicit offline requests allocate entries (four per worker), never ordinary Explorer reads.
    // Capture the original source error before completing Windows I/O, whose IOException loses that type.
    internal sealed class OfflineHydrationAttempt : IDisposable
    {
        private readonly Action _release;
        private Exception? _failure;
        internal OfflineHydrationAttempt(Action release) => _release = release;
        internal Exception? Failure => Volatile.Read(ref _failure);
        internal void Failed(Exception error) => Interlocked.CompareExchange(ref _failure, error, null);
        internal IOException Correlate(IOException error) => IsGenericReadFailure(error) && Failure is { } source
            ? new OfflineHydrationSourceException(source) : error;
        internal static bool IsGenericReadFailure(Exception error) => error.GetType() == typeof(IOException) &&
            error.InnerException is null && (error.HResult is unchecked((int)0x80131620) or
                unchecked((int)0x8007001f) or unchecked((int)0x80070185));
        public void Dispose() => _release();
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, OfflineHydrationAttempt> _offlineAttempts = new();
    private OfflineHydrationAttempt BeginOfflineAttempt(string objectId)
    {
        var attempt = new OfflineHydrationAttempt(() => _offlineAttempts.TryRemove(objectId, out _));
        if (!_offlineAttempts.TryAdd(objectId, attempt))
            throw new InvalidDataException("Offline hydration for this object is already running. Retry when it completes.");
        return attempt;
    }

    internal sealed class OfflineHydrationSourceException(Exception source)
        : IOException("The offline read failed at its provider source.", source);

    /// <summary>One entity, bounded streaming hydration. No subtree enumeration and no dehydration.</summary>
    internal async Task<WindowsCloudPlaceholderInfo> HydrateOwnedPlaceholderAsync(string relativePath,
        PilotDirectoryIdentity expectedLocalIdentity, Action revalidate, CancellationToken ct, bool verifyLocalChanges = false)
    {
        ThrowIfDisposed();
        var relative = PilotPath.NormalizeRelative(relativePath);
        using var attempt = BeginOfflineAttempt(SyncLocalEndpointStore.ObjectId(
            expectedLocalIdentity.VolumeSerialNumber, expectedLocalIdentity.FileId));
        using var root = PilotDirectoryLease.Open(_pair.LocalRoot,
            new(_pair.LocalVolumeSerialNumber, _pair.LocalRootFileId));
        var parents = new List<PilotDirectoryLease>();
        try
        {
            var parent = root;
            var segments = relative.Split('/');
            foreach (var segment in segments.Take(segments.Length - 1))
            {
                parent = PilotDirectoryLease.Open(Path.Combine(parent.Path, segment));
                parents.Add(parent);
            }
            var path = Path.Combine(parent.Path, segments[^1]);
            // Inspect via a no-recall attribute handle before opening any data handle.
            using (var inspected = OpenAttributes(path, writeDac: false, exclusive: false))
            {
                VerifySettlementIdentity(inspected.Handle, expectedLocalIdentity);
                if (!TryReadOwnedPlaceholder(inspected.Handle, false, out var owned) || owned is null ||
                    !owned.Identity.IsBoundTo(_pair.Id, _pair.Provider) || owned.IsDirectory ||
                    owned.SyncRootFileId != _pair.LocalRootFileId)
                    throw new InvalidDataException("The offline item is not owned by this root. Rescan before Retry.");
                RequireCurrentNativeBinding();
                ct.ThrowIfCancellationRequested();
                revalidate();
            }
            // Read sharing retains this exact object and excludes concurrent writes/replacements.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            VerifySettlementIdentity(stream.SafeFileHandle, expectedLocalIdentity);
            if (!TryReadOwnedPlaceholder(stream.SafeFileHandle, false, out var current) || current is null ||
                !current.Identity.IsBoundTo(_pair.Id, _pair.Provider) || current.IsDirectory ||
                current.SyncRootFileId != _pair.LocalRootFileId)
                throw new InvalidDataException("The offline item is not a placeholder owned by this root. Rescan before Retry.");
            RequireCurrentNativeBinding();
            root.VerifyBinding();
            parent.VerifyBinding();
            ct.ThrowIfCancellationRequested();
            revalidate();
            if (!HasSufficientDiskSpace(path, Math.Max(0, current.State.LogicalSize - current.State.OnDiskBytes)))
                throw new InvalidDataException("There is not enough free space to keep this item offline. Free space and Retry.");
            // Normal async reads request all missing ranges through the connected provider, with cancellation.
            // Hashing is streaming (constant memory) and verifies the exact provider revision before success.
            if (stream.Length != current.Identity.LogicalSize && (!verifyLocalChanges || current.State.IsInSync))
                throw new InvalidDataException("The offline item changed size. Resync before Retry.");
            var hash = await SyncContentIdentity.ComputeAsync(_pair.Provider, stream, ct).ConfigureAwait(false);
            if (current.State.IsInSync && !string.Equals(hash, current.Identity.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The hydrated bytes do not match the exact provider revision. Resync before Retry.");
            // A hydrated, locally edited file is preserved and pinned, not restored to the remote content.
            ct.ThrowIfCancellationRequested();
            revalidate();
            using var attributes = OpenAttributes(path, writeDac: true, exclusive: false);
            VerifySettlementIdentity(attributes.Handle, expectedLocalIdentity);
            Check(Native.CfSetPinState(attributes.Handle.DangerousGetHandle(), 1, 0, IntPtr.Zero),
                "Pinning the verified offline item");
            if (!TryReadOwnedPlaceholder(stream.SafeFileHandle, false, out var result) || result is null ||
                !SameIdentity(result.Identity, current.Identity) ||
                result.State.Availability != SyncCloudFileAvailability.AvailableOffline ||
                result.State.OnDiskBytes != result.State.LogicalSize)
                throw new InvalidDataException("Windows has not confirmed all offline bytes. Retry hydration.");
            return result;
        }
        catch (IOException error) when (error.GetType() == typeof(IOException) && attempt.Failure is not null)
        {
            throw attempt.Correlate(error);
        }
        finally
        {
            for (var i = parents.Count - 1; i >= 0; i--) parents[i].Dispose();
        }
    }

    private void RequireCurrentNativeBinding()
    {
        if (_binding is null) return;
        var registration = ReadRootRegistration(_pair.LocalRoot);
        var expected = ExpectedRegistrationId ?? _createdRegistrationId;
        if (expected is null || registration is null || registration.OwnershipToken != expected ||
            registration.FileId != _binding.RootFileId || registration.Identity is not { } identity ||
            identity.AccountId != _binding.AccountId || identity.RemoteRootId != _binding.RemoteRootId ||
            identity.RemoteRoot != _binding.RemoteRoot || !identity.IsBoundTo(_binding.PairId, _binding.Provider))
            throw new InvalidDataException("The native registration no longer matches this pair. Reconnect before Retry.");
    }
}
