using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark;

public sealed record WindowsCloudFileIdentity(
    string Marker,
    int SchemaVersion,
    string PairId,
    string RelativePath,
    string ObjectKind,
    string? DropboxId,
    string? DropboxRevision,
    long LogicalSize,
    string? ContentHash,
    DateTimeOffset? ModifiedUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Provider = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentAlgorithm = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AccountId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RemoteRootId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RemoteRoot = null)
{
    public const string CurrentMarker = "massdex.sync-shark.cloud-files.v1";

    [JsonIgnore] public string EffectiveProvider => Provider ?? SyncProviders.Dropbox;
    [JsonIgnore] public string EffectiveContentAlgorithm => ContentAlgorithm ?? SyncContentIdentity.DropboxBlockSha256;

    public bool IsBoundTo(string pairId, string? provider) => PairId == pairId &&
        EffectiveProvider == SyncProviders.Normalize(provider) &&
        EffectiveContentAlgorithm == SyncContentIdentity.AlgorithmFor(provider);

    public static WindowsCloudFileIdentity Root(string pairId, string? provider = null) =>
        new(CurrentMarker, 1, pairId, ".", "root", null, null, 0, null, null,
            ExplicitProviderFor(provider),
            SyncContentIdentity.IdentityStampFor(provider));

    public static WindowsCloudFileIdentity FromRemote(string pairId, PilotRemoteEntry entry, string? provider = null)
    {
        if (entry.TransferBlockedReason is not null)
            throw new InvalidDataException(entry.TransferBlockedReason);
        return new(CurrentMarker, 1, pairId, PilotPath.NormalizeRelative(entry.RelativePath),
            entry.Kind == PilotEntryKind.Directory ? "directory" : "file", entry.Id, entry.Revision,
            entry.Size, entry.ContentHash, entry.ModifiedUtc,
            ExplicitProviderFor(provider),
            SyncContentIdentity.IdentityStampFor(provider));
    }

    private static string? ExplicitProviderFor(string? provider) => SyncProviders.Normalize(provider) switch
    {
        SyncProviders.Dropbox => null,
        SyncProviders.GoogleDrive => SyncProviders.GoogleDrive,
        _ => throw new InvalidDataException("This provider has no verified Cloud Files identity format.")
    };

    // Earlier Google releases registered status-only roots using the historical unstamped root.
    // Refresh only that empty root marker for the same retained pair; never reinterpret a file hash.
    internal bool CanRefreshRootFor(string pairId, string? provider) => ObjectKind == "root" &&
        RelativePath == "." && PairId == pairId && DropboxId is null && DropboxRevision is null &&
        LogicalSize == 0 && ContentHash is null && ModifiedUtc is null &&
        (IsBoundTo(pairId, provider) || (SyncProviders.Normalize(provider) == SyncProviders.GoogleDrive &&
            Provider is null && ContentAlgorithm is null));

    public byte[] Serialize()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this);
        if (bytes.Length is 0 or > 4096)
            throw new InvalidDataException("The Windows Cloud Files identity is outside the platform limit.");
        return bytes;
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> bytes, out WindowsCloudFileIdentity? identity)
    {
        identity = null;
        try
        {
            var parsed = JsonSerializer.Deserialize<WindowsCloudFileIdentity>(bytes);
            if (parsed is null || parsed.Marker != CurrentMarker || parsed.SchemaVersion != 1 ||
                string.IsNullOrWhiteSpace(parsed.PairId) ||
                parsed.ObjectKind is not ("root" or "directory" or "file") ||
                parsed.LogicalSize < 0 ||
                parsed.EffectiveProvider is not (SyncProviders.Dropbox or SyncProviders.GoogleDrive) ||
                parsed.EffectiveContentAlgorithm != SyncContentIdentity.AlgorithmFor(parsed.EffectiveProvider))
                return false;
            if (parsed.ObjectKind == "root")
            {
                if (parsed.RelativePath != "." || parsed.DropboxId is not null || parsed.DropboxRevision is not null ||
                    parsed.LogicalSize != 0 || parsed.ContentHash is not null || parsed.ModifiedUtc is not null) return false;
            }
            else
            {
                var normalized = PilotPath.NormalizeRelative(parsed.RelativePath);
                if (!string.Equals(normalized, parsed.RelativePath, StringComparison.Ordinal)) return false;
            }
            if (parsed.ObjectKind == "file" &&
                (string.IsNullOrWhiteSpace(parsed.DropboxId) ||
                 string.IsNullOrWhiteSpace(parsed.DropboxRevision) ||
                 parsed.ContentHash is null || parsed.ContentHash.Length != 64 ||
                 parsed.ContentHash.Any(character => !Uri.IsHexDigit(character))))
                return false;
            identity = parsed;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return false;
        }
    }
}

public sealed record WindowsCloudPlaceholderInfo(
    WindowsCloudFileIdentity Identity,
    SyncCloudFileState State,
    ulong FileId,
    ulong SyncRootFileId,
    bool IsDirectory,
    uint RawPlaceholderState);

/// <summary>
/// Owns a registered Windows Cloud Files sync root for one Sync Shark pair. Registration is durable;
/// disposing the provider disconnects callbacks but deliberately does not unregister the sync root.
/// </summary>
public sealed partial class WindowsCloudFilesProvider : IDisposable, ICloudFilesConnection
{
    private const long CloudFilesEndOfFile = -1;
    private const int HResultAlreadyExists = unchecked((int)0x800700B7);
    private const int StatusSuccess = 0;
    private const int StatusCancelled = unchecked((int)0xC0000120);
    private const int StatusUnsuccessful = unchecked((int)0xC0000001);
    private const int TransferChunkBytes = 4 * 1024 * 1024;
    private SyncPair _pair;
    private readonly ISyncRemoteRangeStore? _ranges;
    private readonly Func<CancellationToken, Task> _verifyRemoteRoot;
    private readonly string _temporaryRoot;
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _transfers = new();
    private readonly Native.CloudCallback _fetchCallback;
    private readonly Native.CloudCallback _cancelCallback;
    private GCHandle _selfHandle;
    private IntPtr _callbackTable;
    private long _connectionKey;
    private int _disposed;
    internal static Func<string, long, bool>? SufficientDiskSpaceForTest { get; set; }
    internal static Action<string>? BeforeDehydrateForTest { get; set; }

    public WindowsCloudFilesProvider(SyncPair pair, ISyncRemoteStore remote, string temporaryRoot)
        : this(pair, remote as ISyncRemoteRangeStore, remote.VerifyRootIdentityAsync, temporaryRoot) { }

