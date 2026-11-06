using System.Runtime.CompilerServices;
using System.Text.Json;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Contracts;

/// <summary>Checks for Row's entry/path/folder facts and one-sided construction (`--engine-checks row`).</summary>
public static class RowChecks
{
    private static readonly RowContext Context =
        new(false, false, false, null, false, false, false, false);

    internal static Entry? EntrySink;
    internal static string? StringSink;
    internal static bool BoolSink;

    public static void Run()
    {
        EntryOn();
        PathOn();
        OwnedPaths();
        OneSide();
        FolderFact();
        ContextFacts();
        RecordShape();
        RetainedContextShape();
        OutcomeSuccess();
        SelectorsAllocateNothing();
    }

    private static void OwnedPaths()
    {
        foreach (var local in new Entry?[] { null, MakeEntry("Local") })
        foreach (var remote in new Entry?[] { null, MakeEntry("Remote") })
        foreach (var saved in new SyncedEntry?[] { null, Saved() })
        foreach (var target in new string?[] { null, "", "Target" })
        foreach (var destination in new string?[] { null, "", "Destination" })
        {
            var row = MakeRow(local, remote, saved, []) with { Context = Context with { Destination = destination } };
            Check.Require(Row.Path(row) == (local?.Path ?? remote?.Path ?? saved?.LocalPath ?? row.Key),
                "Row owns the unchanged display/failure path precedence.");
            var paths = Row.Paths(row, target);
            Check.Require(paths.Local == (local?.Path ?? target ?? saved?.LocalPath ?? destination ?? row.Key) &&
                paths.Dropbox == (remote?.Path ?? target ?? saved?.DropboxPath ?? destination ?? row.Key),
                "Row owns both endpoint fallbacks, preserving null versus empty.");
        }
    }

    private static void EntryOn()
    {
        var local = MakeEntry("LocalFile.txt");
        var dropbox = MakeEntry("RemoteFile.txt");
        var both = MakeRow(local, dropbox, Saved(), []);

        Check.Require(ReferenceEquals(both.EntryOn(Side.Local), local), "EntryOn(Local) is the local observation.");
        Check.Require(ReferenceEquals(both.EntryOn(Side.Dropbox), dropbox),
            "EntryOn(Dropbox) is the Dropbox observation.");
        Check.Require(ReferenceEquals(both.EntryOn((Side)7), dropbox),
            "EntryOn falls back to Dropbox for a non-local side.");

        var localOnly = MakeRow(local, null, Saved(), []);
        Check.Require(ReferenceEquals(localOnly.EntryOn(Side.Local), local),
            "EntryOn(Local) returns the only observed entry.");
        Check.Require(localOnly.EntryOn(Side.Dropbox) is null, "EntryOn has no saved-entry fallback.");
    }

    private static void PathOn()
    {
        var local = MakeEntry("LocalFile.txt");
        var dropbox = MakeEntry("RemoteFile.txt");
        var saved = Saved();
        var both = MakeRow(local, dropbox, saved, []);

        Check.Require(both.PathOn(Side.Local, "Target.txt") == local.Path,
            "an observed local path wins over the target and saved path.");
        Check.Require(both.PathOn(Side.Dropbox, "Target.txt") == dropbox.Path,
            "an observed Dropbox path wins over the target and saved path.");

        var absent = MakeRow(null, null, saved, []);
        Check.Require(absent.PathOn(Side.Local, "Target.txt") == "Target.txt",
            "the target wins over the saved path.");
        Check.Require(absent.PathOn(Side.Dropbox) == saved.DropboxPath,
            "the saved Dropbox path is the last fallback.");
        Check.Require(absent.PathOn(Side.Local) == saved.LocalPath, "the saved local path is the last fallback.");
        Check.Require(absent.PathOn((Side)7) == saved.DropboxPath,
            "a non-local side uses the saved Dropbox path.");

        var empty = MakeRow(null, null, null, []);
        Check.Require(empty.PathOn(Side.Local) is null && empty.PathOn(Side.Dropbox, null) is null,
            "with nothing present the path is null.");
    }

