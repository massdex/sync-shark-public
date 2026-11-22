namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Submits the one selected effect and classifies only that member's receipt.</summary>
public sealed class DropboxWriter
{
    internal const string Create = "files/create_folder_v2", Delete = "files/delete_v2", Move = "files/move_v2";
    internal const string Upload = "files/upload", Finish = "files/upload_session/finish";

    /// <summary>Publication keeps the selected revision and never resends an uncertain write.</summary>
    public Task<RemovalResult> UploadAsync(PairConfig pair, Dispatcher dispatcher, DropboxStaged stage, Entry? expected, CancellationToken ct)
    {
        if (expected is not null && (expected.IsFolder || string.IsNullOrWhiteSpace(expected.Id) || string.IsNullOrEmpty(expected.Revision) || !PathNames.Same(stage.Path, expected.Path)))
            throw new ChangedUnderneathException(stage.Path);
        return SubmitAsync(pair, dispatcher, stage.ConsumesSourceOnPublish ? Upload : Finish,
            new(expected?.Id, stage.Path, null), expected, false, stage, ct);
    }

    internal static async Task<RemovalResult> SubmitAsync(PairConfig pair, Dispatcher dispatcher, string route,
        JobMember member, Entry? expected, bool exclusive, DropboxStaged? upload, CancellationToken ct)
    {
        if ((route is Delete or Move) && string.IsNullOrWhiteSpace(member.Id) ||
            route == Delete && expected is { IsFolder: false } && string.IsNullOrWhiteSpace(expected.Revision))
            throw new ChangedUnderneathException(member.Path);
        var (accepted, reply) = await dispatcher.SubmitAsync(pair, route, member,
            expected is { IsFolder: false } ? expected.Revision : null, exclusive, upload, ct).ConfigureAwait(false);
        return Classify(route, member, expected, upload, accepted, reply);
    }

    /// <summary>Classifies an already attributed single-member reply; no observation starts a second request.</summary>
    public static RemovalResult Classify(string route, JobMember member, Entry? expected, DropboxStaged? upload,
        AcceptedJob? accepted, JobReply reply)
    {
        RemovalResult Result(RemovalOutcome outcome, Entry? entry = null, TextKey? cause = null) =>
            new(outcome, entry, cause) { Accepted = accepted, Reply = reply };
        if (accepted is not null && reply is JobReply.Running) return Result(RemovalOutcome.Pending);
        if (reply is not JobReply.Terminal { Members.Count: 1 } terminal) return Result(RemovalOutcome.Unknown, cause: TextKey.FailDropboxOutcomeUnknown);
        var (entry, cause, failure) = terminal.Members[0];
        if (failure == FailureDisposition.UnknownEffect) return Result(RemovalOutcome.Unknown, cause: cause ?? TextKey.FailDropboxOutcomeUnknown);
        if (cause is not null || failure is not null)
        {
            if (cause == TextKey.FailChanged && route == Delete && member.Id is not null && failure == FailureDisposition.Permanent && entry is null)
                return Result(RemovalOutcome.Unknown, cause: cause);
            if (cause == TextKey.ClashNameTaken) throw new PathOccupiedException(member.Destination ?? member.Path, Side.Dropbox);
            throw new ItemFailedException(member.Path, cause ?? TextKey.DropboxRefused, null) { Disposition = failure ?? FailureDisposition.Permanent };
        }
        if (entry is null || string.IsNullOrWhiteSpace(entry.Id) ||
            expected is not null && (entry.Id != expected.Id || entry.IsFolder != expected.IsFolder) ||
            route == Create && !entry.IsFolder ||
            route != Delete && !PathNames.Same(entry.Path, member.Destination ?? member.Path) ||
            route == Delete && (member.Id is null || entry.Id != member.Id))
            return Result(RemovalOutcome.Unknown, cause: TextKey.FailDropboxOutcomeUnknown);
        if (upload is not null && (entry.IsFolder || string.IsNullOrWhiteSpace(entry.Revision) ||
            entry.Size != upload.Total || !ContentHash.IsValid(entry.ContentHash) ||
            (upload.ContentHash ?? (string.IsNullOrEmpty(upload.Source.ContentHash) ? null : upload.Source.ContentHash)) is { } digest &&
            !string.Equals(digest, entry.ContentHash, StringComparison.OrdinalIgnoreCase)))
            return Result(RemovalOutcome.Unknown, cause: TextKey.FailDropboxOutcomeUnknown);
        return route == Delete ? Result(RemovalOutcome.Removed) : Result(RemovalOutcome.Published, entry);
    }
}
