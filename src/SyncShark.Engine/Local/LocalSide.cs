using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>The local side, file-system effects and interrupted-removal recovery. PairSession binds this pair into one
/// pending port, so the side never reaches Storage and the port alone decides durable-write authorization. The port's
/// ReadMember returns the persisted folder-removal member an allocation belongs to, or null when it has none;
/// recoveryRoot is the app-data staging root, never a pair destination. reportInventoryFailure receives a walk's
/// relative folder, observed entry, classified cause and whether the whole root failed; PairSession binds it from the
/// operation's authority.</summary>
public sealed class LocalSide(PairConfig pair, PathScope scope, EngineSettings settings, PendingNotes pending,
    string recoveryRoot, Action<string, Entry?, TextKey, bool> reportInventoryFailure) : ISide, ILocalRecovery, IIdentitySide
{
    private readonly PairConfig _pair = pair;
    private readonly EngineSettings _settings = settings;
    private readonly PathScope _scope = scope;
    private readonly Action<string, Entry?, TextKey, bool> _reportInventoryFailure = reportInventoryFailure;
    private readonly LocalListing _listing = new(pair, scope, reportInventoryFailure);
    private readonly PendingNotes _notes = pending;
    private readonly string _recoveryRoot = recoveryRoot;
    private readonly HashSet<string> _activeStages = new(PathNames.Comparer);
    private PendingResolver? _resolver;

    /// <summary>Opens the checked version for reading; the caller disposes the stream.</summary>
    public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct) =>
        Task.FromResult<Stream>(new LocalReadStream(OpenRead(_pair.LocalRoot, _listing, entry, ct), entry.Path));

    /// <summary>Bounds open hash streams to one per processor core across all local sides.</summary>
    public static int HashSlots { get; } = Environment.ProcessorCount;
    private static readonly SemaphoreSlim HashGate = new(HashSlots, HashSlots);

    /// <summary>Hashes a checked local file using the Dropbox algorithm and the shared hash gate.</summary>
    internal static async Task<string> ContentHashAsync(string root, LocalListing listing, Entry entry, CancellationToken ct)
    {
        await HashGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var stream = OpenRead(root, listing, entry, ct);
            try { return await DropboxContentHash.ComputeAsync(stream, ct).ConfigureAwait(false); }
            catch (Exception error) when (LocalErrors.IsFileFailure(error))
            {
                throw Map(error, entry.Path, TextKey.ReadLocalFailed);
            }
        }
        finally { HashGate.Release(); }
    }

    private static FileStream OpenRead(string root, LocalListing listing, Entry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var full = LocalPaths.Full(root, entry.Path);
        FileStream stream;
        try { stream = OpenReadStream(full); }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw Map(error, entry.Path, TextKey.ReadLocalFailed, full);
        }
        try
        {
            Recheck(listing, entry);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Writes and validates a durable stage without changing the destination.</summary>
    public async Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var full = LocalPaths.Full(_pair.LocalRoot, path);
        if (!Directory.Exists(Path.GetDirectoryName(full)))
            throw new ItemFailedException(path, TextKey.ParentMissing, null);

        var stage = StageNames.For(full);
        SyncLocalFileAuthority? held = null;
        lock (_activeStages) _activeStages.Add(stage);
        try
        {
            // The created handle stays open, share-none, until the staged object is disposed: no other writer can
            // reach the bytes before publication forces this handle's close record (MoveStage).
            held = SyncLocalFileAuthority.CreateNewUnprotectedFile(stage, writeThrough: false, isAsync: true);
            var identity = held.ReadIdentity();
            File.SetAttributes(stage, File.GetAttributes(stage) | FileAttributes.Hidden);
            var hash = await WriteVerifiedAsync(content, held.Stream, source, path, ct).ConfigureAwait(false);
            held.Stream.Flush(flushToDisk: true);
            // Set on the writing handle: a time set through another handle is replaced when this one closes.
            File.SetLastWriteTimeUtc(held.Handle, source.Modified.UtcDateTime);
            var proof = ProofOf(identity, stage, held.Stream.Length, hash);
            return new LocalStaged(path, source, proof, () => PendingOwns(stage), ReleaseStage) { Held = held };
        }
        catch (Exception error)
        {
            DiscardPartialStage(stage, held);
            if (LocalErrors.IsFileFailure(error))
                throw Map(error, path, TextKey.DownloadIncomplete);
            throw;
        }
    }

    // The one read/hash/write pass over the incoming bytes: no second read or hash of the finished file.
    private static async Task<string> WriteVerifiedAsync(Stream content, Stream output, Entry source, string path,
        CancellationToken ct)
    {
        using var hasher = new ContentBlockHasher();
        var buffer = ArrayPool<byte>.Shared.Rent(SyncContentIdentity.DefaultReadBufferBytes);
        try
        {
            var chunk = buffer.AsMemory(0, SyncContentIdentity.DefaultReadBufferBytes);
            int read;
            while ((read = await content.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                hasher.Append(buffer.AsSpan(0, read));
                await output.WriteAsync(chunk[..read], ct).ConfigureAwait(false);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        ct.ThrowIfCancellationRequested();
        if (output.Length != source.Size) throw new ItemFailedException(path, TextKey.DownloadIncomplete, null);
        var hash = hasher.Finish();
        if (!SameContent(hash, source.ContentHash))
            throw new ItemFailedException(path, TextKey.DownloadHashMismatch, null);
        return hash;
    }

    // A computed content hash is the recorded one.
    internal static bool SameContent(string? computed, string? expected) => SyncContentIdentity.IdentitiesMatch(
        SyncContentIdentity.DropboxBlockSha256, computed, SyncContentIdentity.DropboxBlockSha256, expected);

    // The held bytes are the recorded bytes: the same length first, so another object is never hashed, then the
    // complete content hash.
    internal static async Task<bool> SameContent(SyncLocalFileAuthority held, long expectedSize, string? expectedHash,
        CancellationToken ct) =>
        held.Stream.Length == expectedSize &&
        SameContent(await HashAsync(held.Stream, ct).ConfigureAwait(false), expectedHash);

    // The held object is the recorded object with the recorded content: identity, then length (so another object is
    // never hashed), then the complete hash through the shared gate.
    internal static async Task<bool> HoldsAsync(SyncLocalFileAuthority held, StageProof proof, CancellationToken ct) =>
        Names(held.ReadIdentity(), proof) &&
        held.Stream.Length == proof.Size &&
        SameContent(await HashAsync(held.Stream, ct).ConfigureAwait(false), proof.ContentHash);

    internal static StageProof ProofOf(PilotDirectoryIdentity identity, string path, long size, string hash) =>
        new(path, identity.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture),
            LocalIdentity.FileIdText(identity.FileId), size, hash);

    // The held native object is the recorded one: same volume serial and file ID.
    internal static bool SameObject(PilotDirectoryIdentity identity, string volumeId, string fileId) =>
        string.Equals(identity.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture), volumeId,
            StringComparison.Ordinal) &&
        string.Equals(LocalIdentity.FileIdText(identity.FileId), fileId, StringComparison.Ordinal);

    // A stage proof's identity, for disposal decisions that compare no content.
    internal static bool Names(PilotDirectoryIdentity identity, StageProof proof) =>
        SameObject(identity, proof.VolumeId, proof.FileId);

    // A partial stage has no complete proof, so it never becomes a LocalStaged.
    private void DiscardPartialStage(string stage, SyncLocalFileAuthority? created)
    {
        try
        {
            if (created is null) return;
            // The created handle itself, not the name, authorizes the cleanup.
            using (created) created.DeleteOnClose();
        }
        catch (Exception error) when (LocalErrors.IsManagedIoFailure(error))
        {
            // Best-effort cleanup must not mask the transfer failure; the next walk retries it.
        }
        finally
        {
            ReleaseStage(stage);
        }
    }

    private bool PendingOwns(string stage) => _notes.Read() switch
    {
        NoPending => false,
        CurrentPending current => current.Value.Stage is { } owned && PathNames.Comparer.Equals(owned.Path, stage),
        LegacyPending legacy => PathNames.Comparer.Equals(legacy.StagePath, stage),
        // An unrecognized owner keeps the bytes.
        _ => true
    };

    /// <summary>Publishes a stage only if the target still matches, retaining pending ownership across replacement.</summary>
    public async Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (expected is { IsFolder: true })
        {
            if (selection is not null) throw new ArgumentException(null, nameof(selection));
            var removed = await DeleteAsync(expected, null, ct).ConfigureAwait(false);
            if (removed.Outcome != RemovalOutcome.Removed) return removed;
            expected = null;
        }
        var local = (LocalStaged)staged;
        var path = local.Path;
        var full = LocalPaths.Full(_pair.LocalRoot, path);
        var current = _listing.One(path);
        if (expected is not null)
        {
            if (!Matches(current, expected)) throw new ChangedUnderneathException(path);
            return await ReplaceAsync(local, full, RequireSelection(expected, selection), ct).ConfigureAwait(false);
        }
        if (selection is not null) throw new ArgumentException(null, nameof(selection));
        if (current != null) throw new PathOccupiedException(path);
        var (final, boundary) = MoveStage(local, full);
        return new RemovalResult(RemovalOutcome.Published, null, final, null) { LocalPublished = boundary };
    }

    private async Task<RemovalResult> ReplaceAsync(LocalStaged local, string full, RemovalSelection selection,
        CancellationToken ct)
    {
        var path = local.Path;
        VerifyStage(local, path);
        await VerifySelectedAsync(selection.Proof, ct).ConfigureAwait(false);
        var armed = Arm(PendingKind.LocalReplace, path, selection, local.Source, local.Proof);
        local.State = LocalStageState.PendingOwned;
        var (removed, result) = await RemoveAsync(armed, full, ct).ConfigureAwait(false);
        if (result.Outcome == RemovalOutcome.NoEffect) local.State = LocalStageState.Staged;
        if (result.Outcome != RemovalOutcome.Removed) return result;

        var publishing = Advance(removed, removed with { Phase = PendingPhase.PublishArmed });
        Entry final;
        JournalPosition? boundary;
        try
        {
            (final, boundary) = MoveStage(local, full);
        }
        catch (ItemFailedException failed)
        {
            // The removal stands and the stage stays with the note; only its recovery may finish publication.
            Advance(publishing, publishing with { Cause = failed.Cause });
            return new RemovalResult(RemovalOutcome.Unknown, publishing.OperationId, null, failed.Cause);
        }
        Advance(publishing, publishing with { Phase = PendingPhase.Published });
        return new RemovalResult(RemovalOutcome.Published, publishing.OperationId, final, null)
        {
            LocalPublished = boundary
        };
    }

    // Identity and size only, on the held handle: the bytes were verified in the one staging pass.
    private static void VerifyStage(LocalStaged local, string path)
    {
        try
        {
            var held = Held(local);
            if (!Names(held.ReadIdentity(), local.Proof) || held.Stream.Length != local.Proof.Size)
                throw new ChangedUnderneathException(path);
        }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw Map(error, path, TextKey.PublishFailed);
        }
    }

    // Renames through the held stage handle (never replacing a name) and forces its close record, so the returned
    // boundary is the last journal record of this download and every other writer's record is later.
    private (Entry Final, JournalPosition? Boundary) MoveStage(LocalStaged local, string full)
    {
        JournalPosition? boundary;
        try
        {
            var held = Held(local);
            PreparePublication(local.StagePath);
            NtfsUsnJournal.TryReadCheckpoint(_pair.LocalRoot, out var before);
            held.RenameToFullPath(full);
            local.State = LocalStageState.Published;
            boundary = PublishedBoundary(held, before);
        }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw Map(error, local.Path, TextKey.PublishFailed);
        }
        // Every later write, our own or another's, is after the boundary, so the published file is released now.
        try { local.Held!.Dispose(); }
        catch (Exception error) when (LocalErrors.IsManagedIoFailure(error))
        {
            // Closing cannot undo the publication; the staged object's disposal closes the handle again.
        }
        return PublishedEntry(local.Path, local.Proof, _listing.One(local.Path), boundary);
    }

    internal static (Entry Final, JournalPosition? Boundary) PublishedEntry(string path, StageProof proof,
        Entry? final, JournalPosition? boundary)
    {
        if (final is null || final.Id != proof.FileId || final.Size != proof.Size)
            throw new ItemFailedException(path, TextKey.PublishFailed, null);
        return (final, boundary);
    }

    // Only the staging pass creates a held stage; a recovery stage is never published here.
    private static SyncLocalFileAuthority Held(LocalStaged local) =>
        local.Held ?? throw new ArgumentException(null, nameof(local));

    // The forced close record's USN in the one journal read before and after it, or null whenever that cannot be
    // proved (no journal, a failed ioctl, or a journal replaced in between): null keeps the ordinary dirty read.
    private JournalPosition? PublishedBoundary(SyncLocalFileAuthority held, NtfsUsnCheckpoint? before)
    {
        if (before is null ||
            !NtfsUsnJournal.TryReadCheckpoint(_pair.LocalRoot, out var after) || after!.JournalId != before.JournalId ||
            (ulong)after.NextUsn <= (ulong)before.NextUsn)
            return null;
        var usn = (ulong)after.NextUsn - 1;
        _ = held;
        return new JournalPosition(after.JournalId, usn);
    }

    private static RemovalSelection RequireSelection(Entry expected, RemovalSelection? selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Proof.Expected != expected) throw new ChangedUnderneathException(expected.Path);
        return selection;
    }

    // Stale bytes refuse before any note exists. LocalBin rechecks them on the handle it holds through the shell.
    private async Task VerifySelectedAsync(RemovalProof proof, CancellationToken ct)
    {
        var hash = await ContentHashAsync(proof.Expected, ct).ConfigureAwait(false);
        if (!SameContent(hash, proof.ContentHash)) throw new ChangedUnderneathException(proof.Expected.Path);
    }

    private PendingEffect Arm(PendingKind kind, string path, RemovalSelection selection, Entry? source,
        StageProof? stage)
    {
        var armed = new PendingEffect(PendingEncodingVersion, ResourceNames.NewId(), 0, kind, PendingPhase.Armed, path,
            selection.Proof, selection.Saved, source, stage, null, null,
            _settings.WhenRecycleBinUnavailable == RecycleFallback.DeletePermanently, null);
        try { _notes.Arm(armed); }
        catch (InvalidOperationException error)
        {
            throw new ItemFailedException(path, TextKey.FailEarlierReplace, error);
        }
        return armed;
    }

    private PendingEffect Advance(PendingEffect current, PendingEffect change)
    {
        var next = change with { Generation = current.Generation + 1 };
        _notes.Advance(current, next);
        return next;
    }

    // Records one classified effect under the armed note. A failed write leaves the earlier phase as the authority.
    private (PendingEffect Effect, RemovalResult Result) Record(PendingEffect current, RemovalResult result,
        string? binLocator, StageProof? victim)
    {
        var phase = result.Outcome switch
        {
            RemovalOutcome.Removed => PendingPhase.Removed,
            RemovalOutcome.Retained => PendingPhase.Retained,
            RemovalOutcome.Unknown => PendingPhase.Unknown,
            _ => current.Phase
        };
        var next = Advance(current, current with
        {
            Phase = phase, BinLocator = binLocator ?? current.BinLocator, Victim = victim, Cause = result.Cause
        });
        return (next, result with { OperationId = next.OperationId });
    }

    private static RemovalResult NoEffect(TextKey? cause) => new(RemovalOutcome.NoEffect, null, null, cause);

    // LocalBin throws only before it dispatches, and names a full path; the note's result carries the cause instead.
    private async Task<(PendingEffect Effect, RemovalResult Result)> RemoveAsync(PendingEffect armed, string full,
        CancellationToken ct)
    {
        RemovalResult sent;
        string? binLocator = null;
        StageProof? victim = null;
        try { (sent, binLocator, victim) = await LocalBin.SendAsync(full, armed.Losing, ct).ConfigureAwait(false); }
        catch (ItemFailedException failed) { sent = NoEffect(failed.Cause); }
        catch (ChangedUnderneathException) { sent = NoEffect(TextKey.FailChanged); }
        catch (OperationCanceledException) { sent = NoEffect(null); }

        // Current policy is rechecked so a revoked fallback wins over the permission frozen on the note.
        if (sent.Outcome != RemovalOutcome.NoEffect || sent.Cause != TextKey.RecycleUnavailable ||
            !armed.PermanentAllowed || _settings.WhenRecycleBinUnavailable != RecycleFallback.DeletePermanently)
            return Record(armed, sent, binLocator, victim);

        var permanent = Advance(armed, armed with { Phase = PendingPhase.PermanentArmed, Cause = sent.Cause });
        RemovalResult disposed;
        try { disposed = await LocalBin.DeletePermanentlyAsync(full, armed, ct).ConfigureAwait(false); }
        catch (ItemFailedException failed) { disposed = NoEffect(failed.Cause); }
        catch (ChangedUnderneathException) { disposed = NoEffect(TextKey.FailChanged); }
        catch (OperationCanceledException) { disposed = NoEffect(null); }
        return Record(permanent, disposed, null, null);
    }

    internal static void PreparePublication(string stagePath)
    {
        var attributes = File.GetAttributes(stagePath);
        if ((attributes & FileAttributes.Hidden) != 0)
            File.SetAttributes(stagePath, attributes & ~FileAttributes.Hidden);
    }

    /// <summary>Returns or creates a folder, rejecting an occupying file.</summary>
    public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var current = _listing.One(path);
        if (current != null)
        {
            if (!current.IsFolder) throw new PathOccupiedException(path);
            return Task.FromResult(current);
        }
        try { Directory.CreateDirectory(LocalPaths.Full(_pair.LocalRoot, path)); }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw Map(error, path, TextKey.PublishFailed);
        }
        return Task.FromResult(_listing.One(path) ?? throw new ChangedUnderneathException(path));
    }

    /// <summary>Rechecks and recycles the expected item under a durable note, using the configured fallback for a file.
    /// A folder carries no file proof, so it has no note and no permanent fallback.</summary>
    public async Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Recheck(_listing, expected);
        var full = LocalPaths.Full(_pair.LocalRoot, expected.Path);
        if (expected.IsFolder) return await RecycleFolderAsync(expected.Path, full, ct).ConfigureAwait(false);
        var selected = RequireSelection(expected, selection);
        await VerifySelectedAsync(selected.Proof, ct).ConfigureAwait(false);
        var armed = Arm(PendingKind.LocalDelete, expected.Path, selected, null, null);
        var (_, result) = await RemoveAsync(armed, full, ct).ConfigureAwait(false);
        return result;
    }

    private static async Task<RemovalResult> RecycleFolderAsync(string path, string full, CancellationToken ct)
    {
        try
        {
            var (result, _, _) = await LocalBin.SendAsync(full, null, ct).ConfigureAwait(false);
            return result;
        }
        catch (ItemFailedException failed)
        {
            throw new ItemFailedException(path, failed.Cause, failed.InnerException);
        }
        catch (ChangedUnderneathException)
        {
            throw new ChangedUnderneathException(path);
        }
    }

    /// <summary>Rechecks and moves an item to a free path, allowing case-only renames.</summary>
    public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var source = Recheck(_listing, expected);
        if (source.Path == newPath) return Task.FromResult(source);
        // A parent alias cannot rename the parent; only a different leaf spelling needs a case move.
        if (PathNames.Same(source.Path, newPath) && PathNames.Name(source.Path) == PathNames.Name(newPath))
            return Task.FromResult(source with { Path = newPath });
        var caseOnly = PathNames.CaseOnly(expected.Path, newPath);
        if (!caseOnly && _listing.One(newPath) != null) throw new PathOccupiedException(newPath);

        var full = LocalPaths.Full(_pair.LocalRoot, expected.Path);
        var newFull = LocalPaths.Full(_pair.LocalRoot, newPath);
        try
        {
            if (expected.IsFolder) Directory.Move(full, newFull);
            else File.Move(full, newFull);
        }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw Map(error, expected.Path, TextKey.MoveFailed, full);
        }
        var current = _listing.One(newPath);
        if (current == null || !string.Equals(current.Path, newPath, StringComparison.Ordinal))
            throw new ItemFailedException(newPath, TextKey.MoveFailed, null);
        return Task.FromResult(current);
    }

    // Local has no folder job port: it refuses before any I/O rather than answering with a false no-op.
    public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
        IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct) =>
        throw new NotSupportedException();

    // Local has no folder job port: it refuses before any I/O rather than answering with a false no-op.
    public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
        RequestAttempt attempt, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>Stage files currently owned by this side; RemoveStaleStages never deletes them.</summary>
    public IReadOnlyCollection<string> ActiveStages
    {
        get
        {
            lock (_activeStages) return _activeStages.ToArray();
        }
    }

    // OS fact: a FileStream buffer of 1 byte means no managed buffering (section 4.4).
    private const int UnbufferedSize = 1;

    // The first and current pending encoding; Storage alone interprets it.
    private const int PendingEncodingVersion = 1;

    // OS facts: Win32 ERROR_FILE_NOT_FOUND and ERROR_PATH_NOT_FOUND.
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    private static FileStream OpenReadStream(string full) => new(full, FileMode.Open, FileAccess.Read, FileShare.Read,
        UnbufferedSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static Entry Recheck(LocalListing listing, Entry expected)
    {
        var current = listing.One(expected.Path);
        if (!Matches(current, expected)) throw new ChangedUnderneathException(expected.Path);
        return current!;
    }

    private static bool Matches(Entry? current, Entry expected) => current != null && current.Skip == null
        && current.IsFolder == expected.IsFolder && current.Id == expected.Id && current.Size == expected.Size
        && current.Modified == expected.Modified;

    private static Exception Map(Exception error, string path, TextKey cause, string? source = null)
    {
        if (source != null && error is FileNotFoundException or DirectoryNotFoundException)
        {
            try { File.GetAttributes(source); }
            catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
            {
                return new ChangedUnderneathException(path);
            }
            catch (Exception inaccessible) when (LocalErrors.IsFileFailure(inaccessible))
            {
                // An inaccessible source is not evidence that it vanished.
            }
        }
        return new ItemFailedException(path, LocalErrors.Cause(error, cause), error);
    }

    private void ReleaseStage(string stage)
    {
        lock (_activeStages) _activeStages.Remove(stage);
    }

    public Side Side => Side.Local;
    public IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct) =>
        _listing.Walk(under, options, ct);

    /// <summary>Returns the entry at a path, or null when absent.</summary>
    public Task<Entry?> ReadOneAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_listing.One(path));
    }

    /// <summary>Returns where this file ID currently lives, or null when its absence is proven.</summary>
    public Task<IdentityLocation?> ReadByIdAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = _pair.Roots?.Local ??
            throw new EngineRequestException(TextKey.RootUnbound, (TextArg.Pair, _pair.PairId));
        // Entry.Id is the signed decimal form of the native ID, so a text that is not one cannot name an absence.
        if (!long.TryParse(id, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var fileId))
            throw new ItemFailedException(id, TextKey.ReadLocalFailed, null);
        string? full;
        try { full = NtfsIdentityPaths.ResolvePathById(_pair.LocalRoot, root, unchecked((ulong)fileId), ct); }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw Map(error, id, TextKey.ReadLocalFailed);
        }
        if (full is null) return Task.FromResult<IdentityLocation?>(null);
        var relative = PathNames.RelativeUnderLocalRoot(_pair.LocalRoot, full);
        var entry = (relative is null ? ObserveOutside(full) : _listing.One(relative)) ??
            throw new ChangedUnderneathException(relative ?? full);
        return Task.FromResult<IdentityLocation?>(new IdentityLocation(
            root.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture), full, entry));
    }

    // An outside location stays native: the listing is bound to its real parent only to reuse the one-name read.
    private Entry? ObserveOutside(string full)
    {
        var parent = Path.GetDirectoryName(full) ?? throw new ItemFailedException(full, TextKey.ReadLocalFailed, null);
        var observed = new LocalListing(_pair with { LocalRoot = parent }, _scope, _reportInventoryFailure)
            .One(Path.GetFileName(full));
        return observed is null ? null : observed with { Path = full };
    }

    /// <summary>Returns the Dropbox-algorithm content hash of a checked file.</summary>
    public Task<string> ContentHashAsync(Entry entry, CancellationToken ct) =>
        ContentHashAsync(_pair.LocalRoot, _listing, entry, ct);

    /// <summary>Checks the pending slot before staging; arming still checks again against races.</summary>
    public bool CanArmReplace() => _notes.Read() is NoPending;

    /// <summary>Builds removal proof; a missing hash reads a file with the same id, size and time.</summary>
    public async Task<RemovalProof> ProveRemovalAsync(Entry expected, string? hash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        hash ??= await ContentHashAsync(expected, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(hash)) throw new ChangedUnderneathException(expected.Path);
        var serial = Drive().Serial;
        if (serial is null) throw new ChangedUnderneathException(expected.Path);
        return new RemovalProof(expected, serial.Value.ToString(CultureInfo.InvariantCulture), hash);
    }

    /// <summary>Applies the choice to the pair's pending note through its one resolver.</summary>
    public Task<RemovalResult> ResolvePendingAsync(PendingRead pending, RecoveryChoice choice, CancellationToken ct) =>
        (_resolver ??= new PendingResolver(_pair, _listing, _notes.Advance, _notes.Read))
            .ResolveAsync(pending, choice, ct);

    /// <summary>Plans a recovery stage's exact locator under the recovery root, with no file-system effect.</summary>
    public StageAllocation PlanRecoveryStage(string operationId, long ordinal)
    {
        // The stage-name owner supplies the fresh reserved name; that name is the allocation's opaque ID.
        var path = StageNames.For(LocalPaths.Full(_recoveryRoot, operationId));
        return new StageAllocation(operationId, ordinal, Path.GetFileName(path), path);
    }

    /// <summary>Creates only the persisted allocation's exact path and stages the selected bytes in it.</summary>
    public async Task<IRecoveryStage> StageRecoveryAsync(StageAllocation allocation, Entry selected,
        Stream revisionContent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var member = _notes.ReadMember(allocation);
        if (member is not { Phase: FolderRemovalItem.RecoveryPhase.AllocateArmed } ||
            member.StageAllocation != allocation)
            throw new ChangedUnderneathException(selected.Path);
        await using var space = await ReserveRecoverySpaceAsync(allocation, selected, ct).ConfigureAwait(false);
        SyncLocalFileAuthority held;
        try { held = SyncLocalFileAuthority.CreateNewUnprotectedFile(allocation.Path); }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw Map(error, selected.Path, TextKey.DownloadIncomplete);
        }
        await using (held)
        {
            var stageArmed = await ArmHeldStageAsync(held, member, allocation).ConfigureAwait(false);
            try
            {
                var hash = await WriteVerifiedAsync(revisionContent, held.Stream, selected, selected.Path, ct)
                    .ConfigureAwait(false);
                held.Stream.Flush(flushToDisk: true);
                var proof = stageArmed.Stage! with { Size = held.Stream.Length, ContentHash = hash };
                AdvanceMember(stageArmed, stageArmed with
                {
                    Phase = FolderRemovalItem.RecoveryPhase.Staged, Stage = proof
                });
                // Only the exact armed discard removes a recovery stage; disposal never deletes one by name.
                return new LocalStaged(selected.Path, selected, proof, static () => true, ReleaseStage);
            }
            catch (Exception error) when (LocalErrors.IsFileFailure(error))
            {
                throw Map(error, selected.Path, TextKey.DownloadIncomplete);
            }
        }
    }

    // This output is on the app-data volume, which the destination's own reservation does not cover.
    private static async Task<IAsyncDisposable> ReserveRecoverySpaceAsync(StageAllocation allocation, Entry selected,
        CancellationToken ct)
    {
        try
        {
            return await DownloadSpace.Shared.ReserveAsync(Path.GetDirectoryName(allocation.Path)!,
                Path.GetFileName(allocation.Path), selected.Size, ct).ConfigureAwait(false);
        }
        catch (ItemFailedException failed)
        {
            throw new ItemFailedException(selected.Path, failed.Cause, failed.InnerException);
        }
    }

    // Identity is durable before any content. Until then the held handle alone proves the empty file is this
    // allocation's, so it can be removed and the allocation retired; a failed retirement leaves the conservative row.
    private async Task<FolderRemovalItem> ArmHeldStageAsync(SyncLocalFileAuthority held, FolderRemovalItem member,
        StageAllocation allocation)
    {
        try
        {
            var identity = ProofOf(held.ReadIdentity(), allocation.Path, 0, string.Empty);
            return AdvanceMember(member, member with
            {
                Phase = FolderRemovalItem.RecoveryPhase.StageArmed, Stage = identity
            });
        }
        catch
        {
            held.DeleteOnClose();
            await held.DisposeAsync().ConfigureAwait(false);
            AdvanceMember(member, member with
            {
                Phase = FolderRemovalItem.RecoveryPhase.RecoverySelected, StageAllocation = null
            });
            throw;
        }
    }

    private FolderRemovalItem AdvanceMember(FolderRemovalItem current, FolderRemovalItem change)
    {
        var next = change with { Generation = current.Generation + 1 };
        _notes.AdvanceMember(current, next);
        return next;
    }

    /// <summary>Observes an allocation's exact path without following links or changing bytes.</summary>
    public Task<ILocalRecovery.AllocationOccupancy> InspectRecoveryAllocationAsync(StageAllocation allocation,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ILocalRecovery.AllocationOccupancy occupancy;
        try
        {
            // The one-name directory query opens neither the child nor anything a link names.
            occupancy = WindowsDirectoryListing.Named(Path.GetDirectoryName(allocation.Path)!,
                    Path.GetFileName(allocation.Path)) is null
                ? ILocalRecovery.AllocationOccupancy.Absent
                : ILocalRecovery.AllocationOccupancy.PresentOwnershipUnknown;
        }
        catch (Win32Exception error) when (error.NativeErrorCode is ErrorFileNotFound or ErrorPathNotFound)
        {
            occupancy = ILocalRecovery.AllocationOccupancy.Absent;
        }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            occupancy = ILocalRecovery.AllocationOccupancy.Unreadable;
        }
        return Task.FromResult(occupancy);
    }

    /// <summary>Opens the exact recorded object after verifying it; the caller disposes the held seekable stream.</summary>
    public async Task<Stream> OpenRecoveryStageAsync(StageProof proof, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SyncLocalFileAuthority held;
        try { held = SyncLocalFileAuthority.OpenExistingReadGuard(proof.Path); }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw Map(error, proof.Path, TextKey.ReadLocalFailed);
        }
        try
        {
            if (!await HoldsAsync(held, proof, ct).ConfigureAwait(false))
                throw new ChangedUnderneathException(proof.Path);
            held.Stream.Position = 0;
            return held.Stream;
        }
        catch (Exception error)
        {
            await held.DisposeAsync().ConfigureAwait(false);
            if (LocalErrors.IsFileFailure(error)) throw Map(error, proof.Path, TextKey.ReadLocalFailed);
            throw;
        }
    }

    /// <summary>Hashes a held stream through the one hash gate, so hashing stays bounded across all local sides.</summary>
    internal static async Task<string> HashAsync(Stream stream, CancellationToken ct)
    {
        await HashGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await DropboxContentHash.ComputeAsync(stream, ct).ConfigureAwait(false); }
        finally { HashGate.Release(); }
    }

    /// <summary>Deletes only the exact recorded object through its held file.</summary>
    public async Task DiscardRecoveryStageAsync(StageProof proof, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SyncLocalFileAuthority held;
        try { held = SyncLocalFileAuthority.OpenExistingExclusive(proof.Path); }
        catch (IOException error) when (error.InnerException is Win32Exception
            { NativeErrorCode: ErrorFileNotFound or ErrorPathNotFound })
        {
            // Only an armed disposal reaches here, so absence means that disposal already happened.
            return;
        }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw Map(error, proof.Path, TextKey.StageCleanupFailed);
        }
        await using (held)
        {
            try
            {
                if (!Names(held.ReadIdentity(), proof)) throw new ChangedUnderneathException(proof.Path);
                held.DeleteOnClose();
            }
            catch (Exception error) when (LocalErrors.IsFileFailure(error))
            {
                throw Map(error, proof.Path, TextKey.StageCleanupFailed);
            }
        }
    }

    /// <summary>Creates one unique page scratch file under scratchRoot: create-new, read/write, FileShare.None and
    /// delete-on-close from the opening call, so no bytes outlive the single consumer that disposes the stream. It is
    /// never flushed durably, because a page is replayed only within its own attempt.</summary>
    internal static ValueTask<Stream> CreatePageSpoolAsync(string scratchRoot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            Directory.CreateDirectory(scratchRoot);
            using var owned = PilotDirectoryLease.OpenProductDirectory(scratchRoot);
            return new ValueTask<Stream>(new FileStream(Path.Combine(owned.Path, ResourceNames.NewId()),
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    Options = FileOptions.DeleteOnClose | FileOptions.Asynchronous | FileOptions.SequentialScan
                }));
        }
        catch (Exception error) when (LocalErrors.IsFileFailure(error) ||
            error is ArgumentException or InvalidDataException)
        {
            throw Map(error, scratchRoot, TextKey.DownloadIncomplete);
        }
    }

    /// <summary>Removes safe discovered stages without owners and returns operational cleanup failures.</summary>
    public IReadOnlyList<(string Path, Exception Error)> RemoveStaleStages(IReadOnlySet<string> pendingStageNames)
    {
        List<(string Path, Exception Error)>? failures = null;
        foreach (var stage in _listing.StaleStages)
        {
            if (pendingStageNames.Contains(Path.GetFileName(stage))) continue;
            lock (_activeStages)
            {
                if (_activeStages.Contains(stage)) continue;
            }
            try
            {
                var attributes = File.GetAttributes(stage);
                if ((attributes & FileAttributes.Directory) != 0 || PathRules.IsUnsafeAttributes(attributes)) continue;
                File.Delete(stage);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                // Another cleanup or publication already removed this candidate.
            }
            catch (Exception error) when (LocalErrors.IsFileFailure(error))
            {
                (failures ??= []).Add((stage, error));
            }
        }
        return failures is null ? [] : failures;
    }

    private const int DriveLetterLength = 1;

    /// <summary>Reads the configured root's current identity; storage this build refuses throws rather than returning one.</summary>
    public DirectoryIdentity ReadRootIdentity()
    {
        PilotPath.RequireSupportedRootStorage(_pair.LocalRoot);
        return WindowsDirectoryListing.ReadIdentity(_pair.LocalRoot);
    }

    /// <summary>Returns checked volume evidence, retaining the serial when only the journal is unavailable.</summary>
    public DriveEvidence Drive()
    {
        var full = Path.GetFullPath(_pair.LocalRoot);
        var root = Path.GetPathRoot(full);
        PilotDirectoryLease lease;
        try
        {
            lease = root is { Length: > DriveLetterLength } &&
                root[DriveLetterLength] == Path.VolumeSeparatorChar &&
                PathNames.Comparer.Equals(full, root)
                ? PilotDirectoryLease.OpenVolumeRootForAllocation(full)
                : PilotDirectoryLease.Open(full);
        }
        catch (Exception error) when (LocalErrors.IsFileFailure(error) ||
            error is ArgumentException or InvalidDataException)
        {
            return new(null, null, null);
        }
        using (lease)
        {
            var serial = lease.Identity.VolumeSerialNumber;
            if (NtfsUsnJournal.TryReadCheckpoint(lease.Path, out var checkpoint))
                return new(serial, checkpoint!.JournalId, checkpoint.NextUsn);
            return new(serial, null, null);
        }
    }
}