    private static void OneSide()
    {
        var local = MakeEntry("LocalFile.txt");
        var saved = Saved();
        var extra = new[] { MakeEntry("Extra.txt") };

        var row = Row.ForOneSide(Side.Local, "row-key", local, saved, extra, "content-hash", Context);
        Check.Require(ReferenceEquals(row.Local, local) && row.Dropbox is null,
            "ForOneSide(Local) fills only the local entry.");
        Check.Require(row.Key == "row-key" && ReferenceEquals(row.Synced, saved) &&
            ReferenceEquals(row.Extra, extra) && row.LocalHash == "content-hash" &&
            ReferenceEquals(row.Context, Context), "ForOneSide passes the remaining arguments unchanged.");

        var dropboxRow = Row.ForOneSide(Side.Dropbox, "row-key", local, saved, extra, null, Context);
        Check.Require(ReferenceEquals(dropboxRow.Dropbox, local) && dropboxRow.Local is null,
            "ForOneSide(Dropbox) fills only the Dropbox entry.");

        var invalid = Row.ForOneSide((Side)7, "row-key", local, saved, extra, null, Context);
        Check.Require(invalid.Local is null && invalid.Dropbox is null,
            "an invalid side populates neither entry.");

        var absent = Row.ForOneSide(Side.Local, "row-key", null, null, [], null, Context);
        Check.Require(absent.Local is null && absent.Dropbox is null, "a null entry is accepted.");

        Check.Require(ConstructionBytes(() => Row.ForOneSide(Side.Local, "row-key", local, saved, extra, null, Context))
            == ConstructionBytes(() => new Row("row-key", local, null, saved, extra, null, Context)),
            "ForOneSide allocates exactly one row, like its constructor.");
    }

    private static void FolderFact()
    {
        var folder = MakeEntry("Folder", folder: true);
        var file = MakeEntry("File.txt");

        Check.Require(MakeRow(folder, file, null, []).IsFolder(), "a local folder makes the row a folder.");
        Check.Require(!MakeRow(file, folder, null, []).IsFolder(),
            "the local observation wins over the Dropbox folder.");
        Check.Require(MakeRow(null, folder, null, []).IsFolder(), "a Dropbox folder makes the row a folder.");
        Check.Require(MakeRow(null, null, Saved(folder: true), []).IsFolder(),
            "the saved folder fact is used when neither entry exists.");
        Check.Require(!MakeRow(null, null, Saved(folder: false), []).IsFolder(), "no folder fact is false.");
        Check.Require(!MakeRow(null, null, null, [folder]).IsFolder(), "Extra entries are not consulted.");
    }

    private static void ContextFacts()
    {
        var complete = new RowContext(false, false, false, null, false, false, true, false);
        var eligible = new RowContext(false, false, false, null, false, false, false, true);

        Check.Require(complete.CompleteInventory && !complete.DeletionEligible,
            "a complete inventory is not by itself deletion eligibility.");
        Check.Require(eligible.DeletionEligible && !eligible.CompleteInventory,
            "deletion eligibility is separate from inventory completeness.");
    }

