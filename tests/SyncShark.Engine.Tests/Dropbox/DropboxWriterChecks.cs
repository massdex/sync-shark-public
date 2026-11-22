using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for DropboxWriter.</summary>
public static class DropboxWriterChecks
{
    /// <summary>Verifies the DropboxWriter contract through the module surface.</summary>
    public static Task RunAsync()
    {
        var selected = Scenarios.ItemChecks.File(Side.Dropbox, "item", 42, "id:selected");
        var member = new JobMember(selected.Id, selected.Path, null);
        var job = new AcceptedJob("pair", "files/delete_batch", "token", [member]);
        RemovalResult Deleted(JobReply reply, AcceptedJob? accepted = null) =>
            DropboxWriter.Classify("files/delete_v2", member, selected, null, accepted, reply);
        var pending = Deleted(new JobReply.Running(), job);
        Check.Require(pending.Outcome == RemovalOutcome.Pending && pending.Accepted == job, "Running settled a deletion");
        Check.Require(Deleted(new JobReply.Terminal([(selected, null, null)])).Outcome == RemovalOutcome.Removed &&
            Deleted(new JobReply.Terminal([(selected with { Id = "neighbour" }, null, null)])).Outcome == RemovalOutcome.Unknown &&
            Deleted(new JobReply.Terminal([(null, TextKey.FailChanged, FailureDisposition.Permanent)])).Outcome == RemovalOutcome.Unknown &&
            Deleted(new JobReply.Terminal([])).Outcome == RemovalOutcome.Unknown, "Unattributed or missing delete receipt settled a member");
        var upload = DropboxStaged.Session(selected.Path, "session", selected.Size, selected, selected.ContentHash);
        RemovalResult Published(Entry receipt) => DropboxWriter.Classify("files/upload_session/finish", member, selected, upload, null,
            new JobReply.Terminal([(receipt, null, null)]));
        Check.Require(Published(selected with { Path = "Item" }).Final?.Path == "Item" &&
            Published(selected with { Path = "other" }).Outcome == RemovalOutcome.Unknown &&
            Published(selected with { ContentHash = new string('0', selected.ContentHash.Length) }).Outcome == RemovalOutcome.Unknown,
            "Publication accepted unproven bytes or changed the returned spelling");
        return StagedAsync();
    }
    /// <summary>Direct staging retains the source; a closed session owns a complete digest and no source stream.</summary>
    public static Task StagedAsync()
    {
        using var bytes=new MemoryStream(new byte[]{1});var file=Scenarios.ItemChecks.File(Side.Local,"item",1,"id");
        var direct=global::Massdex.SyncShark.Engine.Dropbox.DropboxStaged.Single(file.Path,bytes,file);var session=global::Massdex.SyncShark.Engine.Dropbox.DropboxStaged.Session(file.Path,"session",1,file,file.ContentHash);
        Check.Require(direct.ConsumesSourceOnPublish && direct.Content==bytes && !session.ConsumesSourceOnPublish && session.Content==null && session.ContentHash==file.ContentHash,"Staging confused a completed session with pending input");return Task.CompletedTask;
    }
}