    private WindowsCloudFilesProvider(SyncPair pair, ISyncRemoteRangeStore? ranges,
        Func<CancellationToken, Task> verifyRemoteRoot, string temporaryRoot)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Cloud Files requires Windows.");
        if (!pair.FilesOnDemandEnabled)
            throw new InvalidOperationException("The pair has not enabled Files On-Demand.");
        _pair = pair;
        _ranges = ranges;
        _verifyRemoteRoot = verifyRemoteRoot;
        _temporaryRoot = Path.GetFullPath(temporaryRoot);
        _fetchCallback = FetchDataCallback;
        _cancelCallback = CancelFetchDataCallback;
    }

    public string PairId => _pair.Id;
    internal static (ulong LocalId, string? RemoteId) ReadStorageFileIdentity(string path, string pairId, string? provider = null)
    {
        using var opened = OpenAttributes(path, writeDac: false, exclusive: false);
        if (!PilotDirectoryLease.GetFileInformationByHandle(opened.Handle, out var info))
            throw new IOException("The file identity could not be inspected without downloading it.");
        if ((info.FileAttributes & FileAttributes.Directory) != 0) return (0, null);
        var id = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        if ((info.FileAttributes & FileAttributes.ReparsePoint) == 0) return (id, null);
        if (!TryReadOwnedPlaceholder(path, out var cloud) || cloud is null || !cloud.Identity.IsBoundTo(pairId, provider) || cloud.FileId != id)
            throw new IOException("Another provider or link owns this file.");
        return (id, cloud.Identity.DropboxId);
    }
    public string RootPath => _pair.LocalRoot;
    public bool RegisteredByThisInstance { get; private set; }
    public bool IsConnected => Volatile.Read(ref _connectionKey) != 0;
    private uint _publishedStatus = 1;

    public void PublishSyncStatus(bool syncing, bool needsAttention)
    {
        var status = needsAttention ? 0xC0000002u : syncing ? 0x10u : 1u;
        var key = Volatile.Read(ref _connectionKey);
        if (key == 0 || Volatile.Read(ref _disposed) != 0 || status == _publishedStatus) return;
        Check(Native.CfUpdateSyncProviderStatus(key, status), "Publishing the current Sync Shark state");
        _publishedStatus = status;
    }

    public void UpdatePairPolicy(SyncPair pair)
    {
        if (pair.Id != _pair.Id || pair.ConnectionId != _pair.ConnectionId || pair.Provider != _pair.Provider ||
            pair.LocalRootFileId != _pair.LocalRootFileId || pair.LocalVolumeSerialNumber != _pair.LocalVolumeSerialNumber ||
            !string.Equals(pair.LocalRoot, _pair.LocalRoot, StringComparison.OrdinalIgnoreCase) ||
            pair.DropboxFolderId != _pair.DropboxFolderId)
            throw new InvalidOperationException("A connected Cloud Files provider cannot change account or root identity.");
        _pair = pair;
    }

    public static bool TryGetPlatformVersion(out string version)
    {
        version = "Unavailable";
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var result = Native.CfGetPlatformInfo(out var info);
            if (result < 0) return false;
            version = string.Create(CultureInfo.InvariantCulture,
                $"{info.BuildNumber}.{info.RevisionNumber}.{info.IntegrationNumber}");
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    public void RegisterAndConnect()
    {
        ThrowIfDisposed();
        if (IsConnected) return;
        if (!TryGetPlatformVersion(out var platform))
            throw new PlatformNotSupportedException(
                "Windows Cloud Files is unavailable; Windows 10 version 1709 or later is required.");
        var root = Path.GetFullPath(_pair.LocalRoot);
        if (!Directory.Exists(root) || File.Exists(root))
            throw new DirectoryNotFoundException("The Files On-Demand sync root does not exist.");
        using var rootLease = PilotDirectoryLease.Open(root,
            new PilotDirectoryIdentity(_pair.LocalVolumeSerialNumber, _pair.LocalRootFileId));
        if (ReconnectBoundRoot(root)) { rootLease.VerifyBinding(); return; }
        var hadOwnedRegistration = TryReadOwnedPlaceholder(root, out var priorRegistration) &&
                                   priorRegistration is not null;
        if (hadOwnedRegistration &&
            (priorRegistration!.Identity.AccountId is not null ||
             !priorRegistration.Identity.CanRefreshRootFor(_pair.Id, _pair.Provider)))
            throw new IOException(
                "The local folder is already a Cloud Files sync root owned by another Sync Shark pair.");
        var registeredDuringCurrentTransaction = RegisteredByThisInstance;

        var identity = (_binding?.RootIdentity() ?? WindowsCloudFileIdentity.Root(_pair.Id, _pair.Provider)).Serialize();
        var identityHandle = GCHandle.Alloc(identity, GCHandleType.Pinned);
        try
        {
            var registration = new Native.SyncRegistration
            {
                StructSize = checked((uint)Marshal.SizeOf<Native.SyncRegistration>()),
                ProviderName = "Sync Shark",
                ProviderVersion = BuildIdentity.SourceRevision,
                SyncRootIdentity = identityHandle.AddrOfPinnedObject(),
                SyncRootIdentityLength = checked((uint)identity.Length),
                FileIdentity = identityHandle.AddrOfPinnedObject(),
                FileIdentityLength = checked((uint)identity.Length)
            };
            var policies = new Native.SyncPolicies
            {
                StructSize = checked((uint)Marshal.SizeOf<Native.SyncPolicies>()),
                Hydration = new Native.HydrationPolicy { Primary = 2, Modifier = 0x0004 },
                Population = new Native.PopulationPolicy { Primary = 3, Modifier = 0 },
                InSync = 0x00000100,
                HardLink = 0
            };
            var result = Native.CfRegisterSyncRoot(root, ref registration, ref policies, 0x00000004);
            if (result == HResultAlreadyExists)
            {
                if (_binding is not null)
                    throw new IOException("A registration appeared while binding this root; it was not changed.");
                if (!TryReadOwnedPlaceholder(root, out var existing) || existing is null ||
                    existing.Identity.AccountId is not null ||
                    !existing.Identity.CanRefreshRootFor(_pair.Id, _pair.Provider))
                    throw new IOException(
                        "The local folder is already a Cloud Files sync root owned by another provider or pair.");
                Check(Native.CfRegisterSyncRoot(root, ref registration, ref policies, 0x00000001 | 0x00000004),
                    "Updating the Sync Shark Cloud Files registration");
            }
            else
            {
                Check(result, $"Registering the Sync Shark Cloud Files root on platform {platform}");
                // CfRegisterSyncRoot may report success while refreshing the same retained
                // registration. Only a root that was not already owned was created by this
                // transaction and may be rolled back by it.
                RegisteredByThisInstance = registeredDuringCurrentTransaction || !hadOwnedRegistration;
            }
        }
        finally
        {
            identityHandle.Free();
        }

        try
        {
            rootLease.VerifyBinding();
            Connect(root);
        }
        catch (Exception original)
        {
            try { if (RegisteredByThisInstance) RollBackNewRegistration(); }
            catch (Exception rollback) { throw new AggregateException(original, rollback); }
            throw;
        }
    }

    /// <summary>Registers Explorer status metadata for an ordinary pair. No descendant is
    /// converted, downloaded or freed, and Windows disallows dehydration in this root.</summary>
    public static void RegisterStatusRoot(SyncPair pair)
    {
        if (pair.FilesOnDemandEnabled) throw new InvalidOperationException("Managed storage must use its connected provider.");
        using var root = PilotDirectoryLease.Open(pair.LocalRoot,
            new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
        if (IsOwnedSyncRoot(root.Path, pair.Id))
        {
            if (!IsStatusOnlySyncRoot(pair))
                throw new IOException("This folder still has managed storage. Make its files local before changing registration.");
            return;
        }
        if (ReadRegisteredSyncRootFileId(root.Path) is not null || IsCloudFilesPlaceholder(root.Path))
            throw new IOException("Another cloud provider owns this folder or its parent. Its registration was not changed.");
        var identity = WindowsCloudFileIdentity.Root(pair.Id, pair.Provider).Serialize();
        var pinned = GCHandle.Alloc(identity, GCHandleType.Pinned);
        try
        {
            var registration = new Native.SyncRegistration {
                StructSize = checked((uint)Marshal.SizeOf<Native.SyncRegistration>()), ProviderName = "Sync Shark",
                ProviderVersion = BuildIdentity.SourceRevision, SyncRootIdentity = pinned.AddrOfPinnedObject(),
                SyncRootIdentityLength = checked((uint)identity.Length), FileIdentity = pinned.AddrOfPinnedObject(),
                FileIdentityLength = checked((uint)identity.Length) };
            var policies = new Native.SyncPolicies {
                StructSize = checked((uint)Marshal.SizeOf<Native.SyncPolicies>()),
                Hydration = new Native.HydrationPolicy { Primary = 3, Modifier = 0 },
                Population = new Native.PopulationPolicy { Primary = 3, Modifier = 0 },
                InSync = 0x00000100, HardLink = 0 };
            root.VerifyBinding();
            Check(Native.CfRegisterSyncRoot(root.Path, ref registration, ref policies, 0x00000004),
                "Registering Explorer status for the ordinary folder pair");
            root.VerifyBinding();
            if (!IsStatusOnlySyncRoot(pair)) throw new IOException("Windows did not confirm the exact status-only folder registration.");
        }
        finally { pinned.Free(); }
    }

    public static bool IsStatusOnlySyncRoot(SyncPair pair)
    {
        if (!IsOwnedSyncRoot(pair.LocalRoot, pair.Id)) return false;
        var buffer = new byte[8192];
        Check(Native.CfGetSyncRootInfoByPath(pair.LocalRoot, 1, buffer, buffer.Length, out var returned),
            "Reading the registered folder policy");
        if (returned < 16 || returned > buffer.Length) throw new IOException("Windows returned an incomplete folder policy.");
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(buffer) == pair.LocalRootFileId &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(8)) == 3 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(10)) == 0;
    }

    public static void UnregisterStatusRoot(SyncPair pair)
    {
        using var root = PilotDirectoryLease.Open(pair.LocalRoot,
            new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
        if (!IsOwnedSyncRoot(root.Path, pair.Id)) return;
        if (pair.FilesOnDemandEnabled || !IsStatusOnlySyncRoot(pair))
            throw new InvalidOperationException("Managed files must be made fully local before registration can be removed.");
        // ALWAYS_FULL never permits online-only data. Only remove our exact status-only
        // registration; do not traverse, adopt or change any child object or foreign root.
        root.VerifyBinding(); Check(Native.CfUnregisterSyncRoot(root.Path), "Removing the status-only folder registration");
        root.VerifyBinding();
        if (IsOwnedSyncRoot(root.Path, pair.Id)) throw new IOException("Windows retained the status-only folder registration.");
    }

    public void RollBackNewRegistration()
    {
        ThrowIfDisposed();
        if (!RegisteredByThisInstance) return;
        using var root = PilotDirectoryLease.Open(_pair.LocalRoot,
            new PilotDirectoryIdentity(_pair.LocalVolumeSerialNumber, _pair.LocalRootFileId));
        if (!TryReadOwnedPlaceholder(root.Path, out var info) || info is null ||
            !info.Identity.IsBoundTo(_pair.Id, _pair.Provider))
            throw new IOException("The new registration binding changed; rollback was refused.");
        _binding?.RequireRoot(info);
        RequireNoDependentPointers();
        root.VerifyBinding();
        Disconnect();
        Check(Native.CfUnregisterSyncRoot(_pair.LocalRoot), "Rolling back the Cloud Files registration");
        RegisteredByThisInstance = false;
    }

    public async Task UnregisterAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!IsOwnedSyncRoot(_pair.LocalRoot, _pair.Id, _pair.Provider))
            throw new IOException("The local folder is not the exact Sync Shark Cloud Files root.");
        var enumerationFailures = new List<string>();
        var descendants = EnumerateAvailabilityPaths(_pair.LocalRoot, true, enumerationFailures, cancellationToken);
        if (enumerationFailures.Count != 0)
            throw new IOException("Cloud Files unregistration could not inspect every descendant safely: " + string.Join("; ", enumerationFailures));
        foreach (var path in descendants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadOwnedPlaceholder(path, out var placeholder) || placeholder is null ||
                placeholder.IsDirectory ||
                !placeholder.Identity.IsBoundTo(_pair.Id, _pair.Provider))
                continue;
            if (!placeholder.State.IsInSync)
            {
                if (placeholder.State.Availability != SyncCloudFileAvailability.AvailableOffline)
                    throw new IOException(
                        "Cloud Files unregistration stopped because a pending local upload is not fully resident.");
                // A dirty, fully resident placeholder may be larger or otherwise differ from
                // its last Dropbox identity. Preserve those local bytes exactly and let the
                // ordinary file remain after root unregistration.
                continue;
            }
            var relative = PilotPath.NormalizeRelative(Path.GetRelativePath(_pair.LocalRoot, path));
            var hydrated = await SetAvailabilityAsync(relative, keepOnDevice: true,
                recursive: false, cancellationToken);
            if (hydrated.Failures.Count != 0)
                throw new IOException("Cloud Files unregistration could not make every file available offline: " +
                                      string.Join("; ", hydrated.Failures));
            if (!TryReadOwnedPlaceholder(path, out var afterHydration) || afterHydration is null ||
                afterHydration.State.Availability != SyncCloudFileAvailability.AvailableOffline ||
                afterHydration.State.OnDiskBytes != afterHydration.State.LogicalSize)
                throw new IOException(
                    "Cloud Files unregistration stopped because a placeholder is not fully available offline.");
        }
        Disconnect();
        try
        {
            Check(Native.CfUnregisterSyncRoot(_pair.LocalRoot),
                "Unregistering the Sync Shark Cloud Files root");
            RegisteredByThisInstance = false;
            if (IsOwnedSyncRoot(_pair.LocalRoot, _pair.Id))
                throw new IOException("The Cloud Files root remained registered after unregistration.");
        }
        catch
        {
            if (IsOwnedSyncRoot(_pair.LocalRoot, _pair.Id) && !IsConnected)
                Connect(_pair.LocalRoot);
            throw;
        }
    }

    public WindowsCloudPlaceholderInfo CreatePlaceholder(PilotRemoteEntry remote)
    {
        ThrowIfDisposed();
        if (!IsConnected) throw new InvalidOperationException("The Cloud Files provider is not connected.");
        var identity = WindowsCloudFileIdentity.FromRemote(_pair.Id, remote, _pair.Provider);
        var relative = identity.RelativePath;
        var target = PilotPath.ResolveContainedPath(_pair.LocalRoot, relative);
        if (File.Exists(target) || Directory.Exists(target))
        {
            if (TryReadOwnedPlaceholder(target, out var current) && current is not null &&
                SameIdentity(current.Identity, identity))
                return current;
            throw new IOException("A different local object already occupies the Cloud Files placeholder target.");
        }

        var parentRelative = Path.GetDirectoryName(relative.Replace('/', Path.DirectorySeparatorChar));
        var parent = string.IsNullOrWhiteSpace(parentRelative)
            ? _pair.LocalRoot
            : PilotPath.ResolveContainedPath(_pair.LocalRoot,
                parentRelative.Replace(Path.DirectorySeparatorChar, '/'));
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("The Cloud Files placeholder parent is absent.");
        var name = Path.GetFileName(target);
        var identityBytes = identity.Serialize();
        var identityHandle = GCHandle.Alloc(identityBytes, GCHandleType.Pinned);
        try
        {
            var create = new Native.PlaceholderCreateInfo
            {
                RelativeFileName = name,
                FsMetadata = Metadata(identity),
                FileIdentity = identityHandle.AddrOfPinnedObject(),
                FileIdentityLength = checked((uint)identityBytes.Length),
                // The namespace is populated exhaustively by Sync Shark, so a child
                // directory must be marked fully populated as well as in sync.  A
                // zero flag creates a partial directory, which is invalid beneath
                // an ALWAYS_FULL population-policy root.
                Flags = identity.ObjectKind == "directory" ? 0x00000003u : 0x00000002u
            };
            Check(Native.CfCreatePlaceholders(parent, ref create, 1, 0x00000001, out var processed),
                "Creating the Cloud Files placeholder");
            if (processed != 1 || create.Result < 0)
                Check(create.Result < 0 ? create.Result : StatusUnsuccessful,
                    "Verifying the Cloud Files placeholder result");
        }
        finally
        {
            identityHandle.Free();
        }

        using (var opened = OpenAttributes(target, writeDac: true, exclusive: true))
        {
            var keepDirectory = remote.Kind == PilotEntryKind.Directory &&
                SyncDirectoryStoragePolicy.Resolve(_pair, relative) == SyncDirectoryStorage.AlwaysAvailable;
            Check(Native.CfSetPinState(opened.Handle.DangerousGetHandle(), keepDirectory ? 1 : 2, 0, IntPtr.Zero),
                "Applying the new placeholder's saved directory storage policy");
            if (remote.Kind == PilotEntryKind.File)
                Check(Native.CfDehydratePlaceholder(opened.Handle.DangerousGetHandle(), 0,
                    CloudFilesEndOfFile, 0,
                    IntPtr.Zero), "Removing unverified local bytes from the new Cloud Files placeholder");
        }

        if (!TryReadOwnedPlaceholder(target, out var result) || result is null ||
            !SameIdentity(result.Identity, identity))
            throw new IOException("The created Cloud Files placeholder did not retain its exact Dropbox identity.");
        return result;
    }

    public WindowsCloudPlaceholderInfo UpdatePlaceholder(PilotRemoteEntry remote, bool dehydrate)
    {
        ThrowIfDisposed();
        var identity = WindowsCloudFileIdentity.FromRemote(_pair.Id, remote, _pair.Provider);
        var target = PilotPath.ResolveContainedPath(_pair.LocalRoot, identity.RelativePath);
        if (!TryReadOwnedPlaceholder(target, out var current) || current is null ||
            !current.Identity.IsBoundTo(_pair.Id, _pair.Provider))
            throw new IOException("The local target is not a Sync Shark Cloud Files placeholder for this pair.");
        using var opened = OpenAttributes(target, writeDac: true, exclusive: true);
        var bytes = identity.Serialize();
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var metadata = Metadata(identity);
            var flags = 0x00000001u | 0x00000002u | (dehydrate ? 0x00000004u : 0u);
            Check(Native.CfUpdatePlaceholder(opened.Handle.DangerousGetHandle(), ref metadata,
                pinned.AddrOfPinnedObject(),
                checked((uint)bytes.Length), IntPtr.Zero, 0, flags, IntPtr.Zero, IntPtr.Zero),
                "Updating the Cloud Files placeholder");
        }
        finally
        {
            pinned.Free();
        }
        if (!TryReadOwnedPlaceholder(target, out var result) || result is null ||
            !SameIdentity(result.Identity, identity))
            throw new IOException("The updated Cloud Files placeholder identity did not verify.");
        return result;
    }

    public WindowsCloudPlaceholderInfo ConvertSyncedFile(PilotRemoteEntry remote, bool dehydrate)
    {
        ThrowIfDisposed();
        if (remote.Kind != PilotEntryKind.File)
            throw new ArgumentException("Only a regular synced file can be converted.", nameof(remote));
        var identity = WindowsCloudFileIdentity.FromRemote(_pair.Id, remote, _pair.Provider);
        var target = PilotPath.ResolveContainedPath(_pair.LocalRoot, identity.RelativePath);
        if (!File.Exists(target) || Directory.Exists(target))
            throw new FileNotFoundException("The synced file selected for conversion is absent.", target);
        if (TryReadOwnedPlaceholder(target, out var existing) && existing is not null)
            return SameIdentity(existing.Identity, identity)
                ? existing
                : UpdatePlaceholder(remote, dehydrate);
        using var opened = OpenAttributes(target, writeDac: true, exclusive: true);
        var bytes = identity.Serialize();
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var flags = 0x00000001u | (dehydrate ? 0x00000002u : 0u);
            Check(Native.CfConvertToPlaceholder(opened.Handle.DangerousGetHandle(),
                pinned.AddrOfPinnedObject(),
                checked((uint)bytes.Length), flags, IntPtr.Zero, IntPtr.Zero),
                "Converting the synced file to a Cloud Files placeholder");
        }
        finally
        {
            pinned.Free();
        }
        if (!TryReadOwnedPlaceholder(target, out var result) || result is null ||
            !SameIdentity(result.Identity, identity))
            throw new IOException("The converted Cloud Files placeholder identity did not verify.");
        return result;
    }

    public async Task<WindowsCloudPlaceholderInfo> ConvertVerifiedSyncedFileAsync(PilotRemoteEntry remote,
        CancellationToken cancellationToken = default, bool? dehydrateOverride = null)
    {
        ThrowIfDisposed();
        if (remote.Kind != PilotEntryKind.File || string.IsNullOrWhiteSpace(remote.ContentHash))
            throw new ArgumentException("A verified cloud file is required for online-only conversion.",
                nameof(remote));
        var identity = WindowsCloudFileIdentity.FromRemote(_pair.Id, remote, _pair.Provider);
        var dehydrate = dehydrateOverride ?? (SyncDirectoryStoragePolicy.Resolve(_pair, identity.RelativePath) == SyncDirectoryStorage.CloudOnly);
        var target = PilotPath.ResolveContainedPath(_pair.LocalRoot, identity.RelativePath);
        if (TryReadOwnedPlaceholder(target, out var existing) && existing is not null)
        {
            if (!existing.Identity.IsBoundTo(_pair.Id, _pair.Provider) ||
                existing.Identity.RelativePath != identity.RelativePath)
                throw new IOException("Another provider, pair or path owns this placeholder.");
            if (dehydrate && SameIdentity(existing.Identity, identity) &&
                existing.State.Availability == SyncCloudFileAvailability.OnlineOnly &&
                existing.State.IsInSync)
                return existing;

            BeforeDehydrateForTest?.Invoke(target);
            await using var localPlaceholder = SyncLocalFileAuthority.OpenExistingExclusive(target);
            if (localPlaceholder.Stream.Length != remote.Size)
                throw new IOException("The local file changed size after upload and remains available offline.");
            localPlaceholder.Stream.Position = 0;
            var placeholderHash = await SyncContentIdentity.ComputeAsync(_pair.Provider, localPlaceholder.Stream, cancellationToken);
            if (!string.Equals(placeholderHash, remote.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The local file changed after upload and remains available offline.");
            var placeholderIdentity = identity.Serialize();
            var placeholderIdentityHandle = GCHandle.Alloc(placeholderIdentity, GCHandleType.Pinned);
            try
            {
                var metadata = Metadata(identity);
                var markInSyncAndDehydrate = 0x00000002u | (dehydrate ? 0x00000004u : 0u);
                Check(Native.CfSetPinState(localPlaceholder.Handle.DangerousGetHandle(), dehydrate ? 2 : 1, 0,
                    IntPtr.Zero), "Applying the saved storage policy to the verified uploaded placeholder");
                Check(Native.CfUpdatePlaceholder(localPlaceholder.Handle.DangerousGetHandle(), ref metadata,
                    placeholderIdentityHandle.AddrOfPinnedObject(), checked((uint)placeholderIdentity.Length),
                    IntPtr.Zero, 0, markInSyncAndDehydrate, IntPtr.Zero, IntPtr.Zero),
                    "Updating the verified uploaded placeholder to online-only");
            }
            finally { placeholderIdentityHandle.Free(); }

            if (!TryReadOwnedPlaceholder(target, out var updated) || updated is null ||
                !SameIdentity(updated.Identity, identity) || !updated.State.IsInSync ||
                (dehydrate && updated.State.Availability != SyncCloudFileAvailability.OnlineOnly))
                throw new IOException("The online-only file did not retain its verified Dropbox identity.");
            return updated;
        }

        BeforeDehydrateForTest?.Invoke(target);
        await using (var local = SyncLocalFileAuthority.OpenExistingExclusive(target))
        {
            if (local.Stream.Length != remote.Size)
                throw new IOException("The local file changed size after upload and remains available offline.");
            local.Stream.Position = 0;
            var hash = await SyncContentIdentity.ComputeAsync(_pair.Provider, local.Stream, cancellationToken);
            if (!string.Equals(hash, remote.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The local file changed after upload and remains available offline.");
            var bytes = identity.Serialize();
            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var convertAndDehydrate = 0x00000001u | (dehydrate ? 0x00000002u : 0u);
                Check(Native.CfConvertToPlaceholder(local.Handle.DangerousGetHandle(),
                    pinned.AddrOfPinnedObject(), checked((uint)bytes.Length), convertAndDehydrate,
                    IntPtr.Zero, IntPtr.Zero),
                    "Converting the verified uploaded file to an online-only placeholder");
                Check(Native.CfSetPinState(local.Handle.DangerousGetHandle(), dehydrate ? 2 : 1, 0, IntPtr.Zero),
                    "Applying the saved folder storage policy");
            }
            finally { pinned.Free(); }
        }
        if (!TryReadOwnedPlaceholder(target, out var result) || result is null ||
            !SameIdentity(result.Identity, identity) || !result.State.IsInSync)
            throw new IOException("The online-only file did not retain its verified Dropbox identity.");
        return result;
    }

    public async Task<SyncCloudAvailabilityResult> SetAvailabilityAsync(string relativePath, bool keepOnDevice,
        bool recursive = true, CancellationToken cancellationToken = default, Func<string, bool>? includePath = null,
        Func<SyncComparisonProgress, Task>? onProgress = null)
    {
        ThrowIfDisposed();
        var relative = PilotPath.NormalizeRelative(relativePath);
        var target = PilotPath.ResolveContainedPath(_pair.LocalRoot, relative);
        if (!File.Exists(target) && !Directory.Exists(target))
            throw new FileNotFoundException("The selected Cloud Files path is absent.", target);
        var failures = new List<string>();
        var fileCount = 0;
        var directoryCount = 0;
        var started = DateTimeOffset.UtcNow;
        var inspected = 0;
        var total = 0;
        async Task ReportAsync(string item, string detail)
        {
            if (onProgress is null) return;
            try
            {
                await onProgress(new SyncComparisonProgress("Applying storage choices", item,
                    inspected, total, started, DateTimeOffset.UtcNow, detail, "paths inspected"));
            }
            catch { /* Optional diagnostic delivery cannot replace a native operation's result. */ }
        }
        await ReportAsync(relative, "Enumerating the selected managed subtree for Windows pin/hydration changes.");
        var paths = EnumerateAvailabilityPaths(target, recursive, failures, cancellationToken);
        total = paths.Count;
        await ReportAsync(relative, $"Enumerated {total} paths; inspecting native ownership before changing availability.");
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var itemRelative = PilotPath.NormalizeRelative(Path.GetRelativePath(_pair.LocalRoot, path));
            await ReportAsync(itemRelative, "Inspecting Windows placeholder ownership and the saved storage choice.");
            if (includePath is not null && !includePath(itemRelative))
            {
                inspected++;
                await ReportAsync(itemRelative, "The saved selection/storage policy excludes this path from this availability change.");
                continue;
            }
            if (!TryReadOwnedPlaceholder(path, out var placeholder) || placeholder is null ||
                !placeholder.Identity.IsBoundTo(_pair.Id, _pair.Provider))
            {
                inspected++;
                await ReportAsync(itemRelative, "This is not a placeholder owned by this pair; it was inspected and left unchanged.");
                continue;
            }
            inspected++;
            try
            {
                if (!placeholder.IsDirectory && keepOnDevice &&
                    !HasSufficientDiskSpace(path,
                        Math.Max(0, placeholder.State.LogicalSize - placeholder.State.OnDiskBytes)))
                    throw new IOException(
                        "The selected volume does not have enough available space to keep this item on the device.");
                if (!placeholder.IsDirectory && !keepOnDevice && !placeholder.State.IsInSync)
                    throw new IOException(
                        "This item has local changes or a pending upload and cannot be made online-only yet.");
                using var opened = OpenAttributes(path, writeDac: true, exclusive: !keepOnDevice);
                await ReportAsync(itemRelative, keepOnDevice
                    ? "Pinning the managed item to remain available on this device."
                    : "Unpinning the verified managed item for online-only availability.");
                Check(Native.CfSetPinState(opened.Handle.DangerousGetHandle(), keepOnDevice ? 1 : 2,
                    0, IntPtr.Zero),
                    keepOnDevice ? "Pinning the Cloud Files item" : "Unpinning the Cloud Files item");
                if (!placeholder.IsDirectory)
                {
                    if (keepOnDevice)
                    {
                        await ReportAsync(itemRelative, "Awaiting Windows hydration of this file from its verified provider revision.");
                        Check(Native.CfHydratePlaceholder(opened.Handle.DangerousGetHandle(), 0,
                            CloudFilesEndOfFile, 0,
                            IntPtr.Zero), "Hydrating the Cloud Files item");
                        await ReportAsync(itemRelative, "Verifying this hydrated file is fully available and retains its exact provider identity.");
                        await VerifyFullyHydratedAsync(path, placeholder.Identity,
                            cancellationToken);
                    }
                    else
                    {
                        BeforeDehydrateForTest?.Invoke(path);
                        await ReportAsync(itemRelative, "Freeing verified synchronized bytes through Windows dehydration; cloud content is not deleted.");
                        Check(Native.CfDehydratePlaceholder(opened.Handle.DangerousGetHandle(), 0,
                            CloudFilesEndOfFile, 0,
                            IntPtr.Zero), "Dehydrating the Cloud Files item");
                    }
                    fileCount++;
                }
                else
                    directoryCount++;
                await ReportAsync(itemRelative,
                    $"Native availability change completed; {fileCount} files and {directoryCount} directories changed so far.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                var rollbackFailure = string.Empty;
                if (keepOnDevice && !placeholder.IsDirectory)
                {
                    try
                    {
                        await ReportAsync(itemRelative, "Hydration failed; restoring the previous online-only pin state.");
                        using var rollback = OpenAttributes(path, writeDac: true, exclusive: false);
                        Check(Native.CfSetPinState(rollback.Handle.DangerousGetHandle(), 2, 0,
                            IntPtr.Zero), "Restoring the online-only pin state");
                    }
                    catch (Exception rollbackException) when (rollbackException is IOException or
                                                               UnauthorizedAccessException)
                    {
                        rollbackFailure = " Pin-state rollback also failed: " + rollbackException.Message;
                    }
                }
                failures.Add(PilotPath.NormalizeRelative(Path.GetRelativePath(_pair.LocalRoot, path)) +
                             ": " + exception.Message + rollbackFailure);
                await ReportAsync(itemRelative, "Availability change failed: " + SyncActivityText.Failure(exception) +
                    SyncActivityText.Safe(rollbackFailure));
            }
            await Task.Yield();
        }
        await ReportAsync(relative,
            $"Storage availability finished: {fileCount} files and {directoryCount} directories changed; {failures.Count} failures retained.");
        return new SyncCloudAvailabilityResult(relative, keepOnDevice, fileCount, directoryCount, failures);
    }

    private IReadOnlyList<string> EnumerateAvailabilityPaths(string target, bool recursive,
        ICollection<string> failures, CancellationToken cancellationToken)
    {
        var result = new List<string>(); var pending = new Stack<string>(); pending.Push(target);
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested(); var path = pending.Pop();
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0 &&
                    (!TryReadOwnedPlaceholder(path, out var owned) || owned is null || !owned.Identity.IsBoundTo(_pair.Id, _pair.Provider)))
                { failures.Add(Path.GetRelativePath(_pair.LocalRoot, path) + ": another provider or reparse point was not followed."); continue; }
                result.Add(path);
                if (recursive && (attributes & FileAttributes.Directory) != 0)
                    foreach (var child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { failures.Add(Path.GetRelativePath(_pair.LocalRoot, path) + ": folder storage could not be inspected safely."); }
        }
        return result;
    }

    public static bool IsOwnedSyncRoot(string path, string? expectedPairId = null, string? expectedProvider = null)
    {
        return TryReadOwnedPlaceholder(path, out var info) && info is not null &&
               info.Identity.ObjectKind == "root" &&
               (expectedPairId is null || string.Equals(info.Identity.PairId, expectedPairId,
                   StringComparison.Ordinal)) &&
               (expectedProvider is null || info.Identity.IsBoundTo(expectedPairId ?? info.Identity.PairId, expectedProvider));
    }

    /// <summary>Distinguishes no native registration from an unreadable registration. Never
    /// treats a permission or other unexpected failure as permission to replace a provider.</summary>
    public static ulong? ReadRegisteredSyncRootFileId(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var data = new byte[8];
        var result = Native.CfGetSyncRootInfoByPath(Path.GetFullPath(path), 0, data, data.Length, out var returned);
        if (result is unchecked((int)0x80070178) or unchecked((int)0x80070186) or unchecked((int)0x80070195)) return null;
        Check(result, "Inspecting native cloud-folder ownership");
        if (returned != 8) throw new IOException("Windows returned incomplete cloud-folder ownership information.");
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data);
    }

    public static bool IsCloudFilesPlaceholder(string path)
    {
        if (!OperatingSystem.IsWindows() || (!File.Exists(path) && !Directory.Exists(path))) return false;
        try
        {
            using var opened = OpenAttributes(path, writeDac: false, exclusive: false);
            if (!Native.GetFileInformationByHandleEx(opened.Handle.DangerousGetHandle(), 9, out var tag,
                    checked((uint)Marshal.SizeOf<Native.FileAttributeTagInfo>()))) return false;
            return Native.CfGetPlaceholderStateFromAttributeTag(tag.FileAttributes, tag.ReparseTag) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // B-169: foreign identities are opaque. Query only native state on the held object, never its data.
    internal sealed record StubState(uint PlaceholderState, long OnDiskBytes, long ModifiedBytes,
        int InSyncState, ulong FileId)
    {
        internal bool FullyDehydrated => PlaceholderState != uint.MaxValue &&
            (PlaceholderState & 1) != 0 && (PlaceholderState & 0x10) != 0 && (PlaceholderState & 0x20) == 0 &&
            OnDiskBytes == 0 && ModifiedBytes == 0 && InSyncState == 1;
    }

    internal static Func<SafeFileHandle, StubState?>? StubStateForTest { get; set; }

    internal static StubState? ReadStubState(SafeFileHandle handle)
    {
        if (StubStateForTest is { } query) return query(handle);
        const int capacity = 16 * 1024;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            var raw = handle.DangerousGetHandle();
            if (!Native.GetFileInformationByHandleEx(raw, 9, out var tag,
                    (uint)Marshal.SizeOf<Native.FileAttributeTagInfo>())) return null;
            var state = Native.CfGetPlaceholderStateFromAttributeTag(tag.FileAttributes, tag.ReparseTag);
            if (state == uint.MaxValue || (state & 1) == 0) return null;
            var hr = Native.CfGetPlaceholderInfo(raw, 1, buffer, capacity, out var returned);
            if (hr < 0 || returned < 60 || returned > capacity) return null;
            var header = Marshal.PtrToStructure<Native.PlaceholderStandardInfo>(buffer);
            return new StubState(state, header.OnDiskDataSize, header.ModifiedDataSize,
                header.InSyncState, unchecked((ulong)header.FileId));
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        { return null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static bool IsFullyDehydratedStub(string path, ulong expectedFileId)
    {
        try
        {
            // Inventory is advisory; the writer repeats this query under its exclusive no-recall hold.
            using var opened = OpenAttributes(path, writeDac: false, exclusive: false);
            return ReadStubState(opened.Handle) is { FullyDehydrated: true } state &&
                state.FileId == expectedFileId;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return false; }
    }

    public static bool TryReadOwnedPlaceholder(string path, out WindowsCloudPlaceholderInfo? result)
    {
        result = null;
        if (!OperatingSystem.IsWindows() || (!File.Exists(path) && !Directory.Exists(path))) return false;
        try
        {
            using var opened = OpenAttributes(path, writeDac: false, exclusive: false);
            return TryReadOwnedPlaceholder(opened.Handle, Directory.Exists(path), out result);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    // Inspect the very same held file that the scanner/uploader will read. Reopening its path while
    // an exclusive byte handle is held would fail sharing checks or inspect a replacement object.
    internal static bool TryReadOwnedPlaceholder(SafeFileHandle handle, bool directory,
        out WindowsCloudPlaceholderInfo? result)
    {
        result = null;
        try
        {
            const int capacity = 16 * 1024;
            var buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                var rawHandle = handle.DangerousGetHandle();
                var nativeResult = Native.CfGetPlaceholderInfo(rawHandle, 1, buffer, capacity,
                    out var returned);
                if (nativeResult < 0 || returned < 60 || returned > capacity) return false;
                var header = Marshal.PtrToStructure<Native.PlaceholderStandardInfo>(buffer);
                var identityOffset = Marshal.OffsetOf<Native.PlaceholderStandardInfo>(
                    nameof(Native.PlaceholderStandardInfo.FileIdentityLength)).ToInt32() + sizeof(uint);
                if (header.FileIdentityLength is 0 or > 4096 ||
                    identityOffset + header.FileIdentityLength > returned)
                    return false;
                var bytes = new byte[header.FileIdentityLength];
                Marshal.Copy(IntPtr.Add(buffer, identityOffset), bytes, 0, bytes.Length);
                if (!WindowsCloudFileIdentity.TryDeserialize(bytes, out var identity) || identity is null)
                    return false;
                var logicalSize = directory ? 0 : identity.LogicalSize;
                var placeholderState = 0u;
                if (Native.GetFileInformationByHandleEx(rawHandle, 9, out var tag,
                        checked((uint)Marshal.SizeOf<Native.FileAttributeTagInfo>())))
                    placeholderState = Native.CfGetPlaceholderStateFromAttributeTag(tag.FileAttributes,
                        tag.ReparseTag);
                var availability = directory
                    ? SyncCloudFileAvailability.AvailableOffline
                    : (placeholderState & 0x00000010) != 0 &&
                      header.OnDiskDataSize == 0 && header.PinState != 1
                        ? SyncCloudFileAvailability.OnlineOnly
                        : (placeholderState & (0x00000010u | 0x00000020u)) != 0
                            ? SyncCloudFileAvailability.PartiallyHydrated
                            : SyncCloudFileAvailability.AvailableOffline;
                var relative = identity.ObjectKind == "root" ? "." : identity.RelativePath;
                var state = new SyncCloudFileState(relative, availability, header.PinState == 1,
                    header.InSyncState == 1, logicalSize, Math.Max(0, header.OnDiskDataSize),
                    identity.DropboxId, identity.DropboxRevision, identity.ContentHash);
                result = new WindowsCloudPlaceholderInfo(identity, state,
                    unchecked((ulong)header.FileId), unchecked((ulong)header.SyncRootFileId), directory,
                    placeholderState);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private void Connect(string root)
    {
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        var registrations = new[]
        {
            new Native.CallbackRegistration
            {
                Type = 0,
                Callback = Marshal.GetFunctionPointerForDelegate(_fetchCallback)
            },
            new Native.CallbackRegistration
            {
                Type = 2,
                Callback = Marshal.GetFunctionPointerForDelegate(_cancelCallback)
            },
            new Native.CallbackRegistration { Type = -1, Callback = IntPtr.Zero }
        };
        var size = Marshal.SizeOf<Native.CallbackRegistration>();
        _callbackTable = Marshal.AllocHGlobal(checked(size * registrations.Length));
        try
        {
            for (var index = 0; index < registrations.Length; index++)
                Marshal.StructureToPtr(registrations[index], IntPtr.Add(_callbackTable, index * size), false);
            Check(Native.CfConnectSyncRoot(root, _callbackTable, GCHandle.ToIntPtr(_selfHandle),
                0x00000004, out var key), "Connecting the Sync Shark Cloud Files callbacks");
            Volatile.Write(ref _connectionKey, key);
            Check(Native.CfUpdateSyncProviderStatus(key, 1),
                "Publishing the Sync Shark Cloud Files provider status");
        }
        catch
        {
            Disconnect();
            throw;
        }
    }

    private static void FetchDataCallback(IntPtr callbackInfo, IntPtr callbackParameters)
    {
        var info = Marshal.PtrToStructure<Native.CallbackInfo>(callbackInfo);
        if (info.CallbackContext == IntPtr.Zero) return;
        var handle = GCHandle.FromIntPtr(info.CallbackContext);
        if (handle.Target is not WindowsCloudFilesProvider provider) return;
        var parameters = Marshal.PtrToStructure<Native.CallbackParameters>(callbackParameters);
        provider.StartFetch(info, parameters.FetchData);
    }

    private static void CancelFetchDataCallback(IntPtr callbackInfo, IntPtr callbackParameters)
    {
        var info = Marshal.PtrToStructure<Native.CallbackInfo>(callbackInfo);
        if (info.CallbackContext == IntPtr.Zero) return;
        var handle = GCHandle.FromIntPtr(info.CallbackContext);
        if (handle.Target is not WindowsCloudFilesProvider provider) return;
        if (provider._transfers.TryGetValue(info.TransferKey, out var cancellation)) cancellation.Cancel();
    }

    private void StartFetch(Native.CallbackInfo info, Native.CallbackFetchData parameters)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var identityBytes = new byte[info.FileIdentityLength];
        if (identityBytes.Length is 0 or > 4096 || info.FileIdentity == IntPtr.Zero)
        {
            ExecuteTransfer(info, IntPtr.Zero, parameters.RequiredFileOffset,
                parameters.RequiredLength, StatusUnsuccessful);
            return;
        }
        Marshal.Copy(info.FileIdentity, identityBytes, 0, identityBytes.Length);
        if (!WindowsCloudFileIdentity.TryDeserialize(identityBytes, out var identity) || identity is null ||
            identity.ObjectKind != "file" ||
            !identity.IsBoundTo(_pair.Id, _pair.Provider))
        {
            ExecuteTransfer(info, IntPtr.Zero, parameters.RequiredFileOffset,
                parameters.RequiredLength, StatusUnsuccessful);
            return;
        }
        var cancellation = new CancellationTokenSource();
        if (!_transfers.TryAdd(info.TransferKey, cancellation))
        {
            cancellation.Dispose();
            ExecuteTransfer(info, IntPtr.Zero, parameters.RequiredFileOffset,
                parameters.RequiredLength, StatusUnsuccessful);
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await FetchAsync(info, parameters, identity, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                ExecuteTransfer(info, IntPtr.Zero, parameters.RequiredFileOffset,
                    parameters.RequiredLength, StatusCancelled);
            }
            catch
            {
                ExecuteTransfer(info, IntPtr.Zero, parameters.RequiredFileOffset,
                    parameters.RequiredLength, StatusUnsuccessful);
            }
            finally
            {
                _transfers.TryRemove(info.TransferKey, out _);
                cancellation.Dispose();
            }
        });
    }

    private async Task FetchAsync(Native.CallbackInfo info, Native.CallbackFetchData parameters,
        WindowsCloudFileIdentity identity, CancellationToken cancellationToken)
    {
        var offset = parameters.RequiredFileOffset;
        var requestedLength = parameters.RequiredLength == CloudFilesEndOfFile
            ? identity.LogicalSize - offset
            : parameters.RequiredLength;
        if (offset < 0 || requestedLength < 0 || offset + requestedLength > identity.LogicalSize ||
            offset % 4096 != 0 || (requestedLength % 4096 != 0 && offset + requestedLength != identity.LogicalSize))
            throw new IOException("The Cloud Files hydration range is outside the exact object boundary.");
        if (requestedLength == 0)
        {
            ExecuteTransfer(info, IntPtr.Zero, offset, 0, StatusSuccess);
            return;
        }
        if (!HasSufficientDiskSpace(_pair.LocalRoot, requestedLength))
            throw new IOException("The sync root does not have enough space for the requested hydration range.");
        if (_ranges is not { } ranges)
            throw new NotSupportedException("This provider cannot hydrate a bounded Cloud Files range.");
        var expected = new PilotRemoteEntry(identity.RelativePath, PilotEntryKind.File, identity.DropboxId,
            identity.DropboxRevision, identity.LogicalSize, identity.ContentHash, identity.ModifiedUtc);
        await _verifyRemoteRoot(cancellationToken);
        await using var target = new CloudFilesRangeWriteStream(info, offset, requestedLength);
        var downloaded = await ranges.DownloadRangeAsync(expected, offset, requestedLength, target,
            cancellationToken);
        if (target.Written != requestedLength || downloaded.Offset != offset ||
            downloaded.Length != requestedLength || downloaded.TotalSize != identity.LogicalSize ||
            !string.Equals(downloaded.FileId, identity.DropboxId, StringComparison.Ordinal) ||
            !string.Equals(downloaded.Revision, identity.DropboxRevision, StringComparison.Ordinal) ||
            !string.Equals(downloaded.ContentHash, identity.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The hydrated range differs from the exact provider placeholder identity.");
    }

    private sealed class CloudFilesRangeWriteStream(Native.CallbackInfo callback, long offset, long length) : Stream
    {
        public long Written { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Write(byte[] buffer, int bufferOffset, int count) =>
            WriteCore(buffer.AsSpan(bufferOffset, count));

        public override void Write(ReadOnlySpan<byte> buffer) => WriteCore(buffer);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCore(buffer.Span);
            return ValueTask.CompletedTask;
        }

        private void WriteCore(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0) return;
            if (bytes.Length > TransferChunkBytes || Written > length - bytes.Length)
                throw new IOException("A Cloud Files hydration chunk exceeded its bounded range.");
            var managed = bytes.ToArray();
            var native = Marshal.AllocHGlobal(managed.Length);
            try
            {
                Marshal.Copy(managed, 0, native, managed.Length);
                ExecuteTransfer(callback, native, offset + Written, managed.Length, StatusSuccess);
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
            Written += managed.Length;
            _ = Native.CfReportProviderProgress(callback.ConnectionKey, callback.TransferKey, length, Written);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static void ExecuteTransfer(Native.CallbackInfo callback, IntPtr buffer, long offset, long length,
        int status)
    {
        var operation = new Native.OperationInfo
        {
            StructSize = checked((uint)Marshal.SizeOf<Native.OperationInfo>()),
            Type = 0,
            ConnectionKey = callback.ConnectionKey,
            TransferKey = callback.TransferKey,
            CorrelationVector = callback.CorrelationVector
        };
        var parameters = new Native.OperationParameters
        {
            ParamSize = checked((uint)Marshal.SizeOf<Native.OperationParameters>()),
            TransferData = new Native.OperationTransferData
            {
                CompletionStatus = status,
                Buffer = buffer,
                Offset = offset,
                Length = length
            }
        };
        Check(Native.CfExecute(ref operation, ref parameters), "Completing the Cloud Files hydration transfer");
    }

    private static Native.FsMetadata Metadata(WindowsCloudFileIdentity identity)
    {
        var modified = (identity.ModifiedUtc ?? DateTimeOffset.UtcNow).UtcDateTime.ToFileTimeUtc();
        return new Native.FsMetadata
        {
            BasicInfo = new Native.FileBasicInfo
            {
                CreationTime = modified,
                LastAccessTime = modified,
                LastWriteTime = modified,
                ChangeTime = modified,
                FileAttributes = identity.ObjectKind == "directory"
                    ? (uint)FileAttributes.Directory
                    : (uint)FileAttributes.Archive
            },
            FileSize = identity.ObjectKind == "file" ? identity.LogicalSize : 0
        };
    }

    private static bool SameIdentity(WindowsCloudFileIdentity left, WindowsCloudFileIdentity right) =>
        left.Marker == right.Marker && left.SchemaVersion == right.SchemaVersion &&
        left.EffectiveProvider == right.EffectiveProvider && left.EffectiveContentAlgorithm == right.EffectiveContentAlgorithm &&
        string.Equals(left.PairId, right.PairId, StringComparison.Ordinal) &&
        string.Equals(left.RelativePath, right.RelativePath, StringComparison.Ordinal) &&
        left.ObjectKind == right.ObjectKind &&
        string.Equals(left.DropboxId, right.DropboxId, StringComparison.Ordinal) &&
        string.Equals(left.DropboxRevision, right.DropboxRevision, StringComparison.Ordinal) &&
        left.LogicalSize == right.LogicalSize &&
        string.Equals(left.ContentHash, right.ContentHash, StringComparison.OrdinalIgnoreCase);

    private static bool HasSufficientDiskSpace(string path, long requiredBytes)
    {
        if (requiredBytes <= 0) return true;
        if (SufficientDiskSpaceForTest is not null)
            return SufficientDiskSpaceForTest(path, requiredBytes);
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            // Keep a small reserve so an otherwise full volume can still checkpoint
            // authority and filesystem metadata around a failed hydration.
            return new DriveInfo(root).AvailableFreeSpace >= checked(requiredBytes + 16L * 1024 * 1024);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task VerifyFullyHydratedAsync(string path, WindowsCloudFileIdentity expected,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expected.LogicalSize)
            throw new IOException("The hydrated Cloud Files item length differs from its provider identity.");
        var hash = await SyncContentIdentity.ComputeAsync(expected.EffectiveProvider, stream, cancellationToken);
        if (!string.Equals(hash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The hydrated bytes differ from the exact provider checksum.");
        if (!TryReadOwnedPlaceholder(path, out var hydrated) || hydrated is null ||
            !SameIdentity(hydrated.Identity, expected) ||
            hydrated.State.Availability != SyncCloudFileAvailability.AvailableOffline ||
            hydrated.State.OnDiskBytes != hydrated.State.LogicalSize)
            throw new IOException("Windows did not confirm the complete offline copy.");
    }

    internal static AttributeCloudFileHandle OpenAttributes(string path, bool writeDac, bool exclusive)
    {
        const uint fileReadAttributes = 0x00000080;
        const uint writeDacAccess = 0x00040000;
        const uint openExisting = 3;
        const uint fileFlagBackupSemantics = 0x02000000;
        const uint fileFlagOpenReparsePoint = 0x00200000;
        var handle = Native.CreateFile(PilotPath.ToExtendedWindowsPath(path),
            fileReadAttributes | (writeDac ? writeDacAccess : 0), exclusive ? FileShare.None :
            FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, openExisting,
            fileFlagBackupSemantics | fileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            if (error == 2) throw new FileNotFoundException("The selected Cloud Files item no longer exists.", path);
            if (error == 3) throw new DirectoryNotFoundException("The selected Cloud Files folder no longer exists.");
            throw new IOException("The Cloud Files item could not be opened without hydration.",
                Marshal.GetExceptionForHR(unchecked((int)(0x80070000u | (uint)error))));
        }
        return new AttributeCloudFileHandle(handle);
    }

    private static void Check(int result, string operation)
    {
        if (result >= 0) return;
        throw new IOException($"{operation} failed (HRESULT 0x{unchecked((uint)result):x8}).",
            Marshal.GetExceptionForHR(result));
    }

    private void Disconnect()
    {
        var key = Interlocked.Exchange(ref _connectionKey, 0);
        if (key != 0) _ = Native.CfDisconnectSyncRoot(key);
        foreach (var transfer in _transfers.Values) transfer.Cancel();
        if (_callbackTable != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_callbackTable);
            _callbackTable = IntPtr.Zero;
        }
        if (_selfHandle.IsAllocated) _selfHandle.Free();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Disconnect();
    }

    internal sealed class AttributeCloudFileHandle(SafeFileHandle handle) : IDisposable
    {
        public SafeFileHandle Handle { get; } = handle;
        public void Dispose() => Handle.Dispose();
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct PlatformInfo
        {
            internal uint BuildNumber;
            internal uint RevisionNumber;
            internal uint IntegrationNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileBasicInfo
        {
            internal long CreationTime;
            internal long LastAccessTime;
            internal long LastWriteTime;
            internal long ChangeTime;
            internal uint FileAttributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FsMetadata
        {
            internal FileBasicInfo BasicInfo;
            internal long FileSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct SyncRegistration
        {
            internal uint StructSize;
            [MarshalAs(UnmanagedType.LPWStr)] internal string ProviderName;
            [MarshalAs(UnmanagedType.LPWStr)] internal string ProviderVersion;
            internal IntPtr SyncRootIdentity;
            internal uint SyncRootIdentityLength;
            internal IntPtr FileIdentity;
            internal uint FileIdentityLength;
            internal Guid ProviderId;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HydrationPolicy
        {
            internal ushort Primary;
            internal ushort Modifier;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PopulationPolicy
        {
            internal ushort Primary;
            internal ushort Modifier;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SyncPolicies
        {
            internal uint StructSize;
            internal HydrationPolicy Hydration;
            internal PopulationPolicy Population;
            internal uint InSync;
            internal uint HardLink;
            internal uint PlaceholderManagement;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct PlaceholderCreateInfo
        {
            [MarshalAs(UnmanagedType.LPWStr)] internal string RelativeFileName;
            internal FsMetadata FsMetadata;
            internal IntPtr FileIdentity;
            internal uint FileIdentityLength;
            internal uint Flags;
            internal int Result;
            internal long CreateUsn;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void CloudCallback(IntPtr callbackInfo, IntPtr callbackParameters);

        [StructLayout(LayoutKind.Sequential)]
        internal struct CallbackRegistration
        {
            internal int Type;
            internal IntPtr Callback;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct CallbackInfo
        {
            internal uint StructSize;
            internal long ConnectionKey;
            internal IntPtr CallbackContext;
            internal IntPtr VolumeGuidName;
            internal IntPtr VolumeDosName;
            internal uint VolumeSerialNumber;
            internal long SyncRootFileId;
            internal IntPtr SyncRootIdentity;
            internal uint SyncRootIdentityLength;
            internal long FileId;
            internal long FileSize;
            internal IntPtr FileIdentity;
            internal uint FileIdentityLength;
            internal IntPtr NormalizedPath;
            internal long TransferKey;
            internal byte PriorityHint;
            internal IntPtr CorrelationVector;
            internal IntPtr ProcessInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct CallbackFetchData
        {
            internal uint Flags;
            internal long RequiredFileOffset;
            internal long RequiredLength;
            internal long OptionalFileOffset;
            internal long OptionalLength;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct CallbackParametersUnion
        {
            [FieldOffset(0)] internal CallbackFetchData FetchData;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct CallbackParameters
        {
            internal uint ParamSize;
            private CallbackParametersUnion _parameters;
            internal readonly CallbackFetchData FetchData => _parameters.FetchData;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct OperationInfo
        {
            internal uint StructSize;
            internal int Type;
            internal long ConnectionKey;
            internal long TransferKey;
            internal IntPtr CorrelationVector;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct OperationTransferData
        {
            internal uint Flags;
            internal int CompletionStatus;
            internal IntPtr Buffer;
            internal long Offset;
            internal long Length;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct OperationParametersUnion
        {
            [FieldOffset(0)] internal OperationTransferData TransferData;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct OperationParameters
        {
            internal uint ParamSize;
            private OperationParametersUnion _parameters;
            internal OperationTransferData TransferData
            {
                readonly get => _parameters.TransferData;
                set => _parameters.TransferData = value;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PlaceholderStandardInfo
        {
            internal long OnDiskDataSize;
            internal long ValidatedDataSize;
            internal long ModifiedDataSize;
            internal long PropertiesSize;
            internal int PinState;
            internal int InSyncState;
            internal long FileId;
            internal long SyncRootFileId;
            internal uint FileIdentityLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileAttributeTagInfo
        {
            internal uint FileAttributes;
            internal uint ReparseTag;
        }

        [DllImport("CldApi.dll", ExactSpelling = true)]
        internal static extern int CfGetPlatformInfo(out PlatformInfo platformVersion);

        [DllImport("CldApi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        internal static extern int CfGetSyncRootInfoByPath(string path, int informationClass,
            [Out] byte[] buffer, int bufferLength, out int returnedLength);

        [DllImport("CldApi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        internal static extern int CfRegisterSyncRoot(string syncRootPath, ref SyncRegistration registration,
            ref SyncPolicies policies, uint registerFlags);

        [DllImport("CldApi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        internal static extern int CfUnregisterSyncRoot(string syncRootPath);

        [DllImport("CldApi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        internal static extern int CfConnectSyncRoot(string syncRootPath, IntPtr callbackTable,
            IntPtr callbackContext, uint connectFlags, out long connectionKey);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        internal static extern int CfDisconnectSyncRoot(long connectionKey);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        internal static extern int CfUpdateSyncProviderStatus(long connectionKey, uint providerStatus);

        [DllImport("CldApi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        internal static extern int CfCreatePlaceholders(string baseDirectoryPath,
            ref PlaceholderCreateInfo placeholderArray, uint placeholderCount, uint createFlags,
            out uint entriesProcessed);

        [DllImport("CldApi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        internal static extern int CfOpenFileWithOplock(string filePath, uint flags, out IntPtr protectedHandle);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        internal static extern IntPtr CfGetWin32HandleFromProtectedHandle(IntPtr protectedHandle);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        internal static extern void CfCloseHandle(IntPtr fileHandle);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        internal static extern int CfGetPlaceholderInfo(IntPtr fileHandle, int infoClass, IntPtr infoBuffer,
            int infoBufferLength, out int returnedLength);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        internal static extern uint CfGetPlaceholderStateFromAttributeTag(uint fileAttributes,
            uint reparseTag);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandleEx(IntPtr fileHandle, int fileInformationClass,
            out FileAttributeTagInfo fileInformation, uint bufferSize);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true,
            CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess,
            FileShare shareMode, IntPtr securityAttributes, uint creationDisposition,
            uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        internal static extern int CfConvertToPlaceholder(IntPtr fileHandle, IntPtr fileIdentity,
            uint fileIdentityLength, uint convertFlags, IntPtr convertUsn, IntPtr overlapped);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        internal static extern int CfUpdatePlaceholder(IntPtr fileHandle, ref FsMetadata metadata,
            IntPtr fileIdentity, uint fileIdentityLength, IntPtr dehydrateRangeArray,
            uint dehydrateRangeCount, uint updateFlags, IntPtr updateUsn, IntPtr overlapped);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        internal static extern int CfHydratePlaceholder(IntPtr fileHandle, long startingOffset, long length,
            uint hydrateFlags, IntPtr overlapped);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        internal static extern int CfDehydratePlaceholder(IntPtr fileHandle, long startingOffset, long length,
            uint dehydrateFlags, IntPtr overlapped);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        internal static extern int CfSetPinState(IntPtr fileHandle, int pinState, uint pinFlags,
            IntPtr overlapped);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        internal static extern int CfExecute(ref OperationInfo operationInfo,
            ref OperationParameters operationParameters);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        internal static extern int CfReportProviderProgress(long connectionKey, long transferKey,
            long providerProgressTotal, long providerProgressCompleted);
    }
}
