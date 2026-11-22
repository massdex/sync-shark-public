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
        return Task.CompletedTask;
    }
}
