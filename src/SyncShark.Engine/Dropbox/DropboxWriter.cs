using static Massdex.SyncShark.Engine.Dropbox.DropboxProtocol;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Submits the one selected effect and classifies only that member's receipt.</summary>
public sealed class DropboxWriter
{
    /// <summary>Publication keeps the selected revision and never resends an uncertain write.</summary>
    public Task<RemovalResult> UploadAsync(PairConfig pair, Dispatcher dispatcher, DropboxStaged stage, Entry? expected, CancellationToken ct)
    {
        if (expected is not null && (expected.IsFolder || string.IsNullOrWhiteSpace(expected.Id) || string.IsNullOrEmpty(expected.Revision) || !PathNames.Same(stage.Path, expected.Path)))
            throw new ChangedUnderneathException(stage.Path);
        return SubmitAsync(pair, dispatcher, stage.ConsumesSourceOnPublish ? Upload : UploadSessionFinish,
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
        return Classify(route, member, expected, accepted, reply);
    }

    /// <summary>Classifies an already attributed single-member reply; no observation starts a second request.</summary>
    private static RemovalResult Classify(string route, JobMember member, Entry? expected,
        AcceptedJob? accepted, JobReply reply)
    {
        RemovalResult Result(RemovalOutcome outcome, Entry? entry = null, TextKey? cause = null) =>
            new(outcome, entry, cause) { Accepted = accepted, Reply = reply };
        if (accepted is not null && reply is JobReply.Running) return Result(RemovalOutcome.Pending);
        if (reply is not JobReply.Terminal { Members.Count: 1 } terminal) return Result(RemovalOutcome.Unknown, cause: TextKey.FailDropboxOutcomeUnknown);
        var (entry, cause, failure) = terminal.Members[0];
        // An unresolved delete keeps the unknown cause durably so a later Repair or Retry steps aside; the reply keeps the provider's cause.
        if (failure == FailureDisposition.UnknownEffect) return Result(RemovalOutcome.Unknown, cause: route == Delete && terminal.AbsentOrdinals.Count == 0 ? TextKey.FailDropboxOutcomeUnknown : cause ?? TextKey.FailDropboxOutcomeUnknown);
        if (cause is not null || failure is not null)
        {
            if (route == Move && cause == TextKey.FailDropboxTooManyFiles && failure == FailureDisposition.Permanent &&
                entry is null && expected is { IsFolder: true } && !string.IsNullOrEmpty(expected.Id) && member.Id == expected.Id &&
                PathNames.Same(member.Path, expected.Path) && !string.IsNullOrEmpty(member.Destination))
                return Result(RemovalOutcome.NoEffect, expected, cause);
            if (cause == TextKey.ClashNameTaken) throw new PathOccupiedException(member.Destination ?? member.Path, Side.Dropbox);
            throw new ItemFailedException(member.Path, cause ?? TextKey.DropboxRefused, null) { Disposition = failure ?? FailureDisposition.Permanent };
        }
        if (entry is null || string.IsNullOrWhiteSpace(entry.Id) ||
            expected is not null && (entry.Id != expected.Id || entry.IsFolder != expected.IsFolder) ||
            route == CreateFolder && !entry.IsFolder ||
            route != Delete && !PathNames.Same(entry.Path, member.Destination ?? member.Path) ||
            route == Delete && (member.Id is null || entry.Id != member.Id))
            return Result(RemovalOutcome.Unknown, cause: TextKey.FailDropboxOutcomeUnknown);
        return route == Delete ? Result(RemovalOutcome.Removed) : Result(RemovalOutcome.Published, entry);
    }
}