    private static void RecordShape()
    {
        var bare = new Row("k", null, null, null, Array.Empty<Entry>(), null, null!);
        const string baselineText =
            "Row { Key = k, Local = , Dropbox = , Synced = , Extra = Massdex.SyncShark.Engine.Entry[], " +
            "LocalHash = , Context = , Deletion = , Ambiguous = False }";
        const string baselineJson =
            "{\"Key\":\"k\",\"Local\":null,\"Dropbox\":null,\"Synced\":null,\"Extra\":[],\"LocalHash\":null," +
            "\"Context\":null,\"Deletion\":null,\"Ambiguous\":false}";

        Check.Require(bare.ToString() == baselineText, "Row.ToString keeps the baseline members.");
        Check.Require(!bare.ToString().Contains("IsFolder", StringComparison.Ordinal),
            "Row.ToString has no IsFolder member.");
        Check.Require(JsonSerializer.Serialize(bare) == baselineJson,
            "default Row JSON keeps the baseline members, without IsFolder.");

        var local = MakeEntry("LocalFile.txt");
        var folder = MakeEntry("RemoteFolder", folder: true);
        var row = MakeRow(folder, local, null, []);
        var copy = row with { LocalHash = "copied" };
        Check.Require(!ReferenceEquals(copy, row) && copy.LocalHash == "copied" && copy.Key == row.Key &&
            ReferenceEquals(copy.Local, row.Local) && copy.IsFolder(),
            "a with-copy keeps the row facts and the folder answer.");
        Check.Require(row.Deletion is null, "a row without a requested answer has no deletion evidence.");

        var evidence = new DeletionEvidence.NoRetainedHistory("RemoteFolder");
        var evidenced = row with { Deletion = evidence };
        Check.Require(ReferenceEquals(evidenced.Deletion, evidence) && ReferenceEquals(evidenced.Local, row.Local) &&
            evidenced.Key == row.Key && evidenced.IsFolder(),
            "a with-copy carries the deletion evidence and keeps the row facts.");
        Check.Require((evidenced with { Deletion = null }).Deletion is null,
            "a with-copy clears the deletion evidence.");
    }

    private static void RetainedContextShape()
    {
        var json = JsonSerializer.Serialize(Context);
        using var old = JsonDocument.Parse(json);
        Check.Require(!old.RootElement.TryGetProperty(nameof(RowContext.FirstRunDeletion), out _),
            nameof(RetainedContextShape));
        Check.Require(JsonSerializer.Deserialize<RowContext>(json) == Context,
            nameof(RetainedContextShape));
        foreach (var kind in Enum.GetValues<IssueSubject.FirstRunDeletionKind>())
        {
            var retained = Context with { FirstRunDeletion = kind };
            var encoded = JsonSerializer.Serialize(retained);
            using var document = JsonDocument.Parse(encoded);
            Check.Require(document.RootElement.TryGetProperty(nameof(RowContext.FirstRunDeletion), out _) ==
                (kind != IssueSubject.FirstRunDeletionKind.None), nameof(RetainedContextShape));
            Check.Require(JsonSerializer.Deserialize<RowContext>(encoded) == retained,
                nameof(RetainedContextShape));
            var row = MakeRow(null, null, null, []) with { Context = retained };
            Check.Require(ReferenceEquals((row with { LocalHash = null }).Context, retained) &&
                (retained with { CompleteInventory = true }).FirstRunDeletion == kind &&
                Context.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.None,
                nameof(RetainedContextShape));
        }
    }

