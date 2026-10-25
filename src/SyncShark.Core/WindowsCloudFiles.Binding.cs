namespace Massdex.SyncShark;

/// <summary>Immutable native/provider binding, independent of the v1 policy model.</summary>
public sealed record WindowsCloudFilesBinding(string PairId, string Provider, string AccountId,
    string LocalRoot, uint VolumeSerialNumber, ulong RootFileId, string RemoteRoot, string RemoteRootId)
{
    public void RequireSame(WindowsCloudFilesBinding other)
    {
        if (this != other) throw new InvalidOperationException("The Cloud Files pair, provider, account or root binding changed.");
    }

    internal SyncPair ToPair(SyncDirectoryStorage defaultStorage) => new(PairId, PairId, LocalRoot,
        VolumeSerialNumber, RootFileId, RemoteRoot, RemoteRootId, ConnectionId: AccountId,
        Provider: Provider, FilesOnDemandEnabled: true, DefaultStorage: defaultStorage);

    internal WindowsCloudFileIdentity RootIdentity() => WindowsCloudFileIdentity.Root(PairId, Provider) with
    { AccountId = AccountId, RemoteRootId = RemoteRootId, RemoteRoot = RemoteRoot };

    internal void RequireRoot(WindowsCloudPlaceholderInfo root)
    {
        if (root.Identity.ObjectKind != "root" || !root.Identity.IsBoundTo(PairId, Provider) ||
            root.Identity.AccountId != AccountId || root.Identity.RemoteRootId != RemoteRootId ||
            root.Identity.RemoteRoot != RemoteRoot || root.FileId != RootFileId)
            throw new IOException("The retained Cloud Files registration does not match the exact pair/account/root binding.");
    }
}

/// <summary>The provider needs only verified ranged reads, not an entire sync endpoint.</summary>
public interface IWindowsCloudFilesRangeSource : ISyncRemoteRangeStore
{
    Task VerifyRootIdentityAsync(CancellationToken cancellationToken = default);
}

/// <summary>A narrow lifetime seam for offline lifecycle checks.</summary>
internal interface ICloudFilesConnection : IDisposable
{
    void RegisterAndConnect();
    void RollBackNewRegistration();
}

public sealed partial class WindowsCloudFilesProvider
{
    private readonly WindowsCloudFilesBinding? _binding;

    public WindowsCloudFilesProvider(WindowsCloudFilesBinding binding, IWindowsCloudFilesRangeSource source,
        string temporaryRoot, SyncDirectoryStorage defaultStorage)
        : this(binding.ToPair(defaultStorage), source, source.VerifyRootIdentityAsync, temporaryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.PairId);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.RemoteRootId);
        _binding = binding;
    }

    // The root lease remains held by the caller. A retained registration is never refreshed or unregistered.
    private bool ReconnectBoundRoot(string root)
    {
        if (_binding is null) return false;
        var registration = ReadRootRegistration(root);
        if (registration is null)
        {
            if (ExpectedRegistrationId is not null)
                throw new IOException("The recorded native registration disappeared; it was not recreated.");
            return false;
        }
        if (ExpectedRegistrationId is null || registration.OwnershipToken != ExpectedRegistrationId ||
            registration.FileId != _binding.RootFileId || registration.Identity is not { } identity ||
            identity.AccountId != _binding.AccountId || identity.RemoteRootId != _binding.RemoteRootId ||
            identity.RemoteRoot != _binding.RemoteRoot || !identity.IsBoundTo(_binding.PairId, _binding.Provider) ||
            !TryReadOwnedPlaceholder(root, out var info) || info is null)
            throw new IOException("Another provider or root owns the Cloud Files registration.");
        _binding.RequireRoot(info);
        Connect(root);
        return true;
    }

    private void RequireNoDependentPointers()
    {
        // Conservative rollback: any reparse point or unreadable subtree prevents removal. Never hydrate,
        // follow a junction, or infer that OFFLINE means there are no bytes or dependent placeholders.
        var pending = new Stack<string>();
        pending.Push(_pair.LocalRoot);
        while (pending.TryPop(out var directory))
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline |
                                   (FileAttributes)0x00400000 | (FileAttributes)0x00040000)) != 0)
                    throw new IOException("The new registration has dependent or unverified pointers; it was retained.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
            }
    }
}
