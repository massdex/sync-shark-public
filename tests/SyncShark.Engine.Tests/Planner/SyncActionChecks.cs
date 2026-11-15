using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Tests.Planner;

/// <summary>Checks for SyncAction's written side, settled no-op and copy/delete kinds.</summary>
public static class SyncActionChecks
{
    public static void Run()
    {
        var row = new Row("row-key", null, null, null, Array.Empty<Entry>(), null,
            new RowContext(false, false, false, null, false, false, false, false));

        var fixedSides = new (ActionKind Kind, Side Side)[]
        {
            (ActionKind.Upload, Side.Dropbox), (ActionKind.EnsureDropboxFolder, Side.Dropbox),
            (ActionKind.DeleteDropbox, Side.Dropbox), (ActionKind.MoveDropbox, Side.Dropbox),
            (ActionKind.Download, Side.Local), (ActionKind.EnsureLocalFolder, Side.Local),
            (ActionKind.DeleteLocal, Side.Local), (ActionKind.MoveLocal, Side.Local)
        };
        foreach (var (kind, side) in fixedSides)
        {
            Check.Require(new SyncAction(kind, row).WrittenSide() == side, $"{kind} writes {side}.");
            // A fixed kind takes its side from the kind, never from the explicit Side field.
            Check.Require(new SyncAction(kind, row, Side: PathNames.Other(side)).WrittenSide() == side,
                $"{kind} ignores an explicit side.");
        }

        foreach (var kind in new[] { ActionKind.ReplaceKind, ActionKind.MoveUnlinked })
        foreach (var side in Enum.GetValues<Side>())
            Check.Require(new SyncAction(kind, row, Side: side).WrittenSide() == side,
                $"{kind} writes its explicit {side} side.");

        foreach (var kind in new[] { ActionKind.ReplaceKind, ActionKind.MoveUnlinked })
            RequireException<ArgumentException>(() => new SyncAction(kind, row).WrittenSide(),
                "Side", null, $"{kind} without an explicit side is an argument error.");
        foreach (var kind in new[] { ActionKind.None, ActionKind.Record, ActionKind.NeedLocalHash,
            ActionKind.KeepBoth, ActionKind.ForgetOnly, ActionKind.Notice, ActionKind.DeferFolder })
            RequireException<ArgumentOutOfRangeException>(() => new SyncAction(kind, row).WrittenSide(),
                null, "action", $"{kind} has no written side.");

        Check.Require(new SyncAction(ActionKind.None, row).IsInSync(), "None/InSync is a settled no-op.");
        foreach (var reason in Enum.GetValues<NoneReason>())
            Check.Require(new SyncAction(ActionKind.None, row, None: reason).IsInSync()
                == (reason == NoneReason.InSync), $"None/{reason} is in sync only when the reason is InSync.");
        foreach (var kind in Enum.GetValues<ActionKind>())
            if (kind != ActionKind.None)
                Check.Require(!new SyncAction(kind, row).IsInSync(), $"{kind} is never a settled no-op.");

        foreach (var side in Enum.GetValues<Side>())
        {
            Check.Require(SyncAction.CopyOn(folder: true, side) == (side == Side.Local
                ? ActionKind.EnsureLocalFolder : ActionKind.EnsureDropboxFolder),
                $"CopyOn(folder, {side}) fetches a folder onto that side.");
            Check.Require(SyncAction.CopyOn(folder: false, side) == (side == Side.Local
                ? ActionKind.Download : ActionKind.Upload),
                $"CopyOn(file, {side}) fetches a file onto that side.");
            Check.Require(SyncAction.DeleteOn(side) == (side == Side.Local
                ? ActionKind.DeleteLocal : ActionKind.DeleteDropbox), $"DeleteOn({side}) deletes that side.");
        }

        var entry = new Entry("row-key", false, 0, DateTimeOffset.UnixEpoch, "id", "", "", null);
        var saved = new SyncedEntry("ROW-KEY", "row-key", "row-key", "row-key", false, "local", 0, 0, "remote", "");
        foreach (var kind in Enum.GetValues<ActionKind>())
        foreach (var bound in new[] { false, true })
        foreach (var occupied in new[] { false, true })
        {
            var candidate = row with { Synced = bound ? saved : null,
                Local = occupied ? entry : null, Dropbox = occupied ? entry : null };
            var expected = !bound && !occupied && kind is (ActionKind.Upload or ActionKind.Download or
                ActionKind.EnsureDropboxFolder or ActionKind.EnsureLocalFolder);
            Check.Require(new SyncAction(kind, candidate).CreatesNewItem() == expected,
                $"A234: {kind}, bound={bound}, occupied={occupied}");
        }

        // Methods, not record properties: formatting is the positional record's text and no method member appears.
        var settled = new SyncAction(ActionKind.None, row);
        Check.Require(settled.ToString() ==
            $"SyncAction {{ Kind = {settled.Kind}, Row = {settled.Row}, None = {settled.None}, " +
            $"NoticeKind = {settled.NoticeKind}, Cause = {settled.Cause}, Target = {settled.Target}, " +
            $"InfoAfter = {settled.InfoAfter}, Side = {settled.Side} }}",
            "SyncAction formatting remains the positional record's members only.");
        foreach (var kind in Enum.GetValues<ActionKind>())
        {
            var text = new SyncAction(kind, row, Side: Side.Local).ToString();
            Check.Require(text.StartsWith("SyncAction {") && !text.Contains("WrittenSide") && !text.Contains("IsInSync"),
                $"{kind} formats without the written-side methods.");
        }
    }

    private static void RequireException<T>(Action action, string? message, string? parameter,
        string what) where T : Exception
    {
        try
        {
            action();
            Check.Require(false, what);
        }
        catch (Exception error)
        {
            Check.Require(error.GetType() == typeof(T) && (message is null || error.Message == message)
                && (error as ArgumentException)?.ParamName == parameter,
                $"{what} Expected exactly {typeof(T).Name} " +
                $"(message {message ?? "from the runtime"}, parameter {parameter ?? "null"}), got {error.GetType().Name}.");
        }
    }
}
