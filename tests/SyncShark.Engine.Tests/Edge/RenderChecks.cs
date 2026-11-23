using Massdex.SyncShark.Engine.Edge;

namespace Massdex.SyncShark.Engine.Tests.Edge;

public static class RenderChecks
{
    public static Task RunAsync()
    {
        var n = new Notice(7, NoticeKind.PutBack, TextKey.FailLocalInUse, "file.txt", new IssueSubject([], null) { Holder="Editor" }, DateTimeOffset.UnixEpoch, false) { Reviewed="version" };
        string text = Render.Notice(n, "pair", ["retry", "ignore"]);
        Check.Require(text.Contains("Editor") && text.Contains("file.txt") && text.Contains("--reviewed version --choice retry") && text.Contains("--choice ignore") && !text.Contains("--choice dismiss"), "Lost notice cause or projected choices");
        Check.Require(!Render.Notice(n with { Subject = new([],null) }).Contains("Program holding"), "Unknown holder displayed as known");
        var conflict = n with { Kind=NoticeKind.Info, Cause=TextKey.InfoKeptBoth, Subject=new([
            new(Side.Local,"local-original","1"),new(Side.Dropbox,"cloud-original","2"),
            new(Side.Dropbox,"first-copy","3"),new(Side.Local,"second-copy","4")],null) };
        string kept=Render.Notice(conflict);
        Check.Require(conflict.Subject.Items.All(item=>kept.Contains(item.Path,StringComparison.Ordinal)),"Missing kept-both endpoint");
        var links = n with { Cause=TextKey.InfoLinks, Kind=NoticeKind.Info, Path=string.Empty,
            Subject=new([],null), LinkCount=3, LinkFolders=["north","south"] };
        Check.Require(Render.Notice(links).Contains(Text.Get(TextKey.InfoLinks,(TextArg.Count,"3"),(TextArg.Folders,"north, south")),StringComparison.Ordinal),
            "Link notice must count three members and list both distinct top folders");
        var disconnected = n with { Kind=NoticeKind.Failure, Cause=TextKey.AccountMissing, Path=string.Empty, Subject=new([],null) };
        string shown = Render.Notice(disconnected, "pair", [], "account-1");
        Check.Require(!shown.Contains("{Account}",StringComparison.Ordinal) && shown.Contains("account-1",StringComparison.Ordinal),
            "Disconnected account notice did not name the account instead of leaving a literal placeholder");
        return Task.CompletedTask;
    }
}
