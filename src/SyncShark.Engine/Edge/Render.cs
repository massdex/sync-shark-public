using System.Globalization;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>Formats the notice projection without deriving resolution choices or looking up state.</summary>
public static class Render
{
    /// <summary>Show the retained cause for ordinary and put-back failures; name the holder only when observed.</summary>
    public static string Notice(Notice notice)
    {
        var reason = Text.Get(notice.Cause, (TextArg.Path, notice.Path));
        if (notice.Cause == TextKey.FailLocalInUse && !string.IsNullOrWhiteSpace(notice.Subject.Holder))
            reason = Text.Get(TextKey.FileHolderKnown, (TextArg.Message, reason), (TextArg.Holder, notice.Subject.Holder));
        return Text.Get(TextKey.ResultIssueLine, (TextArg.Id, notice.Id.ToString(CultureInfo.InvariantCulture)),
            (TextArg.Path, notice.Path), (TextArg.At, notice.At.ToString("O", CultureInfo.InvariantCulture))) + Environment.NewLine + reason;
    }

    /// <summary>Append only the permitted CLI choice words supplied by the read projection and command catalog.</summary>
    public static string Notice(Notice notice, string pair, IEnumerable<string> choices) => string.Join(Environment.NewLine,
        choices.Select(choice => Text.Get(TextKey.ResultIssueVisibilityChoice, (TextArg.Name, choice), (TextArg.Pair, pair),
            (TextArg.Id, notice.Id.ToString(CultureInfo.InvariantCulture)), (TextArg.Value, notice.Reviewed ?? string.Empty),
            (TextArg.Choice, choice))).Prepend(Notice(notice)));
}
