namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void FreeSpacePresentationRoundTrip(string root)
    {
        var folder = Fresh(root, "space-notice-metadata");
        var legacy = new Notice("p1", 1, NoticeKind.Info, "legacy.txt", "Old plain text", "local-space-unavailable");
        var notices = Enumerable.Range(0, 5).Select(i => new Notice("p1", 1, NoticeKind.Info,
            $"folder/{i}.txt", "Waiting for room", "local-free-space", FreeSpace:
            new FreeSpaceNoticeMetadata(i % 2 == 0 ? "c:\\" : "C:/", 10, 100 - i, 50))).ToArray();
        string id;
        using (var store = SqliteStateStore.Open(folder, Fast, new TestClock()))
        {
            store.SavePair(Pair("p1"));
            store.AddNotices(notices.Append(legacy).ToArray());
            id = store.OpenNotices("p1").Single(n => n.Path == notices[0].Path).NoticeId!;
            store.AddNotices([notices[0] with { FreeSpace = notices[0].FreeSpace! with { FreeBytes = 80 } }]);
            Require(store.OpenNotices("p1").Count == 6, "space retries keep one row per original problem");
        }
        using (var store = SqliteStateStore.Open(folder, Fast, new TestClock()))
        {
            var read = store.OpenNotices("p1");
            Require(read.Single(n => n.Path == legacy.Path).Detail == legacy.Detail &&
                read.Single(n => n.Path == legacy.Path).FreeSpace is null, "legacy plain detail stays readable");
            var refreshed = read.Single(n => n.NoticeId == id);
            Require(refreshed.Detail == notices[0].Detail && refreshed.FreeSpace?.FreeBytes == 80,
                "structured detail and refreshed facts survive reopen without changing the notice id");
            var summary = FreeSpacePresentation.Group(read).Single();
            Require(summary.VolumeRoot == "C:" && summary.Count == 5 && summary.RequiredBytes == 50 &&
                summary.FreeBytes == 80 && summary.ReserveBytes == 50 && summary.Examples.Count == 3 &&
                summary.Members.Count == 5, "one stable volume summary retains all members with bounded examples");
            var cliItems = read.Select(n => new SyncCliConflictData(n.NoticeId!, n.PairId, n.Path, 0,
                null, null, null, null, null, null, "Info", n.Detail, "open") { FreeSpace = n.FreeSpace }).ToArray();
            var cli = new SyncCliConflictListData(cliItems, 100, 0, cliItems.Length, false, cliItems.Length)
                { FreeSpacePending = [summary] };
            var text = SyncCli.RenderHuman(cli);
            Require(text.Contains(summary.Detail, StringComparison.Ordinal) &&
                !text.Contains("recommendation=Waiting for room", StringComparison.Ordinal) &&
                text.Contains("Old plain text", StringComparison.Ordinal) && cli.Items.Count == 6,
                "CLI human output groups space rows while structured items retain IDs and legacy rows");
            Require(FreeSpacePresentation.Group(read.Reverse()).Single().Detail == summary.Detail,
                "summary is independent of enumeration order");
            store.CloseNotice(id, "resolved");
            Require(FreeSpacePresentation.Group(store.OpenNotices("p1")).Single().Count == 4,
                "closing one member leaves all other retry paths intact");
        }
        var duplicatedPath = notices[0] with { ReasonKey = "local-reserve-unavailable" };
        Require(FreeSpacePresentation.Group(notices.Append(duplicatedPath)).Single().Count == 5,
            "different reasons on the same pending path are not double-counted");
        var unknown = notices[0] with { FreeSpace = new FreeSpaceNoticeMetadata(null, 10) };
        Require(FreeSpacePresentation.Group([unknown]).Single().Detail.Contains("free unknown"),
            "unknown measurements remain explicitly unknown");
        var malformed = legacy with { Detail = "sync-shark-notice-v1:{broken" };
        Require(NoticeDetailCodec.Decode(malformed) == malformed, "malformed envelope remains readable as legacy text");
    }
}
