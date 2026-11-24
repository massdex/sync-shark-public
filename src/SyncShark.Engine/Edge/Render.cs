using System.Globalization;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>Formats the notice projection without deriving resolution choices or looking up state.</summary>
public static class Render
{
    /// <summary>Show the retained cause for ordinary and put-back failures; name the holder only when observed.
    /// The supplied account is the pair's connection id, needed only by the disconnected-account refusal.</summary>
    public static string Notice(Notice notice, string? account = null)
    {
        var reason = notice.Cause == TextKey.InfoLinks ? Links(notice) :
            notice.Cause == TextKey.InfoKeptBoth ? KeptBoth(notice) :
            Text.Get(notice.Cause, (TextArg.Path, notice.Path), (TextArg.Message, string.Empty),
                (TextArg.Id, notice.Id.ToString(CultureInfo.InvariantCulture)), (TextArg.Account, account ?? string.Empty));
        if (notice.Cause == TextKey.FailLocalInUse && !string.IsNullOrWhiteSpace(notice.Subject.Holder))
            reason = Text.Get(TextKey.FileHolderKnown, (TextArg.Message, reason), (TextArg.Holder, notice.Subject.Holder));
        return Text.Get(TextKey.ResultIssueLine, (TextArg.Id, notice.Id.ToString(CultureInfo.InvariantCulture)),
            (TextArg.Path, notice.Path), (TextArg.At, notice.At.ToString(CultureInfo.InvariantCulture))) + Environment.NewLine + reason;
    }

    private static string Links(Notice notice)
    {
        if (notice.LinkCount is not int count || notice.LinkFolders is not { } folders)
            throw new InvalidDataException(nameof(notice.LinkCount));
        return Text.Get(TextKey.InfoLinks, (TextArg.Count, count.ToString(CultureInfo.InvariantCulture)),
            (TextArg.Folders, string.Join(", ", folders)));
    }

    private static string KeptBoth(Notice issue)
    {
        var items = issue.Subject.Items;
        var twoLegs = items is [_, _, { Side: Side.Dropbox }, { Side: Side.Local }];
        var oneLeg = items is [_, _, _];
        if ((!twoLegs && !oneLeg) ||
            items is not [{ Side: Side.Local } local, { Side: Side.Dropbox } dropbox, var copy, ..])
            return Text.Get(issue.Cause, (TextArg.Path, issue.Path));

        var keptLocal = oneLeg && copy.Side == Side.Local;
        var retained = keptLocal ? local : dropbox;
        var path = oneLeg ? retained.Path : issue.Path;
        var localVersion = twoLegs
            ? Text.Get(TextKey.InfoKeptBothLocalVersion, (TextArg.Path, local.Path), (TextArg.Name, copy.Path))
            : Text.Get(keptLocal ? TextKey.InfoKeptBothLocalOnly : TextKey.InfoKeptBothDropboxOnly,
                (TextArg.Path, retained.Path), (TextArg.Name, copy.Path));
        var dropboxVersion = twoLegs
            ? Environment.NewLine + Text.Get(TextKey.InfoKeptBothDropboxVersion,
                (TextArg.Path, dropbox.Path), (TextArg.Name, items[^1].Path)) : string.Empty;
        return Text.Get(oneLeg ? TextKey.InfoKeptBothRetained : issue.Cause, (TextArg.Path, path)) + Environment.NewLine +
            Text.Get(TextKey.InfoKeptBothLocations) + Environment.NewLine + localVersion + dropboxVersion;
    }

    /// <summary>Append only the permitted CLI choice words supplied by the read projection and command catalog.</summary>
    public static string Notice(Notice notice, string pair, IEnumerable<string> choices, string? account = null) => string.Join(Environment.NewLine,
        choices.Select(choice => Text.Get(TextKey.ResultIssueVisibilityChoice, (TextArg.Name, choice), (TextArg.Pair, pair),
            (TextArg.Id, notice.Id.ToString(CultureInfo.InvariantCulture)), (TextArg.Value, notice.Reviewed ?? string.Empty),
            (TextArg.Choice, choice))).Prepend(Notice(notice, account)));
}
