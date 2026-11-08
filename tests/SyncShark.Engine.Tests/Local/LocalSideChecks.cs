using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks for LocalSide (`--engine-checks local`), written by LocalSide's writer.</summary>
public static class LocalSideChecks
{
    // The encoding discriminator of a current pending note and the shipped engine-settings.json log buffer.
    private const int NoteVersion = 1;
    private const int LogBufferCharacters = 262144;
    // OS facts for the directory held against enumeration: FILE_LIST_DIRECTORY, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS.
    private const uint FileListDirectory = 0x1;
    private const uint OpenExisting = 3;
    private const uint FileReadAttributes = 0x80;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    public static void Run()
    {
        CheckErrorFamilies();
        CheckPublishedEntry();
        CheckRootStorageAdmission();
        RunAsync().GetAwaiter().GetResult();
    }

    // The one root-storage admission accepts NTFS and refuses every non-NTFS volume (fixed ReFS included)
    // through the existing format refusal. Runs for every ready local drive on the host.
    private static void CheckRootStorageAdmission()
    {
        var checkedAny = false;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType == DriveType.Network) continue;
            checkedAny = true;
            var bounded = Path.Combine(drive.RootDirectory.FullName, "sync-shark-root-admission-check");
            try
            {
                PilotPath.RequireSupportedRootStorage(bounded);
                Check.Require(string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase),
                    "A non-NTFS volume passes the root-storage admission: " + drive.Name);
            }
            catch (UnsupportedRemovableRootFormatException)
            {
                Check.Require(!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase),
                    "An NTFS volume is refused by the root-storage admission: " + drive.Name);
            }
        }
        Check.Require(checkedAny, "The host exposes no ready local drive to check root admission.");
    }

    private static void CheckPublishedEntry()
    {
        var final = Source("published", [1]) with { Id = "stage-id" };
        var proof = new StageProof("stage", "volume", final.Id, final.Size, final.ContentHash);
        var boundary = new JournalPosition(7, 500);
        Check.Require(LocalSide.PublishedEntry(final.Path, proof, final, boundary) == (final, boundary),
            "A matching post-release entry keeps its publication boundary.");
        foreach (var observed in new Entry?[] { null, final with { Id = "foreign-id" }, final with { Size = 2 } })
        {
            (Entry Final, JournalPosition? Boundary)? published = null;
            try
            {
                published = LocalSide.PublishedEntry(final.Path, proof, observed, boundary);
                throw new InvalidOperationException("Substitution must refuse publication proof.");
            }
            catch (ItemFailedException failure)
            {
                Check.Require(failure.Cause == TextKey.PublishFailed && failure.Path == final.Path &&
                    published is null, "A missing or substituted post-release entry returns no publication proof.");
            }
        }
    }

    private static void CheckErrorFamilies()
    {
        Check.Require(LocalErrors.IsFileFailure(new IOException()), nameof(LocalErrors.IsFileFailure));
        Check.Require(LocalErrors.IsFileFailure(new FileNotFoundException()), nameof(LocalErrors.IsFileFailure));
        Check.Require(LocalErrors.IsFileFailure(new UnauthorizedAccessException()), nameof(LocalErrors.IsFileFailure));
        Check.Require(LocalErrors.IsFileFailure(new Win32Exception()), nameof(LocalErrors.IsFileFailure));
        Check.Require(!LocalErrors.IsFileFailure(new OperationCanceledException()), nameof(OperationCanceledException));
        Check.Require(!LocalErrors.IsFileFailure(new InvalidOperationException()), nameof(InvalidOperationException));
        Check.Require(LocalErrors.IsManagedIoFailure(new IOException()), nameof(LocalErrors.IsManagedIoFailure));
        Check.Require(LocalErrors.IsManagedIoFailure(new UnauthorizedAccessException()), nameof(LocalErrors.IsManagedIoFailure));
        Check.Require(!LocalErrors.IsManagedIoFailure(new Win32Exception()), nameof(LocalErrors.IsManagedIoFailure));
        Check.Require(!LocalErrors.IsManagedIoFailure(new OperationCanceledException()), nameof(OperationCanceledException));
        // Win32 ERROR_HANDLE_DISK_FULL and ERROR_DISK_FULL, including HRESULT_FROM_WIN32 forms.
        foreach (var code in new[] { 39, 112 })
        {
            foreach (var error in new Exception[]
            {
                new Win32Exception(code), new IOException("native failure", unchecked((int)0x80070000) | code),
                new IOException("native wrapper", new Win32Exception(code))
            })
                Check.Require(LocalErrors.Cause(error, TextKey.PublishFailed) == TextKey.FailLocalNoSpace,
                    "Native disk-full representations select the no-space cause.");
        }
        // Win32 sharing and lock violations, their HRESULT forms and the Shell's exact per-item sharing results.
        foreach (var error in new Exception[]
        {
            new Win32Exception(32), new Win32Exception(33),
            new IOException("native wrapper", new Win32Exception(32)),
            new IOException("native wrapper", new Win32Exception(33)),
            new IOException("native failure", unchecked((int)0x80070020)),
            new IOException("native failure", unchecked((int)0x80070021)),
            new COMException(string.Empty, unchecked((int)0x80270027)),
            new COMException(string.Empty, unchecked((int)0x80270028))
        })
            Check.Require(LocalErrors.Cause(error, TextKey.ReadLocalFailed) == TextKey.FailLocalInUse,
                "Every definite sharing representation selects the in-use cause.");
        Check.Require(LocalErrors.Cause(new COMException(string.Empty, unchecked((int)0x80270021)),
            TextKey.ReadLocalFailed) == TextKey.FailLocalInUseOrAccessDenied,
            "The Shell source-access-denied result stays ambiguous, not a definite lock.");
        foreach (var error in new Exception[]
        {
            new IOException("disk full"), new IOException("other facility", unchecked((int)0x80130070)),
            new Win32Exception(5), new IOException("access denied", unchecked((int)0x80070005)),
            new IOException("native wrapper", new Win32Exception(5)),
            new COMException(string.Empty, unchecked((int)0x80270022)),
            new COMException(string.Empty, unchecked((int)0x80004004)),
            new COMException(string.Empty, unchecked((int)0x80004005)),
            new FileNotFoundException(), new UnauthorizedAccessException(),
            new InvalidOperationException(), new OperationCanceledException()
        })
            foreach (var fallback in new[] { TextKey.DownloadIncomplete, TextKey.ReadLocalFailed, TextKey.PublishFailed })
                Check.Require(LocalErrors.Cause(error, fallback) == fallback,
                    "Unrelated native codes, messages, facilities and exception types retain the operation cause.");
    }

    private static async Task RunAsync()
    {
        var root = Directory.CreateTempSubdirectory(nameof(LocalSideChecks)).FullName;
        var previousBin = LocalBin.SendOverride;
        try
        {
            var settings = Settings(RecycleFallback.Fail);
            var pair = new PairConfig("pair", "Pair A", root, "", "", [], [],
                FirstRunPolicy.NewestWins, true, null);
            var slot = new PendingSlot();
            var side = NewSide(pair, settings, slot);
            LocalBin.SendOverride = DeleteTarget;

            Check.Require(side.Side == Side.Local, "The adapter identifies the local side.");
            await CheckDelegationAsync(side, slot, root);
            await CheckStagesAsync(side, root);
            await CheckSameReadAsync(side, slot, root);
            await CheckSourceFailuresAsync(side, root);
            await CheckLockedReadsAsync(side, root);
            await CheckPendingStageNamesAsync(side, slot, root);
            await CheckRecoveryAsync(side, slot, root);
            await CheckFailedStageCleanupAsync(side, root,
                new IOException("disk full", unchecked((int)0x80070070)), TextKey.FailLocalNoSpace);
            await CheckFailedPublicationAsync(side, slot, root);
            await CheckReplacementAsync(pair, settings);
            await CheckFolderReplacementAsync(pair, settings);
            await CheckRemovalProofAsync(side, root);
            await CheckRefusalsAsync(pair, settings);
            await CheckBinOutcomesAsync(pair);
            await CheckArmFailureAsync(pair, settings);
            await CheckPersistenceFailureAsync(pair, settings);
            await CheckIdentityAsync(pair, settings);
            CheckIdResolution(pair, root);
            await CheckInventoryFailureAsync(pair, settings);
            await CheckPageSpoolAsync(root);
            await CheckRecoveryStagesAsync(pair, settings);
            CheckDrive(pair, settings);
        }
        finally
        {
            LocalBin.SendOverride = previousBin;
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CheckDelegationAsync(LocalSide side, PendingSlot slot, string root)
    {
        var folder = await side.EnsureFolderAsync("folder", CancellationToken.None);
        Check.Require(folder.IsFolder, "EnsureFolder delegates to the local writer.");
        Check.Require(await side.MoveAsync(folder, folder.Path, CancellationToken.None) == folder,
            "Moving an identity-checked folder to its existing spelling is a no-op.");
        var child = await side.EnsureFolderAsync("folder/Child", CancellationToken.None);
        var aliased = child with { Path = "FOLDER/Child" };
        Check.Require(await side.MoveAsync(aliased, child.Path, CancellationToken.None) == child,
            "An ancestor-only spelling alias is an identity-checked folder no-op.");
        var recasedChild = await side.MoveAsync(aliased, "folder/CHILD", CancellationToken.None);
        Check.Require(recasedChild.Path == "folder/CHILD" && recasedChild.Id == child.Id,
            "An ancestor alias does not suppress a genuine leaf case rename.");
        await RequireFailureAsync<ChangedUnderneathException>(() =>
            side.MoveAsync(aliased with { Id = "stale-id" }, child.Path, CancellationToken.None));
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[DropboxContentHash.BlockSize + 1] })
        {
            var path = PathNames.Child(folder.Path, Guid.NewGuid().ToString("N"));
            var source = Source(path, bytes);
            using var input = new MemoryStream(bytes);
            await using var staged = await side.StageAsync(path, input, source, CancellationToken.None);
            Check.Require(LocalWriterChecks.StageIsAsync((LocalStaged)staged),
                "Stage writes use an overlapped stream while retaining exclusive custody.");
            var proof = ((LocalStaged)staged).Proof;
            var stageEntry = await side.ReadOneAsync(PathNames.Child(folder.Path, Path.GetFileName(proof.Path)),
                CancellationToken.None);
            Check.Require(proof == new StageProof(((LocalStaged)staged).StagePath, Volume(root), stageEntry!.Id,
                bytes.Length, source.ContentHash), "A stage carries its exact held identity, size and same-read hash.");
            var published = await side.PublishAsync(staged, null, null, CancellationToken.None);
            Check.Require(published.Outcome == RemovalOutcome.Published && published.OperationId is null &&
                published.Cause is null && slot.Current is NoPending && slot.History.Count == 0,
                "Free-target publication returns Published with no pending operation.");
            var entry = published.Final!;
            Check.Require(entry.Id == proof.FileId, "Publication moves the stage's own file.");
            Check.Require((await side.ReadOneAsync(path, CancellationToken.None))?.Id == entry.Id,
                "ReadOne returns the published entry.");
            Check.Require(await side.ContentHashAsync(entry, CancellationToken.None) == DropboxContentHash.Compute(bytes),
                "ContentHash uses the Dropbox algorithm for empty and multi-block files.");
            await using (var opened = await side.OpenReadAsync(entry, null, CancellationToken.None))
            {
                Check.Require(opened is LocalReadStream, "OpenRead returns the stream that names a later read failure.");
                using var copy = new MemoryStream();
                await opened.CopyToAsync(copy);
                Check.Require(copy.ToArray().SequenceEqual(bytes), "OpenRead delegates and returns the exact content.");
                await RequireFailureAsync<IOException>(() =>
                {
                    using var writer = File.Open(Path.Combine(root, path), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    return Task.CompletedTask;
                });
            }
            // Move also proves that both hashing and OpenRead released their handles.
            var moved = await side.MoveAsync(entry, path + "-moved", CancellationToken.None);
            var recasedPath = path + "-MOVED";
            moved = await side.MoveAsync(moved, recasedPath, CancellationToken.None);
            Check.Require(string.Equals(moved.Path, recasedPath, StringComparison.Ordinal),
                "Move permits a case-only rename instead of treating its own file as occupied.");
            Check.Require(await side.MoveAsync(moved, moved.Path, CancellationToken.None) == moved,
                "Moving an identity-checked file to its existing spelling is a no-op.");
            await RequireFailureAsync<ChangedUnderneathException>(() =>
                side.MoveAsync(moved with { Id = "stale-id" }, moved.Path, CancellationToken.None));
            await RequireFailureAsync<PathOccupiedException>(() =>
                side.MoveAsync(moved, folder.Path, CancellationToken.None));
            var listed = new List<Listed>();
            await foreach (var item in side.ReadAllAsync(folder.Path, new ListOptions(false, true), CancellationToken.None))
                listed.Add(item);
            Check.Require(listed.Any(item => item.Entry?.Id == moved.Id), "ReadAll forwards its subtree and options.");
            var selection = await SelectionAsync(side, root, moved);
            var removed = await side.DeleteAsync(moved, selection, CancellationToken.None);
            Check.Require(removed.Outcome == RemovalOutcome.Removed && removed.OperationId == slot.Note?.OperationId &&
                slot.Note is { Kind: PendingKind.LocalDelete, Phase: PendingPhase.Removed } note &&
                note.Losing == selection.Proof && note.Saved == selection.Saved && note.Path == moved.Path &&
                slot.History[0].Phase == PendingPhase.Armed,
                "Delete arms the selected proof before the bin seam and leaves the note for exact settlement.");
            Check.Require(await side.ReadOneAsync(moved.Path, CancellationToken.None) == null, "Delete forwards to the bin seam.");
            slot.Settle();
            slot.History.Clear();
        }
        Check.Require(await side.ReadOneAsync("absent/child", CancellationToken.None) == null,
            "A missing parent returns null.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await RequireFailureAsync<OperationCanceledException>(() => side.ReadOneAsync(folder.Path, cancelled.Token));
    }

    private static async Task CheckStagesAsync(LocalSide side, string root)
    {
        var pendingNames = new HashSet<string>(StringComparer.Ordinal);
        var stale = StageNames.For(Path.Combine(root, "abandoned"));
        var ordinary = Path.Combine(root, "ordinary.stage");
        await File.WriteAllBytesAsync(stale, []);
        await File.WriteAllBytesAsync(ordinary, []);
        var source = Source("active", [1]);
        using var input = new MemoryStream([1]);
        await using var active = await side.StageAsync(source.Path, input, source, CancellationToken.None);
        var activePath = ((LocalStaged)active).StagePath;
        var listed = new List<string>();
        await foreach (var item in side.ReadAllAsync("", new ListOptions(true, false), CancellationToken.None))
            listed.Add(item.Path);
        Check.Require(!listed.Contains(Path.GetFileName(stale)) && !listed.Contains(Path.GetFileName(activePath)) &&
            listed.Contains(Path.GetFileName(ordinary)), "The listing hides engine stages but retains ordinary suffix files.");
        Check.Require(side.ActiveStages.Contains(activePath), "A prepared stage retains active ownership.");
        using (var locked = File.Open(stale, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            var failures = side.RemoveStaleStages(pendingNames);
            Check.Require(failures.Count == 1 && failures[0].Path == stale && failures[0].Error is IOException,
                "Cleanup returns the exact locked artifact and its operational error once.");
            Check.Require(File.Exists(stale), "Cleanup leaves a locked stage for the next run");
        }
        File.SetAttributes(stale, FileAttributes.ReadOnly);
        try
        {
            var failures = side.RemoveStaleStages(pendingNames);
            Check.Require(failures.Count == 1 && failures[0].Path == stale &&
                failures[0].Error is UnauthorizedAccessException,
                "Cleanup returns the read-only artifact rather than silently swallowing its error.");
            Check.Require(File.Exists(stale), "Cleanup leaves a read-only stage for the next run");
        }
        finally { File.SetAttributes(stale, FileAttributes.Normal); }
        await WalkAsync(side);
        Check.Require(side.RemoveStaleStages(pendingNames).Count == 0, "The next opportunity removes an unlocked stage.");
        Check.Require(side.RemoveStaleStages(pendingNames).Count == 0, "Repeated cleanup has no failures.");
        Check.Require(!File.Exists(stale) && File.Exists(activePath) && File.Exists(ordinary),
            "Cleanup deletes stale stages, is repeatable, and preserves active stages and ordinary files.");
        await File.WriteAllBytesAsync(stale, []);
        await WalkAsync(side);
        File.SetAttributes(stale, FileAttributes.Offline);
        try
        {
            Check.Require(side.RemoveStaleStages(pendingNames).Count == 0 && File.Exists(stale),
                "Cleanup rechecks a discovered stage and preserves it when it becomes offline.");
        }
        finally { File.SetAttributes(stale, FileAttributes.Normal); }
        File.Delete(stale);
        Directory.CreateDirectory(stale);
        var child = Path.Combine(stale, "child");
        await File.WriteAllBytesAsync(child, [1]);
        Check.Require(side.RemoveStaleStages(pendingNames).Count == 0 &&
            (await File.ReadAllBytesAsync(child)).SequenceEqual(new byte[] { 1 }),
            "Cleanup rechecks a discovered name and never removes a directory that replaced its stage.");
    }

    private static async Task CheckRecoveryAsync(LocalSide side, PendingSlot slot, string root)
    {
        var cases = new[]
        {
            (Phase: PendingPhase.PublishArmed, Stage: true, Moved: false, Foreign: false, Published: true),
            (Phase: PendingPhase.PublishArmed, Stage: true, Moved: false, Foreign: true, Published: false),
            (Phase: PendingPhase.PublishArmed, Stage: false, Moved: true, Foreign: false, Published: true),
            (Phase: PendingPhase.PublishArmed, Stage: false, Moved: false, Foreign: true, Published: false),
            (Phase: PendingPhase.Armed, Stage: true, Moved: false, Foreign: false, Published: false),
            (Phase: PendingPhase.Unknown, Stage: true, Moved: false, Foreign: false, Published: false)
        };
        foreach (var item in cases)
        {
            var source = Source(Guid.NewGuid().ToString("N"), [1]);
            var final = Path.Combine(root, source.Path);
            var stage = StageNames.For(final);
            var proof = await StageFileAsync(side, root, stage, [1]);
            var note = Note(root, source, item.Phase, proof);
            if (item.Moved) File.Move(stage, final);
            else if (!item.Stage) File.Delete(stage);
            if (item.Foreign) await File.WriteAllBytesAsync(final, [0]);
            slot.Current = new CurrentPending(note);
            Check.Require(!side.CanArmReplace(), "An owned pending slot refuses a new replacement.");
            var result = await side.ResolvePendingAsync(slot.Current, RecoveryChoice.Check, CancellationToken.None);
            Check.Require((result.Outcome == RemovalOutcome.Published) == item.Published &&
                File.Exists(stage) == (item.Stage && !item.Published) && result.OperationId == note.OperationId,
                "ResolvePending delegates each recovery row, publishing only from durable removal evidence.");
            if (item.Published)
                Check.Require(result.Final == await side.ReadOneAsync(source.Path, CancellationToken.None),
                    "Recovery returns the observed published final entry.");
            else if (item.Foreign)
                Check.Require((await File.ReadAllBytesAsync(final)).SequenceEqual(new byte[] { 0 }),
                    "Recovery does not replace an unrelated final file.");
            else
                Check.Require(!File.Exists(final), "Check never publishes without durable removal evidence.");
            slot.Settle();
            Check.Require(side.CanArmReplace(), "Settling the pending slot permits a replacement.");
            File.Delete(stage);
        }

        var blocked = Source("missing/child", [1]);
        var orphan = StageNames.For(Path.Combine(root, "pending"));
        slot.Current = new CurrentPending(Note(root, blocked, PendingPhase.PublishArmed,
            await StageFileAsync(side, root, orphan, [1])));
        await WalkAsync(side);
        Check.Require((await TryResolveAsync(side, slot.Current))?.Outcome != RemovalOutcome.Published,
            "A missing parent is never a publication.");
        Check.Require(side.RemoveStaleStages(new HashSet<string>(StringComparer.Ordinal)
            { Path.GetFileName(orphan) }).Count == 0, "Failed recovery's saved note protects its stage.");
        Check.Require(File.Exists(orphan), "Failed recovery retains pending-stage ownership during cleanup.");
        slot.Settle();

        var legacyPath = Path.Combine(root, "legacy");
        await File.WriteAllBytesAsync(legacyPath, [0]);
        var history = slot.History.Count;
        foreach (var read in new PendingRead[]
        {
            new LegacyPending("{}", "token", "legacy", orphan, Source("legacy", [1])), new NoPending()
        })
        {
            slot.Current = read;
            var observed = await side.ResolvePendingAsync(read, RecoveryChoice.Check, CancellationToken.None);
            Check.Require(observed.Outcome is not (RemovalOutcome.Published or RemovalOutcome.Removed) &&
                slot.History.Count == history && File.Exists(orphan) &&
                (await File.ReadAllBytesAsync(legacyPath)).SequenceEqual(new byte[] { 0 }),
                "A legacy or empty slot is only observed and never publishes, recycles or deletes.");
        }
        File.Delete(orphan);
        File.Delete(legacyPath);
    }

    private static async Task CheckPendingStageNamesAsync(LocalSide side, PendingSlot slot, string root)
    {
        var source = Source("pending-alias", [1]);
        var stages = new[]
        {
            StageNames.For(Path.Combine(root, source.Path)),
            StageNames.For(Path.Combine(root, "other-pair")),
            StageNames.For(Path.Combine(root, "unowned"))
        };
        var proofs = new List<StageProof>();
        foreach (var stage in stages) proofs.Add(await StageFileAsync(side, root, stage, [1]));
        var notes = new[]
        {
            Note(root, source, PendingPhase.PublishArmed,
                proofs[0] with { Path = Path.Combine(root.ToUpperInvariant(), Path.GetFileName(stages[0])) }),
            Note(root, source with { Path = "other-pair" }, PendingPhase.PublishArmed,
                proofs[1] with { Path = Path.Combine(root, "alias", Path.GetFileName(stages[1])) })
        };
        var pendingNames = notes.Select(note => Path.GetFileName(note.Stage!.Path)).ToHashSet(StringComparer.Ordinal);
        await WalkAsync(side);
        Check.Require(side.RemoveStaleStages(pendingNames).Count == 0 && File.Exists(stages[0]) &&
            File.Exists(stages[1]) && !File.Exists(stages[2]),
            "All supplied pending names protect stages regardless of parent case or alias spelling.");
        File.SetAttributes(stages[0],
            File.GetAttributes(stages[0]) | FileAttributes.Hidden | FileAttributes.NotContentIndexed);
        var attributes = File.GetAttributes(stages[0]);
        slot.Current = new CurrentPending(notes[0]);
        var recovered = await side.ResolvePendingAsync(slot.Current, RecoveryChoice.Check, CancellationToken.None);
        Check.Require(recovered.Final?.Path == source.Path && !File.Exists(stages[0]) &&
            (await File.ReadAllBytesAsync(Path.Combine(root, source.Path))).SequenceEqual(new byte[] { 1 }) &&
            File.GetAttributes(Path.Combine(root, source.Path)) == (attributes & ~FileAttributes.Hidden),
            "Recovery through a differently cased parent publishes exact bytes and clears only Hidden.");
        slot.Settle();
    }

    private static async Task CheckFailedStageCleanupAsync(LocalSide side, string root, Exception primary, TextKey cause)
    {
        var source = Source("failed-copy", [1]);
        string? stagePath = null;
        using var input = new ProbeStream([1], _ =>
        {
            stagePath = side.ActiveStages.Single();
            File.SetAttributes(stagePath, File.GetAttributes(stagePath) | FileAttributes.ReadOnly);
            return primary;
        });
        try
        {
            var error = await RequireFailureAsync<ItemFailedException>(() =>
                side.StageAsync(source.Path, input, source, CancellationToken.None));
            Check.Require(error.Cause == cause && ReferenceEquals(error.InnerException, primary),
                "A disposal failure cannot replace the primary transfer error.");
            Check.Require(stagePath is not null && File.Exists(stagePath) && side.ActiveStages.Count == 0 &&
                !File.Exists(Path.Combine(root, source.Path)),
                "Failed cleanup releases active ownership without publishing the failed transfer.");
            await WalkAsync(side);
            var failures = side.RemoveStaleStages(new HashSet<string>(StringComparer.Ordinal));
            Check.Require(failures.Count == 1 && failures[0].Path == stagePath &&
                failures[0].Error is UnauthorizedAccessException,
                "A failed best-effort disposal is rediscovered and reported by ordinary cleanup.");
        }
        finally
        {
            if (stagePath is not null && File.Exists(stagePath)) File.SetAttributes(stagePath, FileAttributes.Normal);
        }
        await WalkAsync(side);
        Check.Require(side.RemoveStaleStages(new HashSet<string>(StringComparer.Ordinal)).Count == 0 &&
            !File.Exists(stagePath), "The next writable opportunity clears the failed stage without a separate queue.");
        // shortcut: CreateNew collisions lack deterministic coverage; add it if an ID/open seam becomes available.
    }

    // One pass over a nonseekable source decides length and hash, so a corrupt download never reaches the target.
    private static async Task CheckSameReadAsync(LocalSide side, PendingSlot slot, string root)
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        const string path = "same-read";
        var full = Path.Combine(root, path);
        await File.WriteAllBytesAsync(full, [9]);
        var source = Source(path, bytes);
        var cases = new (byte[] Read, Entry Expected, TextKey Cause)[]
        {
            (new byte[] { 1, 2, 3, 5 }, source, TextKey.DownloadHashMismatch),
            (new byte[] { 0, 2, 3, 4 }, source, TextKey.DownloadHashMismatch),
            (bytes, source with { ContentHash = "" }, TextKey.DownloadHashMismatch),
            (bytes, source with { ContentHash = "not-a-hash" }, TextKey.DownloadHashMismatch),
            (new byte[] { 1, 2, 3 }, source, TextKey.DownloadIncomplete),
            (new byte[] { 1, 2, 3, 4, 5 }, source, TextKey.DownloadIncomplete)
        };
        foreach (var item in cases)
        {
            using var input = new ProbeStream(item.Read);
            var error = await RequireFailureAsync<ItemFailedException>(() =>
                side.StageAsync(path, input, item.Expected, CancellationToken.None));
            Check.Require(error.Cause == item.Cause && error.Path == path && error.InnerException is null,
                "A failed validation keeps its exact cause, destination path and null inner error.");
            Check.Require((item.Read.Length > bytes.Length || input.Consumed == item.Read.Length) &&
                side.ActiveStages.Count == 0 && Stages(root) == 0 && slot.History.Count == 0 &&
                (await File.ReadAllBytesAsync(full)).SequenceEqual(new byte[] { 9 }),
                "A rejected transfer leaves no stage, no pending note and the existing target untouched.");
        }

        var first = true;
        using var valid = new ProbeStream(bytes, _ =>
        {
            if (!first) return null;
            first = false;
            var active = side.ActiveStages.Single();
            Check.Require((File.GetAttributes(active) & FileAttributes.Hidden) != 0, "A stage is hidden before its first byte.");
            try
            {
                File.Open(active, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete).Dispose();
                return new InvalidOperationException("The output stays exclusive during validation.");
            }
            catch (IOException) { return null; }
        });
        await using (var stage = (LocalStaged)await side.StageAsync(path, valid, source, CancellationToken.None))
        {
            Check.Require(valid.Consumed == bytes.Length && valid.CanRead &&
                stage.Proof.ContentHash == source.ContentHash && stage.Proof.Size == bytes.Length &&
                new FileInfo(stage.StagePath).LastWriteTimeUtc == source.Modified.UtcDateTime,
                "A later valid transfer reads the source once and leaves it caller-owned.");
        }
        Check.Require(Stages(root) == 0 && side.ActiveStages.Count == 0, "Disposal removes an unowned valid stage.");
        File.Delete(full);
    }

    // Source and output lifetime at the staging boundary; the space reservation around it belongs to Transfers.
    private static async Task CheckSourceFailuresAsync(LocalSide side, string root)
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        const string path = "source-failure";
        var source = Source(path, bytes);
        var programming = new InvalidOperationException();
        var transport = new IOException("source transport");
        foreach (var (primary, cause) in new (Exception, TextKey)[]
        {
            (programming, TextKey.DownloadIncomplete), (transport, TextKey.DownloadIncomplete),
            (new IOException("native wrapper", new Win32Exception(112)), TextKey.FailLocalNoSpace),
            (new IOException("native wrapper", new Win32Exception(39)), TextKey.FailLocalNoSpace),
            (new IOException("native wrapper", new Win32Exception(5)), TextKey.DownloadIncomplete)
        })
        {
            using var input = new ProbeStream(bytes, position => position > 0 ? primary : null);
            var error = await RequireFailureAsync<Exception>(() => side.StageAsync(path, input, source, CancellationToken.None));
            Check.Require(ReferenceEquals(primary, programming) ? ReferenceEquals(error, programming)
                : error is ItemFailedException failed && failed.Cause == cause &&
                    ReferenceEquals(failed.InnerException, primary),
                "A failure after a prefix keeps its original attribution.");
            Check.Require(Stages(root) == 0 && side.ActiveStages.Count == 0, "A failed prefix leaves no owned stage.");
        }
        using var cancelled = new CancellationTokenSource();
        using var stopping = new ProbeStream(bytes, position =>
        {
            if (position > 0) cancelled.Cancel();
            return null;
        });
        await RequireFailureAsync<OperationCanceledException>(() => side.StageAsync(path, stopping, source, cancelled.Token));
        using var neverRead = new ProbeStream(bytes, _ => throw new InvalidOperationException("Pre-cancel reads no source"));
        await RequireFailureAsync<OperationCanceledException>(() => side.StageAsync(path, neverRead, source, cancelled.Token));
        Check.Require(Stages(root) == 0 && side.ActiveStages.Count == 0, "Cancellation leaves no owned stage.");
        await (await StageAsync(side, source, bytes)).DisposeAsync();
        Check.Require(Stages(root) == 0, "The next valid transfer still stages after every failure.");
    }

    // Opening and later reads through the real OpenRead path; a byte-range lock can only fail after the open.
    private static async Task CheckLockedReadsAsync(LocalSide side, string root)
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        const string path = "locked-read";
        var full = Path.Combine(root, path);
        await File.WriteAllBytesAsync(full, bytes);
        var entry = (await side.ReadOneAsync(path, CancellationToken.None))!;
        void RequireInUse(ItemFailedException error) => Check.Require(
            error.Path == path && error.Cause == TextKey.FailLocalInUse && error.InnerException is not null,
            "A local read failure names the item and its in-use cause.");

        using (File.Open(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            RequireInUse(await RequireFailureAsync<ItemFailedException>(() => side.OpenReadAsync(entry, null, CancellationToken.None)));
            RequireInUse(await RequireFailureAsync<ItemFailedException>(() => side.ContentHashAsync(entry, CancellationToken.None)));
        }

        foreach (var read in new Func<Stream, Task>[]
        {
            stream => { _ = stream.Read(new byte[1], 0, 1); return Task.CompletedTask; },
            stream => { _ = stream.Read(new byte[1].AsSpan()); return Task.CompletedTask; },
            stream => stream.ReadAsync(new byte[1].AsMemory()).AsTask(),
            stream => stream.ReadAsync(new byte[1], 0, 1, CancellationToken.None),
            stream => stream.CopyToAsync(Stream.Null),
            async stream =>
            {
                using var content = new StreamContent(stream);
                await content.CopyToAsync(Stream.Null);
            }
        })
        {
            await using var opened = await side.OpenReadAsync(entry, null, CancellationToken.None);
            using var holder = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            holder.Lock(0, bytes.Length);
            RequireInUse(FindItemFailure(await RequireFailureAsync<Exception>(() => read(opened))));
            holder.Unlock(0, bytes.Length);
        }

        using (var holder = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            holder.Lock(0, bytes.Length);
            RequireInUse(await RequireFailureAsync<ItemFailedException>(() => side.ContentHashAsync(entry, CancellationToken.None)));
            holder.Unlock(0, bytes.Length);
        }
        Check.Require(await side.ContentHashAsync(entry, CancellationToken.None) == DropboxContentHash.Compute(bytes),
            "Releasing the lock lets the next read succeed.");
        File.Delete(full);
    }

    private static async Task CheckFailedPublicationAsync(LocalSide side, PendingSlot slot, string root)
    {
        var source = Source("replacement", [1]);
        var (expected, selection, finalPath) = await TargetAsync(side, root, source.Path);
        LocalBin.SendOverride = path =>
        {
            File.Delete(path);
            Directory.CreateDirectory(path);
            return true;
        };
        var staged = await StageAsync(side, source, [1]);
        RemovalResult failed;
        try { failed = await side.PublishAsync(staged, expected, selection, CancellationToken.None); }
        finally { await staged.DisposeAsync(); }
        Check.Require(failed.Outcome != RemovalOutcome.Published && failed.OperationId == slot.Note?.OperationId &&
            slot.Note?.Stage == staged.Proof, "A failed publish retains the pending note.");
        var pendingStage = slot.Note!.Stage!.Path;
        await WalkAsync(side);
        var pendingNames = new HashSet<string>(StringComparer.Ordinal) { Path.GetFileName(pendingStage) };
        Check.Require(side.RemoveStaleStages(pendingNames).Count == 0, "Pending cleanup has no failures.");
        Check.Require(File.Exists(pendingStage), "Cleanup preserves a pending stage after disposal releases its active ownership.");
        Directory.Delete(finalPath);
        using (File.Open(pendingStage, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var locked = await TryResolveAsync(side, slot.Current);
            Check.Require(locked?.Outcome != RemovalOutcome.Published &&
                (locked is null || locked.Cause == TextKey.FailLocalInUse),
                "A locked recovery reports its cause and publishes nothing.");
            Check.Require(slot.Note?.OperationId == failed.OperationId, "A locked recovery retains the pending note.");
            Check.Require(side.RemoveStaleStages(pendingNames).Count == 0, "Locked pending stages are not cleanup attempts.");
            Check.Require(File.Exists(pendingStage), "A locked recovery retains pending-stage ownership.");
        }
        var recovered = await side.ResolvePendingAsync(slot.Current, RecoveryChoice.Check, CancellationToken.None);
        Check.Require(recovered.Outcome == RemovalOutcome.Published &&
            recovered.Final == await side.ReadOneAsync(source.Path, CancellationToken.None),
            "Recovery completes the durable publication once the lock is released.");
        slot.Settle();
        LocalBin.SendOverride = DeleteTarget;
    }

    private static async Task CheckRemovalProofAsync(LocalSide side, string root)
    {
        var full = Path.Combine(root, "fresh-proof");
        await File.WriteAllBytesAsync(full, [1]);
        var expected = (await side.ReadOneAsync("fresh-proof", default))!;
        var proof = await side.ProveRemovalAsync(expected, null, default);
        Check.Require(proof.Expected == expected && proof.ContentHash == DropboxContentHash.Compute([1]),
            "A143: a file without an earlier hash gets proof of its exact observed bytes.");
        await File.WriteAllBytesAsync(full, [2, 3]);
        await RequireFailureAsync<ChangedUnderneathException>(() => side.ProveRemovalAsync(expected, null, default));
        expected = (await side.ReadOneAsync("fresh-proof", default))!;
        using var held = File.Open(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await RequireFailureAsync<ItemFailedException>(() => side.ProveRemovalAsync(expected, null, default));
        await RequireFailureAsync<DeletionEvidenceRefusedException>(() => side.ProveRemovalAsync(expected, "", default));
        Check.Require(File.Exists(full), "AJ-01: an unknown saved hash refuses without reading or removing the locked file.");
    }

    private static async Task CheckFolderReplacementAsync(PairConfig pair, EngineSettings settings)
    {
        foreach (var refused in new[] { false, true })
        {
            var slot = new PendingSlot();
            var side = NewSide(pair, settings, slot);
            var path = refused ? "folder-refused" : "folder-published";
            var folder = await side.EnsureFolderAsync(path, default);
            await using var stage = await StageAsync(side, Source(path, [1]), [1]);
            var recycles = 0;
            LocalBin.SendOverride = full =>
            {
                recycles++;
                return !refused && DeleteTarget(full);
            };
            try
            {
                var stray = new RemovalSelection(new RemovalProof(folder with { IsFolder = false },
                    Volume(pair.LocalRoot), "hash"), null);
                await RequireFailureAsync<ArgumentException>(() => side.PublishAsync(stage, folder, stray, default));
                Check.Require(recycles == 0 && Directory.Exists(Path.Combine(pair.LocalRoot, path)),
                    "A145: a stray folder selection refuses before recycling.");
                var result = await side.PublishAsync(stage, folder, null, default);
                Check.Require(recycles == 1 && slot.Current is NoPending && slot.History.Count == 0,
                    "A146: folder publication recycles once without a file note.");
                Check.Require(refused
                    ? result.Outcome != RemovalOutcome.Published && Directory.Exists(Path.Combine(pair.LocalRoot, path))
                    : result.Outcome == RemovalOutcome.Published && result.Final?.Id == stage.Proof.FileId,
                    "A146: a refused recycle retains the folder; success publishes the verified stage.");
            }
            finally { LocalBin.SendOverride = DeleteTarget; }
        }
    }

    private static async Task CheckReplacementAsync(PairConfig pair, EngineSettings settings)
    {
        var slot = new PendingSlot();
        var side = NewSide(pair, settings, slot);
        var (expected, selection, full) = await TargetAsync(side, pair.LocalRoot, "replaced");
        var stage = await StageAsync(side, Source(expected.Path, [1]), [1]);
        var result = await side.PublishAsync(stage, expected, selection, CancellationToken.None);
        await stage.DisposeAsync();
        var phases = slot.History.Select(item => item.Phase).ToList();
        var removed = phases.IndexOf(PendingPhase.Removed);
        Check.Require(result.Outcome == RemovalOutcome.Published && result.Cause is null &&
            result.OperationId == slot.Note?.OperationId && result.Final?.Id == stage.Proof.FileId &&
            slot.Note is { Kind: PendingKind.LocalReplace, Phase: PendingPhase.Published } note &&
            note.Stage == stage.Proof && note.Losing == selection.Proof && note.Saved == selection.Saved &&
            !note.PermanentAllowed && stage.State == LocalStageState.Published,
            "Replacement returns Published with its operation and leaves the note for exact settlement.");
        Check.Require(phases[0] == PendingPhase.Armed && removed > 0 &&
            phases.IndexOf(PendingPhase.PublishArmed) > removed && phases[^1] == PendingPhase.Published,
            "Replacement persists arm, verified removal and publish intent before the move.");
        Check.Require((await File.ReadAllBytesAsync(full)).SequenceEqual(new byte[] { 1 }) && !File.Exists(stage.StagePath),
            "Published bytes replace the verified target.");
    }

    private static async Task CheckRefusalsAsync(PairConfig pair, EngineSettings settings)
    {
        var slot = new PendingSlot();
        var side = NewSide(pair, settings, slot);
        var (expected, selection, full) = await TargetAsync(side, pair.LocalRoot, "refused");
        var sends = 0;
        LocalBin.SendOverride = _ => { sends++; return true; };
        var stage = await StageAsync(side, Source(expected.Path, [1]), [1]);
        var unbound = selection with { Proof = selection.Proof with { Expected = expected with { Path = "other" } } };
        foreach (var refused in new[] { null, unbound })
        {
            await RequireFailureAsync<Exception>(() => side.DeleteAsync(expected, refused, CancellationToken.None));
            await RequireFailureAsync<Exception>(() => side.PublishAsync(stage, expected, refused, CancellationToken.None));
        }
        await RequireFailureAsync<Exception>(() => side.PublishAsync(stage, null, selection, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await RequireFailureAsync<OperationCanceledException>(() => side.DeleteAsync(expected, selection, cancelled.Token));
        Check.Require(sends == 0 && slot.History.Count == 0 && (await File.ReadAllBytesAsync(full)).SequenceEqual(new byte[] { 0 }),
            "A missing or unbound selection and pre-dispatch cancellation refuse before any note or effect.");

        // An in-place edit keeps ID, size and time, so only the complete selected hash can refuse it.
        await File.WriteAllBytesAsync(full, [9]);
        File.SetLastWriteTimeUtc(full, expected.Modified.UtcDateTime);
        RemovalResult? outcome = null;
        try { outcome = await side.DeleteAsync(expected, selection, CancellationToken.None); }
        catch (ChangedUnderneathException) { }
        Check.Require(sends == 0 && outcome?.Outcome is null or RemovalOutcome.NoEffect &&
            (await File.ReadAllBytesAsync(full)).SequenceEqual(new byte[] { 9 }),
            "Stale selected bytes never reach the bin and never authorize removal.");
        slot.Settle();
        await stage.DisposeAsync();
        LocalBin.SendOverride = DeleteTarget;
    }

    // Both fallback settings against a definite refusal and against a lock: only the refusal may reach permanent removal.
    private static async Task CheckBinOutcomesAsync(PairConfig pair)
    {
        foreach (var fallback in new[] { RecycleFallback.Fail, RecycleFallback.DeletePermanently })
        foreach (var replace in new[] { false, true })
        foreach (var locked in new[] { false, true })
        {
            var slot = new PendingSlot();
            var side = NewSide(pair, Settings(fallback), slot);
            var (expected, selection, full) = await TargetAsync(side, pair.LocalRoot, $"bin-{fallback}-{replace}-{locked}");
            var sends = 0;
            LocalBin.SendOverride = path =>
            {
                sends++;
                return locked ? throw new ItemFailedException(path, TextKey.FailLocalInUse, null) : false;
            };
            await using var stage = await StageAsync(side, Source(expected.Path, [1]), [1]);
            var result = replace
                ? await side.PublishAsync(stage, expected, selection, CancellationToken.None)
                : await side.DeleteAsync(expected, selection, CancellationToken.None);
            var note = slot.Note!;
            var allowed = fallback == RecycleFallback.DeletePermanently;
            var permanent = allowed && !locked;
            Check.Require(sends == 1 && note.PermanentAllowed == allowed &&
                note.Kind == (replace ? PendingKind.LocalReplace : PendingKind.LocalDelete) &&
                slot.History.Any(item => item.Phase == PendingPhase.PermanentArmed) == permanent,
                "The frozen permission arms permanent removal only after a definite no-effect refusal.");
            if (permanent)
            {
                Check.Require(result.Outcome == (replace ? RemovalOutcome.Published : RemovalOutcome.Removed) &&
                    (replace ? (await File.ReadAllBytesAsync(full)).SequenceEqual(new byte[] { 1 }) : !File.Exists(full)),
                    "Permitted permanent removal completes the requested effect.");
                continue;
            }
            Check.Require(result.Outcome is not (RemovalOutcome.Removed or RemovalOutcome.Published) &&
                result.OperationId == note.OperationId && result.Cause == note.Cause &&
                result.Cause == (locked ? TextKey.FailLocalInUse : TextKey.RecycleUnavailable) &&
                (await File.ReadAllBytesAsync(full)).SequenceEqual(new byte[] { 0 }),
                "An armed refusal returns and persists its cause and preserves the target.");
        }
        LocalBin.SendOverride = DeleteTarget;
    }

    private static async Task CheckArmFailureAsync(PairConfig pair, EngineSettings settings)
    {
        foreach (var saved in new[] { false, true })
        {
            var slot = new PendingSlot
            {
                ArmFailure = new IOException("pending arm failure"),
                ArmFailsAfterSaving = saved
            };
            var side = NewSide(pair, settings, slot);
            var (expected, selection, full) = await TargetAsync(side, pair.LocalRoot, $"arm-{saved}");
            var sends = 0;
            LocalBin.SendOverride = _ => { sends++; return true; };
            var stage = await StageAsync(side, Source(expected.Path, [1]), [1]);
            await RequireFailureAsync<Exception>(() => side.PublishAsync(stage, expected, selection, CancellationToken.None));
            await stage.DisposeAsync();
            Check.Require(sends == 0 && File.Exists(stage.StagePath) == saved && side.ActiveStages.Count == 0 &&
                (await File.ReadAllBytesAsync(full)).SequenceEqual(new byte[] { 0 }),
                "Durable ownership, not in-memory state, decides whether disposal may delete the stage.");
            if (saved) File.Delete(stage.StagePath);
        }
        LocalBin.SendOverride = DeleteTarget;
    }

    private static async Task CheckPersistenceFailureAsync(PairConfig pair, EngineSettings settings)
    {
        var slot = new PendingSlot();
        var side = NewSide(pair, settings, slot);
        var (expected, selection, full) = await TargetAsync(side, pair.LocalRoot, "bin-and-outcome");
        var stage = await StageAsync(side, Source(expected.Path, [1]), [1]);
        slot.AdvanceFailure = new IOException("pending advance failure");
        LocalBin.SendOverride = path => throw new ItemFailedException(path, TextKey.RecycleFailed, null);
        try
        {
            await RequireFailureAsync<Exception>(() => side.PublishAsync(stage, expected, selection, CancellationToken.None));
            Check.Require(slot.Note is { Phase: PendingPhase.Armed, Generation: 0 } && slot.Note.Stage == stage.Proof,
                "A failed outcome write leaves the armed note as the authority.");
        }
        finally { await stage.DisposeAsync(); }
        Check.Require(File.Exists(stage.StagePath) && side.ActiveStages.Count == 0 &&
            (await File.ReadAllBytesAsync(stage.StagePath)).SequenceEqual(new byte[] { 1 }) &&
            (await File.ReadAllBytesAsync(full)).SequenceEqual(new byte[] { 0 }),
            "Disposal preserves pending-owned replacement bytes and the untouched target.");
        await WalkAsync(side);
        Check.Require(side.RemoveStaleStages(new HashSet<string>(StringComparer.Ordinal)
            { Path.GetFileName(stage.StagePath) }).Count == 0 && File.Exists(stage.StagePath),
            "The retained pending note protects the stage after active ownership ends.");
        slot.AdvanceFailure = null;
        var abandoned = await side.ResolvePendingAsync(slot.Current, RecoveryChoice.AbandonIncoming, CancellationToken.None);
        Check.Require(abandoned.Outcome != RemovalOutcome.Published && !File.Exists(stage.StagePath) &&
            (await File.ReadAllBytesAsync(full)).SequenceEqual(new byte[] { 0 }),
            "Abandoning discards only the owned stage and never touches the current target.");
        slot.Settle();
        LocalBin.SendOverride = DeleteTarget;
    }

    private static async Task CheckIdentityAsync(PairConfig pair, EngineSettings settings)
    {
        var slot = new PendingSlot();
        var identity = NewSide(pair, settings, slot).ReadRootIdentity();
        Check.Require(identity == NtfsIdentityPaths.ReadRootIdentity(pair.LocalRoot), "The side reads the shared root identity.");
        Check.Require(identity.FileIdHigh == 0, "The real NTFS root reader returns a zero high file-id part.");
        Check.Require(NtfsIdentityPaths.MatchesLegacyIdentity(identity,
            unchecked((uint)identity.VolumeSerialNumber), identity.FileId),
            "The real NTFS root identity matches its legacy representation.");
        OpenExclusive(pair.LocalRoot, "A successful identity read releases its root handle.").Dispose();
        var bound = pair with
        {
            Roots = new RootBinding(pair.LocalRoot, "", "", identity,
                new DropboxRootIdentity("account", "root", "home", "folder"))
        };
        var side = NewSide(bound, settings, slot);
        var (entry, _, _) = await TargetAsync(side, pair.LocalRoot, "identified");
        var located = await side.ReadByIdAsync(entry.Id, CancellationToken.None);
        Check.Require(located?.Entry.Id == entry.Id && located.Entry.Path == entry.Path &&
            located.NativePath.EndsWith(entry.Path, StringComparison.OrdinalIgnoreCase), "An ID resolves to its current path.");
        var moved = await side.MoveAsync(entry, "identified-moved", CancellationToken.None);
        located = await side.ReadByIdAsync(entry.Id, CancellationToken.None);
        Check.Require(located?.Entry.Path == moved.Path, "A moved ID resolves to its new path without enumeration.");

        var outside = Directory.CreateTempSubdirectory(nameof(LocalSideChecks)).FullName;
        try
        {
            var foreign = NewSide(pair with { LocalRoot = outside }, settings, slot);
            var (elsewhere, _, _) = await TargetAsync(foreign, outside, "elsewhere");
            located = await side.ReadByIdAsync(elsewhere.Id, CancellationToken.None);
            Check.Require(located?.NativePath.EndsWith("elsewhere", StringComparison.OrdinalIgnoreCase) == true,
                "An out-of-pair location is returned intact, never reported as absence.");
        }
        finally { Directory.Delete(outside, recursive: true); }

        File.Delete(Path.Combine(pair.LocalRoot, moved.Path));
        Check.Require(await side.ReadByIdAsync(entry.Id, CancellationToken.None) is null, "Null is proven absence.");
        await RequireFailureAsync<Exception>(() => side.ReadByIdAsync("not-a-file-id", CancellationToken.None));

        var reports = 0;
        var mismatched = bound with { Roots = bound.Roots! with { Local = identity with { FileId = identity.FileId + 1 } } };
        await RequireFailureAsync<InventoryIncompleteException>(() =>
            WalkAsync(NewSide(mismatched, settings, slot, report: (_, _, _, _) => reports++)));
        Check.Require(reports == 0, "A replaced root handle refuses the walk without an in-use report.");
        OpenExclusive(pair.LocalRoot, "A stale root walk releases its root handle.").Dispose();

        var file = Path.Combine(pair.LocalRoot, "root-file");
        await File.WriteAllBytesAsync(file, [0]);
        await WalkAsync(side);
        OpenExclusive(pair.LocalRoot, "A completed walk releases its root handle.").Dispose();
        foreach (var cancel in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            await using (var walk = side.ReadAllAsync("", new ListOptions(true, false), cancellation.Token)
                .GetAsyncEnumerator())
            {
                Check.Require(await walk.MoveNextAsync(), "The walk yields before disposal.");
                if (cancel)
                {
                    cancellation.Cancel();
                    await RequireFailureAsync<OperationCanceledException>(async () => { await walk.MoveNextAsync(); });
                }
            }
            OpenExclusive(pair.LocalRoot, "A disposed or cancelled walk releases its root handle.").Dispose();
        }
        foreach (var root in new[] { Path.Combine(pair.LocalRoot, "missing-root"), file, @"\\server\share" })
        {
            var config = pair with { LocalRoot = root };
            await RequireFailureAsync<Exception>(() =>
            {
                NewSide(config, settings, slot).ReadRootIdentity();
                return Task.CompletedTask;
            });
        }
        OpenExclusive(file, "A refused file-as-root identity read releases its handle.").Dispose();
        File.Delete(file);
        Check.Require(slot.History.Count == 0, "Root reads never arm pending state.");
    }

    // The by-id resolver's proof: live, renamed and hard-linked identities resolve; a retired identity is
    // proven absent through the observed native ERROR_INVALID_PARAMETER (87) the production call reads.
    private static void CheckIdResolution(PairConfig pair, string root)
    {
        var identity = NtfsIdentityPaths.ReadRootIdentity(root);
        var listing = new LocalListing(pair, new PathScope(pair, [pair]), (_, _, _, _) => { });
        var live = Path.Combine(root, "resolved.txt");
        using (File.Create(live)) { }
        var described = listing.One("resolved.txt") ?? throw new InvalidOperationException("The fixture file is missing.");
        var id = unchecked((ulong)long.Parse(described.Id, CultureInfo.InvariantCulture));
        Check.Require(string.Equals(Path.GetFileName(Resolve(root, identity, id)), "resolved.txt", StringComparison.Ordinal),
            "A live file identity resolves to its current path.");
        var renamed = Path.Combine(root, "renamed.txt");
        File.Move(live, renamed);
        Check.Require(string.Equals(Path.GetFileName(Resolve(root, identity, id)), "renamed.txt", StringComparison.Ordinal),
            "A renamed file identity resolves to its new path.");
        var alias = Path.Combine(root, "alias.txt");
        Check.Require(CreateHardLinkW(alias, renamed, IntPtr.Zero),
            "The fixture volume creates a hard link for the shared-identity case.");
        var linked = Resolve(root, identity, id);
        Check.Require(linked is not null &&
            (string.Equals(Path.GetFileName(linked), "renamed.txt", StringComparison.Ordinal) ||
             string.Equals(Path.GetFileName(linked), "alias.txt", StringComparison.Ordinal)),
            "A hard-linked file identity still resolves.");
        using (var held = CreateFileW(renamed, FileReadAttributes, FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero))
        {
            Check.Require(!held.IsInvalid, "The fixture keeps the identity alive without preventing unlink.");
            File.Delete(renamed);
            Check.Require(Resolve(root, identity, id) is not null, "A remaining hard link is not absence.");
            File.Delete(alias);
            Check.Require(NativeOpenByIdError(root, id) == 0,
                "An open handle keeps the unlinked identity resolvable by the native API.");
            Check.Require(Resolve(root, identity, id) is null,
                "An unlinked identity is absent even while its last handle keeps the native object alive.");
        }
        Check.Require(Resolve(root, identity, id) is null,
            "A retired identity remains absent after the final handle closes.");
    }

    private static string? Resolve(string root, DirectoryIdentity identity, ulong id) =>
        NtfsIdentityPaths.ResolvePathById(root, identity, id, CancellationToken.None);

    // Observe the native result through the production identity-opening owner.
    private static int NativeOpenByIdError(string root, ulong id)
    {
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(root))!;
        using var volume = CreateFileW(volumeRoot, FileReadAttributes, FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (volume.IsInvalid) throw new InvalidOperationException("The fixture volume could not be opened.");
        using var handle = NtfsIdentityPaths.OpenById(volume, id);
        return handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

    // The side hands the composition's callback to its own listing; the failure shapes are the listing's checks.
    private static async Task CheckInventoryFailureAsync(PairConfig pair, EngineSettings settings)
    {
        var folder = Directory.CreateDirectory(Path.Combine(pair.LocalRoot, "held")).FullName;
        var reports = new List<(string Folder, Entry? Observed, TextKey Cause, bool FullRoot)>();
        var side = NewSide(pair, settings, new PendingSlot(), report: (path, observed, cause, fullRoot) =>
            reports.Add((path, observed, cause, fullRoot)));
        var listed = new List<Listed>();
        using (var held = OpenExclusive(folder, nameof(CreateFile)))
        {
            await foreach (var item in side.ReadAllAsync("", new ListOptions(true, false), CancellationToken.None))
                listed.Add(item);
        }
        Check.Require(reports.Count == 1 && reports[0].Folder == "held" && reports[0].Observed?.Path == "held" &&
            reports[0].Cause == TextKey.FailLocalInUse && !reports[0].FullRoot &&
            listed.Any(item => item.Path == "held" && item.Entry?.Skip == TextKey.SkipUnreadable.ToString()),
            "A sharing failure reaches the composition callback once and stays protected in the listing.");
        Directory.Delete(folder);
    }

    private static async Task CheckPageSpoolAsync(string root)
    {
        var scratch = Directory.CreateDirectory(Path.Combine(root, "page-scratch")).FullName;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await RequireFailureAsync<OperationCanceledException>(() =>
            LocalSide.CreatePageSpoolAsync(scratch, cancelled.Token).AsTask());
        Check.Require(!Directory.EnumerateFileSystemEntries(scratch).Any(), "A cancelled spool creates nothing.");
        var bytes = new byte[] { 1, 2, 3 };
        await using (var first = await LocalSide.CreatePageSpoolAsync(scratch, CancellationToken.None))
        await using (var second = await LocalSide.CreatePageSpoolAsync(scratch, CancellationToken.None))
        {
            var files = Directory.GetFiles(scratch);
            Check.Require(files.Length == 2 && first.CanRead && first.CanWrite && first.CanSeek,
                "Each spool is its own read/write scratch file.");
            await first.WriteAsync(bytes);
            first.Position = 0;
            using var copy = new MemoryStream();
            await first.CopyToAsync(copy);
            Check.Require(copy.ToArray().SequenceEqual(bytes) && second.Length == 0, "A spool replays exactly its own bytes.");
            await RequireFailureAsync<IOException>(() =>
            {
                using var other = File.Open(files[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return Task.CompletedTask;
            });
        }
        Check.Require(!Directory.EnumerateFileSystemEntries(scratch).Any(), "Disposal alone deletes a spool.");
        Directory.Delete(scratch);
    }

    // Recovery-stage custody: a locator is never ownership, and only the recorded object is opened or discarded.
    private static async Task CheckRecoveryStagesAsync(PairConfig pair, EngineSettings settings)
    {
        var recoveryRoot = Directory.CreateTempSubdirectory(nameof(LocalSideChecks)).FullName;
        try
        {
            var bytes = new byte[] { 1, 2, 3, 4 };
            var selected = Source("recovered", bytes);
            var member = new MemberSlot();
            var side = NewSide(pair, settings, new PendingSlot(), recoveryRoot, member);
            var operation = Guid.NewGuid().ToString("N");
            var allocation = side.PlanRecoveryStage(operation, 1);
            var unrelated = side.PlanRecoveryStage(operation, 1);
            Check.Require(allocation.OperationId == operation && allocation.Ordinal == 1 &&
                allocation.AllocationId != unrelated.AllocationId && allocation.Path != unrelated.Path &&
                allocation.Path.StartsWith(recoveryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !Directory.EnumerateFileSystemEntries(recoveryRoot).Any(),
                "Planning returns a fresh locator under the recovery root with no file-system effect.");
            Check.Require(await side.InspectRecoveryAllocationAsync(allocation, CancellationToken.None) ==
                ILocalRecovery.AllocationOccupancy.Absent, "An unused locator is absent.");

            async Task<IRecoveryStage> StageRecoveryAsync(StageAllocation target, byte[] content)
            {
                using var input = new MemoryStream(content);
                return await side.StageRecoveryAsync(target, selected, input, CancellationToken.None);
            }

            await RequireFailureAsync<Exception>(() => StageRecoveryAsync(allocation, bytes));
            Check.Require(!File.Exists(allocation.Path), "An allocation that was not persisted creates nothing.");

            member.Current = Member(allocation, selected);
            member.AdvanceFailure = new IOException("member advance failure");
            await RequireFailureAsync<Exception>(() => StageRecoveryAsync(allocation, bytes));
            Check.Require(member.Current.Phase == FolderRemovalItem.RecoveryPhase.AllocateArmed &&
                (!File.Exists(allocation.Path) || new FileInfo(allocation.Path).Length == 0),
                "No content is written before the identity is persisted.");
            if (File.Exists(allocation.Path)) File.Delete(allocation.Path);
            member.AdvanceFailure = null;

            var stage = await StageRecoveryAsync(allocation, bytes);
            Check.Require(stage.Proof.Path == allocation.Path && stage.Proof.VolumeId == Volume(recoveryRoot) &&
                stage.Proof.Size == bytes.Length && stage.Proof.ContentHash == selected.ContentHash &&
                member.Phases.SequenceEqual(new[]
                {
                    FolderRemovalItem.RecoveryPhase.StageArmed, FolderRemovalItem.RecoveryPhase.Staged
                }) && member.Current.Stage == stage.Proof,
                "Identity is persisted before content and the complete proof after it.");
            await stage.DisposeAsync();
            Check.Require((await File.ReadAllBytesAsync(allocation.Path)).SequenceEqual(bytes) &&
                await side.InspectRecoveryAllocationAsync(allocation, CancellationToken.None) ==
                ILocalRecovery.AllocationOccupancy.PresentOwnershipUnknown,
                "Disposal retains a durably owned stage, and a locator alone never reports ownership.");

            await using (var opened = await side.OpenRecoveryStageAsync(stage.Proof, CancellationToken.None))
            {
                using var copy = new MemoryStream();
                await opened.CopyToAsync(copy);
                opened.Position = 0;
                Check.Require(opened.CanSeek && copy.ToArray().SequenceEqual(bytes), "The exact recorded stage reopens seekable.");
            }
            await RequireFailureAsync<Exception>(() => side.OpenRecoveryStageAsync(
                stage.Proof with { ContentHash = DropboxContentHash.Compute(new byte[] { 9 }) }, CancellationToken.None));
            await side.DiscardRecoveryStageAsync(stage.Proof, CancellationToken.None);
            Check.Require(!File.Exists(allocation.Path), "Discard deletes the exact recorded object.");

            // A new file at the old locator has the same bytes and a different identity.
            await File.WriteAllBytesAsync(allocation.Path, bytes);
            await RequireFailureAsync<Exception>(() => side.OpenRecoveryStageAsync(stage.Proof, CancellationToken.None));
            try { await side.DiscardRecoveryStageAsync(stage.Proof, CancellationToken.None); }
            catch (Exception error) when (error is ItemFailedException or ChangedUnderneathException or IOException) { }
            Check.Require(File.Exists(allocation.Path), "A substituted object is never removed.");

            var occupied = side.PlanRecoveryStage(operation, 2);
            await File.WriteAllBytesAsync(occupied.Path, [9]);
            member.Current = Member(occupied, selected);
            await RequireFailureAsync<Exception>(() => StageRecoveryAsync(occupied, bytes));
            Check.Require(member.Current.Stage is null &&
                (await File.ReadAllBytesAsync(occupied.Path)).SequenceEqual(new byte[] { 9 }),
                "A creation conflict never adopts the occupant.");

            var corrupt = side.PlanRecoveryStage(operation, 3);
            member.Current = Member(corrupt, selected);
            await RequireFailureAsync<Exception>(() => StageRecoveryAsync(corrupt, new byte[] { 4, 3, 2, 1 }));
            Check.Require(member.Current.Phase != FolderRemovalItem.RecoveryPhase.Staged, "Mismatched bytes never become Staged.");
        }
        finally { Directory.Delete(recoveryRoot, recursive: true); }
    }

    private static FolderRemovalItem Member(StageAllocation allocation, Entry selected) => new(allocation.OperationId,
        allocation.Ordinal, default, FolderRemovalItem.ProvenanceKind.Planned, selected.Path, selected.Path, selected, null,
        FolderVerdict.Removed, false, FolderRemovalItem.FollowUpStatus.None, true, FolderEffectKind.ConditionalFile,
        FolderRemovalItem.EffectStatus.Settled, default, null, null, null, FolderRemovalItem.RecoveryPhase.AllocateArmed,
        selected, allocation, null, null, null);

    private static void CheckDrive(PairConfig pair, EngineSettings settings)
    {
        var slot = new PendingSlot();
        using var lease = PilotDirectoryLease.Open(pair.LocalRoot);
        foreach (var root in new[] { pair.LocalRoot, Path.GetPathRoot(pair.LocalRoot)! })
        {
            var config = pair with { LocalRoot = root };
            var side = NewSide(config, settings, slot);
            NtfsUsnJournal.TryReadCheckpoint(root, out var before);
            var evidence = side.Drive();
            NtfsUsnJournal.TryReadCheckpoint(root, out var after);
            Check.Require(evidence.Serial == lease.Identity.VolumeSerialNumber,
                "Both bounded-folder and drive-letter-root reads retain the checked volume serial.");
            Check.Require((evidence.JournalId is null) == (evidence.NextUsn is null) &&
                (evidence.NextUsn is null || evidence.NextUsn >= 0),
                "Journal identity and nonnegative position are one observation, or both unavailable.");
            if (before is not null && after is not null && before.JournalId == after.JournalId &&
                before.NextUsn >= 0 && after.NextUsn >= before.NextUsn)
                Check.Require(evidence.JournalId == before.JournalId && evidence.NextUsn >= before.NextUsn &&
                    evidence.NextUsn <= after.NextUsn,
                    "Available native journal evidence is bound to the root and lies between the surrounding reads.");
        }
        // Core's existing test hook keeps the product-state refusal fixture away from real product data.
        using var productRoot = (IDisposable)typeof(SyncSharkRuntimePaths)
            .GetMethod("OverrideProductRootForTest", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { Path.Combine(pair.LocalRoot, "product-state") })!;
        var productChild = Directory.CreateDirectory(Path.Combine(SyncSharkRuntimePaths.ProductRoot, "pair")).FullName;
        foreach (var root in new[] { Path.Combine(pair.LocalRoot, "missing-drive-root"), @"\\no-such-host.invalid\share", productChild })
        {
            var config = pair with { LocalRoot = root };
            var unavailable = NewSide(config, settings, slot).Drive();
            Check.Require(unavailable == new DriveEvidence(null, null, null),
                $"A refused native root lease supplies unavailable evidence instead of a Repair fault: {root}");
        }
        Check.Require(slot.History.Count == 0, "Drive evidence never arms pending state.");
    }

    private static Entry Source(string path, byte[] bytes) => new(path, false, bytes.Length,
        DateTimeOffset.UtcNow, "source", "revision", DropboxContentHash.Compute(bytes), null);

    private static EngineSettings Settings(RecycleFallback fallback) => new("{name}", "yyyyMMdd", "local", "dropbox", ".case",
        fallback, 1, FirstRunPolicy.NewestWins, 1, "", LogBufferCharacters, ScanTestSettings.BatchRows);

    private static LocalSide NewSide(PairConfig pair, EngineSettings settings, PendingSlot slot,
        string? recoveryRoot = null, MemberSlot? member = null, Action<string, Entry?, TextKey, bool>? report = null) =>
        new(pair, new PathScope(pair, [pair]), settings,
            new PendingNotes(new SlotStore(slot, member), pair.PairId, settings.StoragePageSize, mutation: true),
            recoveryRoot ?? Path.GetTempPath(), report ?? ((_, _, _, _) => { }));

    private static bool DeleteTarget(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else File.Delete(path);
        return true;
    }

    private static string Volume(string root) => VolumeSpace.Serial(root).ToString(CultureInfo.InvariantCulture);

    private static async Task<RemovalSelection> SelectionAsync(LocalSide side, string root, Entry expected) =>
        new(new RemovalProof(expected, Volume(root), await side.ContentHashAsync(expected, CancellationToken.None)), null);

    private static async Task<(Entry Expected, RemovalSelection Selection, string Full)> TargetAsync(LocalSide side,
        string root, string path)
    {
        var full = Path.Combine(root, path);
        await File.WriteAllBytesAsync(full, [0]);
        var expected = (await side.ReadOneAsync(path, CancellationToken.None))!;
        return (expected, await SelectionAsync(side, root, expected), full);
    }

    private static async Task<LocalStaged> StageAsync(LocalSide side, Entry source, byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        return (LocalStaged)await side.StageAsync(source.Path, input, source, CancellationToken.None);
    }

    private static async Task<StageProof> StageFileAsync(LocalSide side, string root, string stage, byte[] bytes)
    {
        await File.WriteAllBytesAsync(stage, bytes);
        var entry = await side.ReadOneAsync(Path.GetFileName(stage), CancellationToken.None);
        return new StageProof(stage, Volume(root), entry!.Id, bytes.Length, DropboxContentHash.Compute(bytes));
    }

    private static PendingEffect Note(string root, Entry source, PendingPhase phase, StageProof stage) =>
        new(NoteVersion, Guid.NewGuid().ToString("N"), default, PendingKind.LocalReplace, phase, source.Path,
            new RemovalProof(source with { Id = "1" }, Volume(root), source.ContentHash), null, source, stage,
            null, null, false, null);

    private static async Task<RemovalResult?> TryResolveAsync(LocalSide side, PendingRead pending)
    {
        try { return await side.ResolvePendingAsync(pending, RecoveryChoice.Check, CancellationToken.None); }
        catch (ItemFailedException) { return null; }
    }

    private static int Stages(string root) =>
        Directory.EnumerateFiles(root).Count(file => StageNames.IsStage(Path.GetFileName(file)));

    private static ItemFailedException FindItemFailure(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is ItemFailedException failed) return failed;
        throw new InvalidOperationException(nameof(ItemFailedException), error);
    }

    private static async Task WalkAsync(LocalSide side)
    {
        await foreach (var _ in side.ReadAllAsync("", new ListOptions(true, false), CancellationToken.None)) { }
    }

    private static async Task<T> RequireFailureAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException(typeof(T).Name);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    private static SafeFileHandle OpenExclusive(string path, string message)
    {
        var handle = CreateFile(path, FileListDirectory, FileShare.None, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        try
        {
            Check.Require(!handle.IsInvalid, message);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>The pair's single durable note: conditional arm and advance as Storage applies them, with injected
    /// persistence failures. Local never clears it; the check settles it as ActionExecutor would.</summary>
    private sealed class PendingSlot
    {
        public PendingRead Current { get; set; } = new NoPending();
        public List<PendingEffect> History { get; } = [];
        public Exception? ArmFailure { get; set; }
        public bool ArmFailsAfterSaving { get; set; }
        public Exception? AdvanceFailure { get; set; }
        public PendingEffect? Note => (Current as CurrentPending)?.Value;

        public void Arm(PendingEffect value)
        {
            if (ArmFailure is { } refused && !ArmFailsAfterSaving) throw refused;
            Check.Require(Current is NoPending, "Arm refuses an occupied slot.");
            Current = new CurrentPending(value);
            History.Add(value);
            if (ArmFailure is { } lost) throw lost;
        }

        public void Advance(PendingEffect expected, PendingEffect next)
        {
            if (AdvanceFailure is { } failure) throw failure;
            Check.Require(Current == new CurrentPending(expected) && next.OperationId == expected.OperationId &&
                next.Generation == expected.Generation + 1, "Advance is the same operation's next generation.");
            Current = new CurrentPending(next);
            History.Add(next);
        }

        public void Settle() => Current = new NoPending();
    }

    /// <summary>The persisted folder-removal member a recovery allocation belongs to.</summary>
    private sealed class MemberSlot
    {
        public FolderRemovalItem? Current { get; set; }
        public List<FolderRemovalItem.RecoveryPhase> Phases { get; } = [];
        public Exception? AdvanceFailure { get; set; }

        public FolderRemovalItem? Read(StageAllocation allocation) =>
            Current?.StageAllocation == allocation ? Current : null;

        public void Advance(FolderRemovalItem expected, FolderRemovalItem next)
        {
            if (AdvanceFailure is { } failure) throw failure;
            Check.Require(Current == expected, "A member advance is conditional on the persisted member.");
            Current = next;
            Phases.Add(next.Phase);
        }
    }

    // Store-seam double: the production PendingNotes port (and its mutation gate) runs above these slots.
    private sealed class SlotStore(PendingSlot slot, MemberSlot? member) : ForwardingStateStore(null!)
    {
        public override PendingRead Pending(string pairId) => slot.Current;

        public override void ArmPending(string pairId, PendingEffect value) => slot.Arm(value);

        public override void AdvancePending(string pairId, PendingEffect expected, PendingEffect next) =>
            slot.Advance(expected, next);

        public override IEnumerable<FolderRemovalItem> FolderRemovalItems(string pairId, string operationId,
            long afterOrdinal, int pageSize) =>
            member?.Current is { } current && current.OperationId == operationId && current.Ordinal > afterOrdinal
                ? [current] : [];

        public override void SettleFolderRemovalItem(string pairId, string operationId, long ordinal,
            long expectedGeneration, FolderRemovalItem next, StateChange verifiedChange)
        {
            Check.Require(member?.Current is { } current && current.OperationId == operationId &&
                current.Ordinal == ordinal && current.Generation == expectedGeneration,
                "A member advance is conditional on the persisted member.");
            member!.Advance(member.Current!, next);
        }
    }

    /// <summary>A nonseekable source that returns short reads and can fail at any read; it can be read only once.</summary>
    private sealed class ProbeStream(byte[] bytes, Func<long, Exception?>? beforeRead = null) : Stream
    {
        // Shorter than either the I/O buffer or a hash block, so reads never align with them.
        private const int ShortRead = 3;
        private int _position;

        public long Consumed => _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (beforeRead?.Invoke(_position) is { } failure) throw failure;
            var count = Math.Min(Math.Min(buffer.Length, ShortRead), bytes.Length - _position);
            bytes.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
