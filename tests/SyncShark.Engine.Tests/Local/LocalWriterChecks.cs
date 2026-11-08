using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks for LocalSide file effects (`--engine-checks local`); the suite name remains stable.</summary>
public static class LocalWriterChecks
{
    private static readonly EngineSettings Settings = new("{name}", "yyyyMMdd", "local", "dropbox",
        ".case", RecycleFallback.Fail, 30, FirstRunPolicy.NewestWins, 1, "", 262144, ScanTestSettings.BatchRows) { JournalPageBufferBytes = ScanTestSettings.JournalPageBufferBytes, LogReadBufferBytes = ScanTestSettings.LogReadBufferBytes };
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("new content");

    // SHA-256 of no bytes: the fixed empty-content vector the block composition must also produce.
    private const string EmptyContentHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var root = Directory.CreateTempSubdirectory(nameof(LocalWriterChecks)).FullName;
        var previous = LocalBin.SendOverride;
        try
        {
            foreach (var check in new Func<string, Task>[]
            {
                StageAndCreateAsync, StageFailuresAsync, ContentMatchAsync, HashBoundariesAsync, HashMismatchAsync,
                PublishPreconditionsAsync, ReplaceAsync, EarlierPendingAsync, BinFailureAsync,
                PublishFailuresAsync, EnsureFoldersAsync, DeleteAsync, MoveAsync, OpenReadAsync,
                MappedFailuresAsync, HashCoreLimitAsync
            })
            {
                LocalBin.SendOverride = _ => throw new InvalidOperationException("Unexpected bin call");
                var folder = Path.Combine(root, Guid.NewGuid().ToString());
                Directory.CreateDirectory(folder);
                await check(folder);
            }
        }
        finally
        {
            LocalBin.SendOverride = previous;
            Directory.Delete(root, recursive: true);
        }
    }

    // #90b: opens with every share flag, so only the held stage handle's own share mode can refuse it.
    private static Task OpenShared(string path)
    {
        using var opened = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        return Task.CompletedTask;
    }

    // #90b: the held authority is Core-internal to this assembly, so its stream mode is read by reflection.
    internal static bool StageIsAsync(LocalStaged staged) =>
        ((FileStream)staged.Held!.GetType().GetProperty("Stream")!.GetValue(staged.Held)!).IsAsync;

    internal static (LocalSide Writer, LocalListing Listing) Create(string root, Notes? notes = null)
    {
        var pair = new PairConfig("pair", "Pair A", root, "", "account", [], [],
            FirstRunPolicy.NewestWins, true, null);
        var scope = new PathScope(pair, [pair]);
        var listing = new LocalListing(pair, scope, (_, _, _, _) => { });
        var note = notes ?? new Notes();
        var pending = new PendingNotes(note, "pair", Settings.StoragePageSize, mutation: true);
        return (new LocalSide(pair, scope, Settings, pending,
            Path.Combine(root, ".recovery"), (_, _, _, _) => { }), listing);
    }

    internal static Entry Source(string path, byte[]? content = null)
    {
        var bytes = content ?? Content;
        return new(path, false, bytes.Length, DateTimeOffset.UnixEpoch.AddDays(1), "source", "revision",
            DropboxContentHash.Compute(bytes), null);
    }

    internal static async Task<LocalStaged> StageAsync(LocalSide writer, string path, Entry? source = null,
        byte[]? content = null)
    {
        var bytes = content ?? Content;
        using var input = new MemoryStream(bytes);
        var stage = (LocalStaged)await writer.StageAsync(path, input, source ?? Source(path, bytes),
            CancellationToken.None);
        Check.Require(input.CanRead, "Staging must not dispose the caller's input");
        return stage;
    }

    private static RemovalSelection Select(string root, LocalListing listing, string path, byte[] selected,
        SyncedEntry? saved = null) =>
        new(new RemovalProof(One(listing, path), VolumeId(root), DropboxContentHash.Compute(selected)), saved);

    // The proof producer's invariant-decimal volume serial (LocalBin compares it ordinally).
    private static string VolumeId(string root) => VolumeSpace.Serial(root).ToString(CultureInfo.InvariantCulture);

    private static SyncedEntry Saved(string path, Entry expected, byte[] content) =>
        new(PathNames.Key(path), path, PathNames.DropboxKey(path), path, false, expected.Id, expected.Size,
            expected.Modified.UtcDateTime.Ticks, "dropbox-id", DropboxContentHash.Compute(content));

    private static FileAttributes SetStageAttributes(string stagePath)
    {
        Check.Require((File.GetAttributes(stagePath) & FileAttributes.Hidden) != 0,
            "A prepared stage is hidden before publication");
        File.SetAttributes(stagePath, File.GetAttributes(stagePath) | FileAttributes.NotContentIndexed);
        return File.GetAttributes(stagePath) & ~FileAttributes.Hidden;
    }

    private static Entry One(LocalListing listing, string path) =>
        listing.One(path) ?? throw new InvalidOperationException("Expected a local entry");

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T error)
        {
            return error;
        }
        throw new InvalidOperationException(typeof(T).Name);
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T error)
        {
            return error;
        }
        throw new InvalidOperationException(typeof(T).Name);
    }

    private static void NoStages(LocalSide writer, string root)
    {
        Check.Require(writer.ActiveStages.Count == 0, "Failed or disposed stages must release active ownership");
        Check.Require(!Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Any(file => StageNames.IsStage(Path.GetFileName(file))),
            "No failed stage may remain on disk");
    }

    // A deterministic incompressible-enough content of an exact length.
    private static byte[] Pattern(int size)
    {
        var content = new byte[size];
        for (var i = 0; i < size; i++) content[i] = (byte)(i % 251);
        return content;
    }

    // Independent test-only Dropbox composition: SHA-256 of the per-block SHA-256 digests; no production helper.
    private static string IndependentContentHash(ReadOnlySpan<byte> content)
    {
        using var blocks = new MemoryStream();
        for (var offset = 0; offset < content.Length; offset += DropboxContentHash.BlockSize)
        {
            var length = Math.Min(DropboxContentHash.BlockSize, content.Length - offset);
            blocks.Write(SHA256.HashData(content.Slice(offset, length)));
        }
        return Convert.ToHexString(SHA256.HashData(blocks.ToArray())).ToLowerInvariant();
    }

    private static async Task StageAndCreateAsync(string root)
    {
        var notes = new Notes();
        var (writer, listing) = Create(root, notes);
        var source = Source("remote");
        var stage = await StageAsync(writer, "created", source);
        try
        {
            Check.Require(stage.Path == "created" && stage.Source == source, "A stage retains destination and source");
            Check.Require(stage.State == LocalStageState.Staged && writer.ActiveStages.Contains(stage.StagePath),
                "A prepared stage is actively owned");
            Check.Require(Path.GetDirectoryName(stage.StagePath) == root
                && StageNames.IsStage(Path.GetFileName(stage.StagePath)), "Stage name and parent");
            var publishedAttributes = SetStageAttributes(stage.StagePath);
            await ThrowsAsync<IOException>(() => OpenShared(stage.StagePath));
            Check.Require(File.GetLastWriteTimeUtc(stage.StagePath) == source.Modified.UtcDateTime,
                "Staging sets source time on the held output, which no other opener can read before publication");
            var published = await writer.PublishAsync(stage, null, null, CancellationToken.None);
            Check.Require(published.Outcome == RemovalOutcome.Published && published.OperationId is null
                && published.Final == One(listing, "created") && published.Cause is null,
                "A free-target create returns Published with its final entry and no pending operation");
            Check.Require(notes.Arms == 0, "A free-target create arms no pending note");
            Check.Require(stage.State == LocalStageState.Published && !File.Exists(stage.StagePath),
                "Publish consumes the stage and marks it published");
            Check.Require(File.GetAttributes(Path.Combine(root, stage.Path)) == publishedAttributes,
                "Create clears only Hidden and preserves the stage's other attributes");
        }
        finally
        {
            await stage.DisposeAsync();
        }
        Check.Require(File.ReadAllBytes(Path.Combine(root, "created")).SequenceEqual(Content),
            "Disposing a published stage preserves the final file");
        var abandoned = await StageAsync(writer, "abandoned");
        await abandoned.DisposeAsync();
        Check.Require(!File.Exists(abandoned.StagePath), "An unpublished stage is deleted on disposal");
        await abandoned.DisposeAsync();
        NoStages(writer, root);
        File.WriteAllBytes(abandoned.StagePath, Content);
        await abandoned.DisposeAsync();
        Check.Require(File.ReadAllBytes(abandoned.StagePath).SequenceEqual(Content),
            "Repeated disposal never reopens or deletes a replacement at the former stage path");
        Check.Require(writer.ActiveStages.Count == 0, "Repeated disposal keeps active ownership released");
    }

    private static async Task StageFailuresAsync(string root)
    {
        var notes = new Notes();
        var (writer, _) = Create(root, notes);
        var missing = Path.Combine("missing", "file");
        using (var input = new MemoryStream(Content))
        {
            var error = await ThrowsAsync<ItemFailedException>(() => writer.StageAsync(missing, input,
                Source(missing), CancellationToken.None));
            Check.Require(error.Cause == TextKey.ParentMissing && !Directory.Exists(Path.Combine(root, "missing")),
                "Stage never creates a missing parent");
        }
        var target = Path.Combine(root, "incomplete");
        File.WriteAllText(target, "original");
        foreach (var size in new[] { Content.Length - 1, Content.Length + 1 })
        {
            using var input = new MemoryStream(Content);
            var error = await ThrowsAsync<ItemFailedException>(() => writer.StageAsync("incomplete", input,
                Source("incomplete") with { Size = size, Modified = DateTimeOffset.MinValue }, CancellationToken.None));
            Check.Require(error.Cause == TextKey.DownloadIncomplete && error.InnerException is null,
                "Wrong length fails validation before applying even an invalid source timestamp");
            Check.Require(input.CanRead && File.ReadAllText(target) == "original",
                "A wrong-length stage leaves its input open and its existing target untouched");
            NoStages(writer, root);
        }
        using (var input = new CopyFailureStream(root))
        {
            var path = "io-failure";
            var error = await ThrowsAsync<ItemFailedException>(() => writer.StageAsync(path, input,
                Source(path), CancellationToken.None));
            Check.Require(error.Path == path && error.Cause == TextKey.DownloadIncomplete && error.InnerException is IOException,
                "A staging I/O failure is mapped and its cleanup does not mask it");
            NoStages(writer, root);
        }
        var disposed = new MemoryStream(Content);
        disposed.Dispose();
        await ThrowsAsync<ObjectDisposedException>(() => writer.StageAsync("broken", disposed,
            Source("broken"), CancellationToken.None));
        NoStages(writer, root);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var content = new MemoryStream(Content);
        await ThrowsAsync<OperationCanceledException>(() => writer.StageAsync("cancelled", content,
            Source("cancelled"), cancelled.Token));
        Check.Require(notes.Arms == 0, "Staging never arms a pending note");
        NoStages(writer, root);
    }

    private static async Task ContentMatchAsync(string root)
    {
        var notes = new Notes();
        var (writer, _) = Create(root, notes);
        foreach (var item in ContentMatchCases.All())
        {
            // Flush needs native fault injection; commit belongs to Transfers, not LocalSide.
            if (item.LaterFailure is "flush" or "commit" or "upload-envelope" or "upload-identity") continue;
            var source = Source(item.Id, item.Body) with
            {
                Size = item.AdvertisedSize,
                ContentHash = item.ExpectedHash! // Null at runtime is the CM04/CM09 input the gate must refuse.
            };
            if (item.LaterFailure == "timestamp") source = source with { Modified = DateTimeOffset.MinValue };
            using var input = new MemoryStream(item.Body);
            if (item.ExpectedGate != "accept")
            {
                var error = await ThrowsAsync<ItemFailedException>(() => writer.StageAsync(item.Id, input,
                    source, CancellationToken.None));
                var cause = item.ExpectedGate switch
                {
                    "size" => TextKey.DownloadIncomplete,
                    "hash" => TextKey.DownloadHashMismatch,
                    _ => throw new InvalidOperationException(item.Id)
                };
                Check.Require(error.Path == item.Id && error.Cause == cause && error.InnerException is null,
                    $"{item.Id}: the real local gate reports the shared size/hash refusal");
            }
            else if (item.LaterFailure == "timestamp")
            {
                await ThrowsAsync<ArgumentOutOfRangeException>(() => writer.StageAsync(item.Id, input,
                    source, CancellationToken.None));
            }
            else
            {
                await using var stage = (LocalStaged)await writer.StageAsync(item.Id, input, source,
                    CancellationToken.None);
                Check.Require(stage.State == LocalStageState.Staged && stage.Proof.Size == item.Body.Length
                    && stage.Proof.ContentHash == DropboxContentHash.Compute(item.Body),
                    $"{item.Id}: the real local gate accepts the shared content case");
                if (item.LaterFailure == "publication")
                {
                    await ThrowsAsync<IOException>(() => OpenShared(stage.StagePath));
                    Check.Require(stage.State == LocalStageState.Staged,
                        $"{item.Id}: no other handle can lock an accepted stage against its publication");
                }
                else Check.Require(item.LaterFailure is null, $"{item.Id}: unhandled local lifecycle boundary");
            }
            Check.Require(input.CanRead && notes.Arms == 0 && notes.Value is null
                && !File.Exists(Path.Combine(root, item.Id)),
                $"{item.Id}: no publication or pending note, and the caller retains its input");
            NoStages(writer, root);
        }
    }

    private static async Task HashBoundariesAsync(string root)
    {
        var notes = new Notes();
        var (writer, _) = Create(root, notes);
        Check.Require(DropboxContentHash.Compute(Array.Empty<byte>()) == EmptyContentHash,
            "The content facade matches the fixed empty-content vector");
        foreach (var size in new[]
        {
            0, 1, DropboxContentHash.BlockSize - 1, DropboxContentHash.BlockSize,
            DropboxContentHash.BlockSize + 1, DropboxContentHash.BlockSize * 2,
            DropboxContentHash.BlockSize * 2 + 123
        })
        {
            var content = Pattern(size);
            var expected = IndependentContentHash(content);
            Check.Require(DropboxContentHash.Compute(content) == expected,
                $"The content hash matches independent block composition at {size} bytes");
            var path = $"boundary-{size}";
            using var input = new MemoryStream(content);
            await using var stage = (LocalStaged)await writer.StageAsync(path, input, Source(path, content),
                CancellationToken.None);
            Check.Require(stage.State == LocalStageState.Staged && stage.Source.Size == size
                && stage.Source.ContentHash == expected,
                $"A {size}-byte stage is accepted only with the independently composed hash");
        }
        Check.Require(notes.Arms == 0, "Block-boundary staging never arms a pending note");
        NoStages(writer, root);
    }

    private static async Task HashMismatchAsync(string root)
    {
        var notes = new Notes();
        var (writer, _) = Create(root, notes);
        var bins = 0;
        LocalBin.SendOverride = _ => { bins++; return true; };
        var original = Pattern(DropboxContentHash.BlockSize * 2 + 64);
        var expected = DropboxContentHash.Compute(original);
        var cases = new (string Path, byte[]? Existing, bool Folder, int Corrupt)[]
        {
            ("mismatch-absent-first", null, false, 0),
            ("mismatch-absent-last", null, false, DropboxContentHash.BlockSize * 2 - 1),
            ("mismatch-absent-tail", null, false, DropboxContentHash.BlockSize * 2 + 10),
            ("mismatch-file", original, false, DropboxContentHash.BlockSize * 2 + 10),
            ("mismatch-folder", null, true, 0),
        };
        foreach (var item in cases)
        {
            var full = Path.Combine(root, item.Path);
            var existing = item.Existing;
            if (item.Folder) Directory.CreateDirectory(full);
            else if (existing is not null) File.WriteAllBytes(full, existing);
            var damaged = original.ToArray();
            damaged[item.Corrupt] ^= 0xff;
            var source = Source(item.Path, damaged) with { ContentHash = expected };
            using var input = new MemoryStream(damaged);
            var error = await ThrowsAsync<ItemFailedException>(() => writer.StageAsync(item.Path, input, source,
                CancellationToken.None));
            Check.Require(error.Path == item.Path && error.Cause == TextKey.DownloadHashMismatch
                && error.InnerException is null,
                "Same-length corruption is a hash mismatch naming the destination with no inner error");
            Check.Require(input.CanRead, "A rejected stage leaves its input open");
            if (item.Folder) Check.Require(Directory.Exists(full), "A rejected stage preserves an existing folder");
            else if (existing is not null)
                Check.Require(File.ReadAllBytes(full).SequenceEqual(existing),
                    "A rejected stage preserves the existing file bytes");
            else Check.Require(!File.Exists(full) && !Directory.Exists(full),
                "A rejected stage creates no final entry");
            NoStages(writer, root);
        }
        Check.Require(notes.Arms == 0 && bins == 0, "A hash mismatch never arms a pending note or reaches the bin");
    }

    private static async Task PublishPreconditionsAsync(string root)
    {
        var notes = new Notes();
        var (writer, listing) = Create(root, notes);
        var full = Path.Combine(root, "target");
        var old = Encoding.UTF8.GetBytes("old");
        File.WriteAllBytes(full, old);
        var expected = One(listing, "target");
        var selection = Select(root, listing, "target", old);
        await using (var occupied = await StageAsync(writer, "target"))
        {
            await ThrowsAsync<PathOccupiedException>(() => writer.PublishAsync(occupied, null, null,
                CancellationToken.None));
            Check.Require(occupied.State == LocalStageState.Staged, "An occupied create does not take pending ownership");
            Check.Require(notes.Arms == 0, "A refused create arms no pending note");
        }
        foreach (var stale in new[]
        {
            expected with { Id = "different" }, expected with { Size = expected.Size + 1 },
            expected with { Modified = expected.Modified.AddTicks(1) }
        })
        {
            await using var stage = await StageAsync(writer, "target");
            var staleSelection = new RemovalSelection(
                new RemovalProof(stale, VolumeId(root), DropboxContentHash.Compute(old)), null);
            await ThrowsAsync<ChangedUnderneathException>(() => writer.PublishAsync(stage, stale, staleSelection,
                CancellationToken.None));
            Check.Require(stage.State == LocalStageState.Staged && File.ReadAllBytes(full).SequenceEqual(old),
                "A changed target must not be binned or published over");
        }
        Check.Require(notes.Arms == 0, "A changed target is refused before any note is armed");
        await using (var unproved = await StageAsync(writer, "target"))
        {
            await ThrowsAsync<ArgumentException>(() => writer.PublishAsync(unproved, expected, null,
                CancellationToken.None));
            Check.Require(unproved.State == LocalStageState.Staged && File.ReadAllBytes(full).SequenceEqual(old),
                "A Local file replacement requires its selected losing proof");
        }
        var armsBeforeAbsent = notes.Arms;
        File.Delete(full);
        await using (var absent = await StageAsync(writer, "target"))
            await ThrowsAsync<ChangedUnderneathException>(() => writer.PublishAsync(absent, expected, selection,
                CancellationToken.None));
        Check.Require(notes.Arms == armsBeforeAbsent, "A target that vanished before the shell arms no new note");
        NoStages(writer, root);

        // A selection whose proof does not describe the selected bytes is refused before the shell.
        var wrongRoot = Path.Combine(root, "wrong-proof");
        Directory.CreateDirectory(wrongRoot);
        var wrongNotes = new Notes();
        var (wrongWriter, wrongListing) = Create(wrongRoot, wrongNotes);
        var wrongFull = Path.Combine(wrongRoot, "target");
        File.WriteAllBytes(wrongFull, old);
        var wrongExpected = One(wrongListing, "target");
        var wrongProof = new RemovalSelection(
            new RemovalProof(wrongExpected, VolumeId(wrongRoot), DropboxContentHash.Compute(Content)), null);
        var bins = 0;
        LocalBin.SendOverride = _ => { bins++; return true; };
        await using (var wrongStage = await StageAsync(wrongWriter, "target"))
        {
            await ThrowsAsync<ChangedUnderneathException>(() => wrongWriter.PublishAsync(wrongStage, wrongExpected,
                wrongProof, CancellationToken.None));
            Check.Require(File.ReadAllBytes(wrongFull).SequenceEqual(old) && bins == 0
                && wrongNotes.Arms == 0 && wrongNotes.Value is null && wrongStage.State == LocalStageState.Staged,
                "A selection that does not prove the selected bytes changes nothing and never reaches the shell");
        }
        NoStages(wrongWriter, wrongRoot);
    }

    private static async Task ReplaceAsync(string root)
    {
        var notes = new Notes();
        var (writer, listing) = Create(root, notes);
        var full = Path.Combine(root, "target");
        var old = Encoding.UTF8.GetBytes("old");
        File.WriteAllBytes(full, old);
        var expected = One(listing, "target");
        var saved = Saved("target", expected, old);
        var selection = Select(root, listing, "target", old, saved);
        var stage = await StageAsync(writer, "target");
        var publishedAttributes = SetStageAttributes(stage.StagePath);
        var bins = 0;
        LocalBin.SendOverride = path =>
        {
            Check.Require(path == full, "Bin target");
            Check.Require(notes.Value is { } note && note.Version == 1 && note.Generation == 0
                && note.Kind == PendingKind.LocalReplace && note.Phase == PendingPhase.Armed
                && note.Path == "target" && note.Losing == selection.Proof && note.Saved == saved
                && note.Source == stage.Source && note.Stage == stage.Proof,
                "The armed note binds the exact selection, saved binding, source and stage before binning");
            Check.Require(stage.State == LocalStageState.PendingOwned, "Pending ownership precedes binning");
            Check.Require(File.ReadAllBytes(path).SequenceEqual(old), "Bin sees the old version");
            bins++;
            File.Delete(path);
            return true;
        };
        try
        {
            var published = await writer.PublishAsync(stage, expected, selection, CancellationToken.None);
            Check.Require(published.Outcome == RemovalOutcome.Published && published.Cause is null
                && published.Final == One(listing, "target") && notes.Value is { } armed
                && published.OperationId == armed.OperationId,
                "Replace returns Published with the new entry and the pending operation, leaving settlement to the caller");
            Check.Require(stage.State == LocalStageState.Published, "Replace publishes its stage");
            Check.Require(bins == 1, "Replace recycles the old version exactly once");
        }
        finally
        {
            await stage.DisposeAsync();
        }
        Check.Require(File.ReadAllBytes(full).SequenceEqual(Content), "Replace publishes new bytes");
        Check.Require(File.GetAttributes(full) == publishedAttributes,
            "Replace clears only Hidden and preserves the stage's other attributes");
        NoStages(writer, root);
    }

    private static async Task EarlierPendingAsync(string root)
    {
        var notes = new Notes { FailNextArm = true };
        var (writer, listing) = Create(root, notes);
        var full = Path.Combine(root, "target");
        var old = Encoding.UTF8.GetBytes("old");
        File.WriteAllBytes(full, old);
        var selection = Select(root, listing, "target", old);
        await using (var stage = await StageAsync(writer, "target"))
        {
            var error = await ThrowsAsync<ItemFailedException>(() => writer.PublishAsync(stage,
                One(listing, "target"), selection, CancellationToken.None));
            Check.Require(error.Cause == TextKey.FailEarlierReplace && error.InnerException is InvalidOperationException,
                "An earlier pending note becomes FailEarlierReplace");
            Check.Require(stage.State == LocalStageState.Staged, "A refused note does not own the new stage");
        }
        Check.Require(File.ReadAllBytes(full).SequenceEqual(old), "An earlier note prevents binning");
        NoStages(writer, root);
    }

    private static async Task BinFailureAsync(string root)
    {
        var notes = new Notes();
        var (writer, listing) = Create(root, notes);
        var full = Path.Combine(root, "target");
        var old = Encoding.UTF8.GetBytes("old");
        File.WriteAllBytes(full, old);
        var selection = Select(root, listing, "target", old);
        var calls = 0;
        LocalBin.SendOverride = _ => { calls++; return false; };
        await using (var stage = await StageAsync(writer, "target"))
        {
            var result = await writer.PublishAsync(stage, One(listing, "target"), selection, CancellationToken.None);
            Check.Require(result.Outcome == RemovalOutcome.NoEffect && result.Cause == TextKey.RecycleUnavailable
                && result.Final is null,
                "A refused recycle is a definite no-effect with its native cause");
            Check.Require(calls == 1 && notes.Value is not null && File.ReadAllBytes(full).SequenceEqual(old),
                "A refused recycle keeps the target and the armed note for the caller's settlement");
            Check.Require(stage.State != LocalStageState.Published, "A refused recycle publishes nothing");
        }
    }

    private static async Task PublishFailuresAsync(string root)
    {
        var notes = new Notes();
        var (writer, listing) = Create(root, notes);
        var create = await StageAsync(writer, "create");
        try
        {
            await ThrowsAsync<IOException>(() => OpenShared(create.StagePath));
            Check.Require(create.State == LocalStageState.Staged && notes.Value is null,
                "No other handle can lock the held stage; it remains disposal-owned and has no pending note");
        }
        finally
        {
            await create.DisposeAsync();
        }
        NoStages(writer, root);

        var full = Path.Combine(root, "replace");
        var old = Encoding.UTF8.GetBytes("old");
        File.WriteAllBytes(full, old);
        var selection = Select(root, listing, "replace", old);
        var replace = await StageAsync(writer, "replace");
        LocalBin.SendOverride = path =>
        {
            File.Delete(path);
            Directory.CreateDirectory(path);
            return true;
        };
        try
        {
            var result = await writer.PublishAsync(replace, One(listing, "replace"), selection, CancellationToken.None);
            Check.Require(result.Outcome == RemovalOutcome.Unknown && result.Cause == TextKey.PublishFailed
                && result.Final is null && notes.Value is not null,
                "A post-bin publication failure is an uncertain result that retains recovery ownership");
            Check.Require(replace.State == LocalStageState.PendingOwned,
                "The verified removal leaves the stage pending-owned");
        }
        finally
        {
            await replace.DisposeAsync();
        }
        Check.Require(writer.ActiveStages.Count == 0 && File.Exists(replace.StagePath)
            && File.ReadAllBytes(replace.StagePath).SequenceEqual(Content)
            && File.GetLastWriteTimeUtc(replace.StagePath) == replace.Source.Modified.UtcDateTime,
            "Disposal leaves the exact pending bytes and source timestamp for recovery");
        Check.Require(Directory.Exists(full), "Failed publish never overwrites the new occupant");
    }

    private static async Task EnsureFoldersAsync(string root)
    {
        var (writer, listing) = Create(root);
        var created = await writer.EnsureFolderAsync("folder", CancellationToken.None);
        var existing = await writer.EnsureFolderAsync("folder", CancellationToken.None);
        Check.Require(created.IsFolder && created.Id == existing.Id && existing == One(listing, "folder"),
            "Folder ensure creates once and returns an existing folder unchanged");
        File.WriteAllText(Path.Combine(root, "file"), "old");
        await ThrowsAsync<PathOccupiedException>(() => writer.EnsureFolderAsync("file", CancellationToken.None));
        Check.Require(File.ReadAllText(Path.Combine(root, "file")) == "old", "Folder ensure preserves a blocking file");
    }

    private static async Task DeleteAsync(string root)
    {
        var old = Encoding.UTF8.GetBytes("old");
        var refusedRoot = Path.Combine(root, "refused");
        Directory.CreateDirectory(refusedRoot);
        var refusedNotes = new Notes();
        var (refusedWriter, refusedListing) = Create(refusedRoot, refusedNotes);
        var refusedPath = Path.Combine(refusedRoot, "file");
        File.WriteAllBytes(refusedPath, old);
        var refusedEntry = One(refusedListing, "file");
        var refusedSelection = Select(refusedRoot, refusedListing, "file", old);
        var refusals = 0;
        LocalBin.SendOverride = _ => { refusals++; return false; };
        var refused = await refusedWriter.DeleteAsync(refusedEntry, refusedSelection, CancellationToken.None);
        Check.Require(refused.Outcome == RemovalOutcome.NoEffect && refused.Cause == TextKey.RecycleUnavailable
            && refusals == 1 && File.ReadAllBytes(refusedPath).SequenceEqual(old),
            "A refused file delete keeps the target and reports the native cause");

        var notes = new Notes();
        var (writer, listing) = Create(root, notes);
        var file = Path.Combine(root, "file");
        File.WriteAllBytes(file, old);
        var expected = One(listing, "file");
        var selection = Select(root, listing, "file", old);
        LocalBin.SendOverride = _ => throw new InvalidOperationException("A stale delete reached the bin");
        var staleSelection = new RemovalSelection(
            new RemovalProof(expected with { Id = "stale" }, VolumeId(root), DropboxContentHash.Compute(old)), null);
        await ThrowsAsync<ChangedUnderneathException>(() => writer.DeleteAsync(expected with { Id = "stale" },
            staleSelection, CancellationToken.None));
        var calls = 0;
        LocalBin.SendOverride = target =>
        {
            Check.Require(target == file, "Delete forwards the exact target");
            calls++;
            File.Delete(target);
            return true;
        };
        var result = await writer.DeleteAsync(expected, selection, CancellationToken.None);
        Check.Require(result.Outcome == RemovalOutcome.Removed
            && notes.Value is { Kind: PendingKind.LocalDelete } note && result.OperationId == note.OperationId,
            "Delete recycles the checked file once and returns its pending operation");
        Check.Require(calls == 1 && listing.One("file") is null, "A file delete removes the checked item exactly once");
        await ThrowsAsync<ChangedUnderneathException>(() => writer.DeleteAsync(expected, selection,
            CancellationToken.None));
        Check.Require(calls == 1, "An absent delete target never reaches the bin");

        var folderPath = "folder";
        Directory.CreateDirectory(Path.Combine(root, folderPath));
        var folder = One(listing, folderPath);
        calls = 0;
        LocalBin.SendOverride = target =>
        {
            Check.Require(target == Path.Combine(root, folderPath), "Folder target");
            calls++;
            Directory.Delete(target);
            return true;
        };
        var folderResult = await writer.DeleteAsync(folder, null, CancellationToken.None);
        Check.Require(folderResult.Outcome == RemovalOutcome.Removed && folderResult.OperationId is null && calls == 1
            && listing.One(folderPath) is null,
            "Folder deletion stays with the folder owner without a file pending note");
        Check.Require(notes.Arms == 1, "Only the file delete armed a pending note");
    }

    private static async Task MoveAsync(string root)
    {
        var (writer, listing) = Create(root);
        foreach (var folder in new[] { false, true })
        {
            var path = folder ? "Folder" : "File";
            var movedPath = folder ? "MovedFolder" : "MovedFile";
            var full = Path.Combine(root, path);
            if (folder)
            {
                Directory.CreateDirectory(full);
                File.WriteAllText(Path.Combine(full, "child"), "child bytes");
            }
            else File.WriteAllText(full, "file bytes");
            var expected = One(listing, path);
            await ThrowsAsync<ChangedUnderneathException>(() => writer.MoveAsync(expected with { Id = "stale" }, movedPath, CancellationToken.None));
            File.WriteAllText(Path.Combine(root, "occupied"), "keep");
            await ThrowsAsync<PathOccupiedException>(() => writer.MoveAsync(expected, "occupied", CancellationToken.None));
            Check.Require(File.ReadAllText(Path.Combine(root, "occupied")) == "keep", "Move never overwrites another item");
            var moved = await writer.MoveAsync(expected, movedPath, CancellationToken.None);
            Check.Require(moved.Path == movedPath && moved.Id == expected.Id && listing.One(path) == null,
                "Ordinary move preserves identity and removes the old name");
            var casePath = movedPath.ToUpperInvariant();
            var recased = await writer.MoveAsync(moved, casePath, CancellationToken.None);
            Check.Require(recased.Path == casePath && recased.Id == expected.Id,
                "Single-step case-only move preserves identity and returns exact spelling");
            Check.Require(Directory.EnumerateFileSystemEntries(root).Any(p => Path.GetFileName(p) == casePath),
                "Case-only move changes the physical directory entry spelling");
            if (folder) Check.Require(File.ReadAllText(Path.Combine(root, casePath, "child")) == "child bytes",
                "A folder move carries its children");
            var missing = await ThrowsAsync<ChangedUnderneathException>(() => writer.MoveAsync(expected, "missing-source", CancellationToken.None));
            Check.Require(missing.Path == expected.Path, "A vanished move source names the source, not the destination");
        }
    }

    private static async Task MappedFailuresAsync(string root)
    {
        var (writer, listing) = Create(root);
        var full = Path.Combine(root, "file");
        File.WriteAllBytes(full, Content);
        var expected = One(listing, "file");
        using (var held = new FileStream(full, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            var read = await ThrowsAsync<ItemFailedException>(() => writer.OpenReadAsync(expected, null, CancellationToken.None));
            Check.Require(read.Path == expected.Path && read.Cause == TextKey.FailLocalInUse && read.InnerException is IOException,
                "A file open for writing becomes the item's in-use failure, not a run failure");
            var move = await ThrowsAsync<ItemFailedException>(() => writer.MoveAsync(expected, "moved", CancellationToken.None));
            Check.Require(move.Path == expected.Path && move.Cause == TextKey.FailLocalInUse && move.InnerException is IOException,
                "A locked move becomes the item's in-use failure naming the source");
        }
        Check.Require(File.ReadAllBytes(full).SequenceEqual(Content) && !File.Exists(Path.Combine(root, "moved")),
            "Failed locked operations preserve the source and leave no destination");
        var invalid = await ThrowsAsync<ItemFailedException>(() => writer.MoveAsync(expected, "CON.txt", CancellationToken.None));
        Check.Require(invalid.Cause == TextKey.LocalNameInvalid && invalid.Path == "CON.txt",
            "A reserved target is an item failure before any move");
        var parent = await ThrowsAsync<ItemFailedException>(() => writer.MoveAsync(expected, "absent/child", CancellationToken.None));
        Check.Require(parent.Cause == TextKey.MoveFailed && File.Exists(full),
            "A missing destination parent is not a missing source");
        var folder = Path.Combine(root, "folder");
        Directory.CreateDirectory(folder);
        var child = Path.Combine(folder, "child");
        File.WriteAllBytes(child, Content);
        var expectedFolder = One(listing, "folder");
        using (var held = new FileStream(child, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            var move = await ThrowsAsync<ItemFailedException>(() => writer.MoveAsync(expectedFolder, "moved-folder", CancellationToken.None));
            // Windows refuses a directory rename over an open child with ERROR_ACCESS_DENIED, which the approved
            // classifier keeps as the operation cause rather than a definite lock (LOCAL-IN-USE c8 C4).
            Check.Require(move.Cause == TextKey.MoveFailed && move.InnerException is IOException,
                "A folder with an open child surfaces the move failure");
        }
        Check.Require(File.ReadAllBytes(child).SequenceEqual(Content), "A refused folder move preserves its child");
        File.Delete(full);
        await ThrowsAsync<ChangedUnderneathException>(() => writer.OpenReadAsync(expected, null, CancellationToken.None));
        Directory.Delete(folder, recursive: true);
        await ThrowsAsync<ChangedUnderneathException>(() => writer.OpenReadAsync(expected with { Path = "folder/child" }, null, CancellationToken.None));

        var ensure = await ThrowsAsync<ItemFailedException>(() => writer.EnsureFolderAsync(new string('x', 256), CancellationToken.None));
        Check.Require(ensure.Cause == TextKey.PublishFailed, "A folder creation I/O failure is an item failure");
    }

    private static async Task OpenReadAsync(string root)
    {
        var (writer, listing) = Create(root);
        var full = Path.Combine(root, "file");
        File.WriteAllBytes(full, Content);
        var expected = One(listing, "file");
        await using (var opened = await writer.OpenReadAsync(expected, null, CancellationToken.None))
        {
            Check.Require(opened.CanSeek && !opened.CanWrite, "OpenRead returns a seekable read-only stream");
            using var bytes = new MemoryStream();
            await opened.CopyToAsync(bytes);
            Check.Require(bytes.ToArray().SequenceEqual(Content), "OpenRead returns the checked bytes");
            using var locker = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            locker.Lock(0, Content.Length);
            try
            {
                opened.Position = 0;
                var sync = Throws<ItemFailedException>(() => opened.Read(new byte[Content.Length], 0, Content.Length));
                Check.Require(sync.Path == expected.Path && sync.Cause == TextKey.FailLocalInUse
                    && sync.InnerException is IOException,
                    "A later byte-range lock on a synchronous read is the item's in-use failure");
                opened.Position = 0;
                var asyncRead = await ThrowsAsync<ItemFailedException>(() => opened.ReadAsync(
                    new byte[Content.Length], 0, Content.Length, CancellationToken.None));
                Check.Require(asyncRead.Path == expected.Path && asyncRead.Cause == TextKey.FailLocalInUse
                    && asyncRead.InnerException is IOException,
                    "A later byte-range lock on an asynchronous read is the item's in-use failure");
                opened.Position = 0;
                var wrapped = await ThrowsAsync<ItemFailedException>(() =>
                    CopyThroughHttpAsync(opened, new MemoryStream(), CancellationToken.None));
                Check.Require(wrapped.Path == expected.Path && wrapped.Cause == TextKey.FailLocalInUse,
                    "A later lock failure stays the item's classified failure through an HTTP-shaped source wrapper");
            }
            finally
            {
                locker.Unlock(0, Content.Length);
            }
        }
        foreach (var stale in new[]
        {
            expected with { Id = "stale" }, expected with { Size = expected.Size + 1 },
            expected with { Modified = expected.Modified.AddTicks(1) }
        })
        {
            await ThrowsAsync<ChangedUnderneathException>(() => writer.OpenReadAsync(stale, null, CancellationToken.None));
            using var exclusive = new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Check.Require(exclusive.Length == Content.Length, "Failed open-then-check releases its handle");
        }
        File.Move(full, Path.Combine(root, "renamed"));
        Check.Require(!File.Exists(full), "Closing OpenRead releases its rename exclusion");
    }

    // Stands in for an HTTP upload path that wraps a source IOException; the item failure must already be classified.
    private static async Task CopyThroughHttpAsync(Stream source, Stream destination, CancellationToken ct)
    {
        try
        {
            await source.CopyToAsync(destination, ct);
        }
        catch (IOException error)
        {
            throw new HttpRequestException("HTTP transport", error);
        }
    }

    private sealed class CopyFailureStream(string root) : MemoryStream(Content)
    {
        private bool _started;

        // The stage must be hidden before the loop writes its first copied bytes; the read side can only prove
        // the order because the writer reads before it writes each prefix.
        private int Next(Span<byte> buffer)
        {
            if (_started) throw new IOException("Copy failed");
            _started = true;
            var stage = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Single(file => StageNames.IsStage(Path.GetFileName(file)));
            Check.Require((File.GetAttributes(stage) & FileAttributes.Hidden) != 0,
                "The stage is hidden before the first content write");
            var prefix = Math.Min(buffer.Length, Content.Length / 2);
            Content.AsSpan(0, prefix).CopyTo(buffer);
            return prefix;
        }

        public override int Read(byte[] buffer, int offset, int count) => Next(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) => Next(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            Task.FromResult(Next(buffer.AsSpan(offset, count)));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            new(Next(buffer.Span));
    }

    // Store-seam double: the production PendingNotes port (and its mutation gate) runs above this.
    internal sealed class Notes() : ForwardingStateStore(null!)
    {
        public PendingEffect? Value { get; private set; }
        public int Arms { get; private set; }
        public bool FailNextArm { get; set; }

        public override PendingRead Pending(string pairId) =>
            Value is { } value ? new CurrentPending(value) : new NoPending();

        public override void ArmPending(string pairId, PendingEffect value)
        {
            if (FailNextArm)
            {
                FailNextArm = false;
                throw new InvalidOperationException("A pending note already exists");
            }
            Check.Require(Value is null, "Only one pending note may exist");
            Value = value;
            Arms++;
        }

        public override void AdvancePending(string pairId, PendingEffect expected, PendingEffect next)
        {
            Check.Require(Equals(Value, expected), "Advance names the current pending note");
            Value = next;
        }
    }

    private static async Task HashCoreLimitAsync(string root)
    {
        var (side, listing) = Create(root);
        var files = new[] { "first", "second", "third" };
        foreach (var file in files) File.WriteAllBytes(Path.Combine(root, file), Content);
        var first = One(listing, files[0]);
        var waiting = files.Skip(1).Select(file => (Entry: One(listing, file), Full: Path.Combine(root, file))).ToArray();

        var gate = (SemaphoreSlim)typeof(LocalSide)
            .GetField("HashGate", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Check.Require(gate.CurrentCount == Environment.ProcessorCount, "The shared hash gate has one slot per core");
        var heldSlots = 0;
        try
        {
            while (gate.Wait(0)) heldSlots++;
            using var cancelled = new CancellationTokenSource();
            var hashes = waiting
                .Select(item => LocalSide.ContentHashAsync(root, listing, item.Entry, cancelled.Token)).ToArray();
            try
            {
                Check.Require(hashes.All(hash => !hash.IsCompleted), "Hashes wait when all core slots are occupied");
                foreach (var item in waiting)
                {
                    using var exclusive = new FileStream(item.Full, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    Check.Require(exclusive.Length == Content.Length, "A waiting hash opens no file handle");
                }
            }
            finally
            {
                cancelled.Cancel();
                foreach (var hash in hashes) await ThrowsAsync<OperationCanceledException>(() => hash);
            }
            Check.Require(gate.CurrentCount == 0, "A cancelled waiter releases nothing it did not hold");
            if (Environment.ProcessorCount > 1)
            {
                gate.Release();
                heldSlots--;
                Check.Require(heldSlots > 0 && gate.CurrentCount == 1,
                    "Controlled holders occupy every core slot except one");
                var hashesWithHeldSlots = waiting.Select(item => side.ContentHashAsync(item.Entry, CancellationToken.None)).ToArray();
                var results = await Task.WhenAll(hashesWithHeldSlots);
                Check.Require(results.All(hash => hash == DropboxContentHash.Compute(Content)),
                    "Files hash correctly through the available slot while controlled holders retain the other slots");
                Check.Require(gate.CurrentCount == 1, "Successful hashes release only their own slot");
            }
        }
        finally
        {
            if (heldSlots > 0) gate.Release(heldSlots);
        }
        Check.Require(await LocalSide.ContentHashAsync(root, listing, first, CancellationToken.None)
            == DropboxContentHash.Compute(Content), "Hashing resumes and matches the Dropbox algorithm once released");
        Check.Require(gate.CurrentCount == Environment.ProcessorCount, "Hashing restores every core slot");
    }
}
