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
            DropboxWriter.Classify("files/delete_v2", member, selected, accepted, reply);
        var pending = Deleted(new JobReply.Running(), job);
        Check.Require(pending.Outcome == RemovalOutcome.Pending && pending.Accepted == job, "Running settled a deletion");
        Check.Require(Deleted(new JobReply.Terminal([(selected, null, null)])).Outcome == RemovalOutcome.Removed &&
            Deleted(new JobReply.Terminal([(selected with { Id = "neighbour" }, null, null)])).Outcome == RemovalOutcome.Unknown &&
            Deleted(new JobReply.Terminal([(null, TextKey.FailChanged, FailureDisposition.Permanent)])).Outcome == RemovalOutcome.Unknown &&
            Deleted(new JobReply.Terminal([(selected with { Path = "moved" }, null, null)])).Outcome == RemovalOutcome.Removed &&
            Deleted(new JobReply.Terminal([])).Outcome == RemovalOutcome.Unknown, "Unattributed or missing delete receipt settled a member");
        var unknown = new JobReply.Terminal([(null, TextKey.FailChanged, FailureDisposition.UnknownEffect)]);
        var observed = Deleted(unknown);
        Check.Require(observed.Outcome == RemovalOutcome.Unknown && ReferenceEquals(observed.Reply, unknown),
            "An absence observation must not prove our delete or be discarded before redecision");
        var folder = selected with { IsFolder = true };
        var move = member with { Destination = "new" };
        RemovalResult Move(Entry target, FailureDisposition disposition) => DropboxWriter.Classify("files/move_v2", move,
            target, null, new JobReply.Terminal([(null, TextKey.FailDropboxTooManyFiles, disposition)]));
        Check.Require(Move(folder, FailureDisposition.Permanent) is { Outcome: RemovalOutcome.NoEffect, Cause: TextKey.FailDropboxTooManyFiles } &&
            Move(folder, FailureDisposition.UnknownEffect).Outcome == RemovalOutcome.Unknown,
            "Only a proved selected-folder move size refusal can split");
        try { Move(selected, FailureDisposition.Permanent); throw new InvalidDataException("File size refusal authorized a folder split"); }
        catch (ItemFailedException e) when (e.Cause == TextKey.FailDropboxTooManyFiles) { }
        var receipt = selected with { Path = "Item" };
        RemovalResult Published(JobReply reply) => DropboxWriter.Classify("files/upload", member, selected, null, reply);
        Check.Require(Published(new JobReply.Terminal([(receipt, null, null)])).Final == receipt &&
            Published(new JobReply.Terminal([(null, TextKey.FailDropboxOutcomeUnknown, FailureDisposition.UnknownEffect)])).Outcome == RemovalOutcome.Unknown,
            "Attributed publication must preserve the actual receipt and an unproved publication must remain unknown");
        return Task.CompletedTask;
    }
    /// <summary>Direct staging retains the source; a closed session owns a complete digest and no source stream.</summary>
    public static Task StagedAsync()
    {
        using var bytes=new MemoryStream(new byte[]{1});var file=Scenarios.ItemChecks.File(Side.Local,"item",1,"id");
        var direct=global::Massdex.SyncShark.Engine.Dropbox.DropboxStaged.Single(file.Path,bytes,file);var session=global::Massdex.SyncShark.Engine.Dropbox.DropboxStaged.Session(file.Path,"session",1,file,file.ContentHash);
        Check.Require(direct.ConsumesSourceOnPublish && direct.Content==bytes && !session.ConsumesSourceOnPublish && session.Content==null && session.ContentHash==file.ContentHash,"Staging confused a completed session with pending input");return Task.CompletedTask;
    }
}