    private static void OutcomeSuccess()
    {
        foreach (var action in Enum.GetValues<ActionKind>())
        foreach (var deferred in new[] { false, true })
        foreach (var raised in Enum.GetValues<IssueKind>().Select(kind => (IssueKind?)kind).Prepend(null))
        foreach (var none in Enum.GetValues<NoneReason>().Select(reason => (NoneReason?)reason).Prepend(null))
        {
            var outcome = new SyncOutcome(action, none, null, raised, deferred);
            var expected = !deferred && raised is not (IssueKind.Failure or IssueKind.Tie or IssueKind.NameClash)
                && (none is null or NoneReason.InSync);
            Check.Require(outcome.Succeeded() == expected, nameof(OutcomeSuccess));
        }
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            new SyncOutcome(ActionKind.Record, null, null, null, false)));
        Check.Require(!json.RootElement.TryGetProperty(nameof(SyncOutcome.Succeeded), out _),
            nameof(OutcomeSuccess));
    }

    private static void SelectorsAllocateNothing()
    {
        var local = MakeEntry("LocalFile.txt");
        var dropbox = MakeEntry("RemoteFile.txt");
        var localFolder = MakeEntry("LocalFolder", folder: true);
        var dropboxFolder = MakeEntry("RemoteFolder", folder: true);
        var saved = Saved();
        var row = MakeRow(local, dropbox, saved, []);
        var folderRow = MakeRow(localFolder, dropbox, null, []);
        var dropboxFolderRow = MakeRow(null, dropboxFolder, null, []);
        var targetOnly = MakeRow(null, null, saved, []);
        var absent = MakeRow(null, null, null, []);
        string? target = "Target.txt";
        string? noTarget = null;

        Check.Require(
            AllocProbe.Measure(index =>
            {
                for (var i = 0; i < 1_000; i++)
                {
                    Sink(row.EntryOn(Side.Local));
                    Sink(row.EntryOn(Side.Dropbox));
                }
            }) == AllocProbe.Measure(index =>
            {
                for (var i = 0; i < 1_000; i++)
                {
                    Sink(row.Local);
                    Sink(row.Dropbox);
                }
            }), "Row.EntryOn allocates no more than its inline expression.");

        Check.Require(
            AllocProbe.Measure(index =>
            {
                for (var i = 0; i < 1_000; i++)
                {
                    Sink(row.PathOn(Side.Local, target));
                    Sink(row.PathOn(Side.Dropbox, target));
                    Sink(targetOnly.PathOn(Side.Local));
                    Sink(targetOnly.PathOn(Side.Dropbox));
                    Sink(absent.PathOn(Side.Local, target));
                    Sink(absent.PathOn(Side.Dropbox));
                }
            }) == AllocProbe.Measure(index =>
            {
                for (var i = 0; i < 1_000; i++)
                {
                    Sink(row.Local?.Path ?? target ?? row.Synced?.PathOn(Side.Local));
                    Sink(row.Dropbox?.Path ?? target ?? row.Synced?.PathOn(Side.Dropbox));
                    Sink(targetOnly.Local?.Path ?? noTarget ?? targetOnly.Synced?.PathOn(Side.Local));
                    Sink(targetOnly.Dropbox?.Path ?? noTarget ?? targetOnly.Synced?.PathOn(Side.Dropbox));
                    Sink(absent.Local?.Path ?? target ?? absent.Synced?.PathOn(Side.Local));
                    Sink(absent.Dropbox?.Path ?? noTarget ?? absent.Synced?.PathOn(Side.Dropbox));
                }
            }), "Row.PathOn allocates no more than its inline expression on every branch.");

        Check.Require(
            AllocProbe.Measure(index =>
            {
                for (var i = 0; i < 1_000; i++)
                {
                    Sink(row.IsFolder());
                    Sink(folderRow.IsFolder());
                    Sink(dropboxFolderRow.IsFolder());
                    Sink(targetOnly.IsFolder());
                }
            }) == AllocProbe.Measure(index =>
            {
                for (var i = 0; i < 1_000; i++)
                {
                    Sink((row.Local ?? row.Dropbox)?.IsFolder ?? row.Synced?.IsFolder ?? false);
                    Sink((folderRow.Local ?? folderRow.Dropbox)?.IsFolder ?? folderRow.Synced?.IsFolder ?? false);
                    Sink((dropboxFolderRow.Local ?? dropboxFolderRow.Dropbox)?.IsFolder ??
                        dropboxFolderRow.Synced?.IsFolder ?? false);
                    Sink((targetOnly.Local ?? targetOnly.Dropbox)?.IsFolder ?? targetOnly.Synced?.IsFolder ?? false);
                }
            }), "Row.IsFolder allocates no more than its inline expression.");
    }

    private static long ConstructionBytes(Func<Row> create) =>
        AllocProbe.Measure(index => { _ = create(); }, 256, 64) / 256;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(Entry? value) => EntrySink = value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(string? value) => StringSink = value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(bool value) => BoolSink = value;

    private static Row MakeRow(Entry? local, Entry? dropbox, SyncedEntry? synced, IReadOnlyList<Entry> extra) =>
        new("row-key", local, dropbox, synced, extra, null, Context);

    private static Entry MakeEntry(string path, bool folder = false) =>
        new(path, folder, 0, DateTimeOffset.UnixEpoch, "id", "rev", "hash", null);

    private static SyncedEntry Saved(bool folder = false) =>
        new("LocalKey", "SavedLocal.txt", "DropboxKey", "SavedRemote.txt", folder, "local-id", 0, 0,
            "dropbox-id", "content-hash");
}
