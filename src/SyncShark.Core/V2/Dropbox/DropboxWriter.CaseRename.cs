using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class DropboxWriter
{
    internal const string CaseRenameCheckpointKind = "case-rename";
    private sealed record CaseRenameCheckpoint(string From, string Temporary, string To, int Step);

    public bool CanResumeProviderEffect(SyncAction action) => action.ProviderEffect?.Kind == CaseRenameCheckpointKind;

    private async Task<bool?> ResumeCaseAsync(SyncAction action, CancellationToken ct)
    {
        var checkpoint = JsonSerializer.Deserialize<CaseRenameCheckpoint>(action.ProviderEffect!.Id)
            ?? throw new InvalidDataException("Invalid case rename checkpoint.");
        try
        {
            var located = await _client.FindIdentityForMoveAsync(action.ExpectedTarget!.ObjectId, ct).ConfigureAwait(false);
            if (located.OutsideRoot || located.Entry is null) return false;
            var current = DropboxItems.ToItem(located.Entry);
            if (DropboxPilotClient.SameMoveSpelling(current.Path, checkpoint.To)) return true;
            if (!DropboxPilotClient.SameMoveSpelling(current.Path, checkpoint.From) &&
                !DropboxPilotClient.SameMoveSpelling(current.Path, checkpoint.Temporary)) return false;
            // A native rename carries the latest content; retain the conditional revision guard
            // against this newly observed revision rather than an obsolete pre-interruption revision.
            await MoveCaseAsync(action with { ExpectedTarget = current }, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (FailureRetryOwner.ScopeOf(error, ct) == FailureRetryOwner.FailureScope.Item)
        {
            throw new UncertainEffectException("Case rename identity lookup/resume failed. Last retained locations: " +
                checkpoint.From + " or " + checkpoint.Temporary + "; destination: " + checkpoint.To + ". " + error.Message, error);
        }
    }

    // Dropbox's files.stone move:2 explicitly does not support case-only renaming.
    // Each leg is an ordinary identity-preserving move, with its intent durable first.
    private async Task<ItemState> MoveCaseAsync(SyncAction action, CancellationToken ct)
    {
        var context = ProviderEffectContext.Current.Value ??
            throw new InvalidOperationException("A case rename requires a durable provider context.");
        var checkpoint = action.ProviderEffect is { Kind: CaseRenameCheckpointKind } saved
            ? JsonSerializer.Deserialize<CaseRenameCheckpoint>(saved.Id) ?? throw new InvalidDataException("Invalid case rename checkpoint.")
            : new CaseRenameCheckpoint(action.FromPath!, TemporaryCasePath(action), action.Path, 0);
        if (checkpoint.From != action.FromPath || checkpoint.To != action.Path)
            throw new InvalidDataException("Case rename checkpoint does not match its intent.");
        var expected = action.ExpectedTarget!;
        var destination = await ObserveAsync(checkpoint.To, ct).ConfigureAwait(false);
        if (destination is not null && destination.ObjectId != expected.ObjectId)
            throw new ExpectedTargetMismatchException("The case rename destination is occupied by another identity.");
        if (destination is not null && DropboxPilotClient.SameMoveSpelling(destination.Path, checkpoint.To))
            return destination;
        var temporary = await ObserveAsync(checkpoint.Temporary, ct).ConfigureAwait(false);
        if (temporary is not null && temporary.ObjectId != expected.ObjectId)
            throw new ExpectedTargetMismatchException("The case rename intermediate name is occupied by another identity.");
        if (temporary is null)
        {
            var original = await ObserveAsync(checkpoint.From, ct).ConfigureAwait(false);
            if (original is null || original.ObjectId != expected.ObjectId || !DropboxPilotClient.SameMoveSpelling(original.Path, checkpoint.From))
                throw new ExpectedTargetMismatchException("The case rename source identity or spelling changed.");
            await context.RecordAsync(CaseRenameCheckpointKind,
                JsonSerializer.Serialize(checkpoint with { Step = 1 })).ConfigureAwait(false);
            temporary = DropboxItems.ToItem(await MoveCaseLegAsync(
                DropboxItems.ToEntry(expected with { Path = checkpoint.From }), checkpoint.Temporary,
                action.RequireMoveRevision, ct).ConfigureAwait(false));
        }
        await context.RecordAsync(CaseRenameCheckpointKind,
            JsonSerializer.Serialize(checkpoint with { Step = 2 })).ConfigureAwait(false);
        return DropboxItems.ToItem(await MoveCaseLegAsync(
            DropboxItems.ToEntry(expected with { Path = checkpoint.Temporary }), checkpoint.To,
            action.RequireMoveRevision, ct).ConfigureAwait(false));
    }

    private async Task<PilotRemoteEntry> MoveCaseLegAsync(PilotRemoteEntry expected, string destination,
        bool requireRevision, CancellationToken ct)
    {
        try { return await _client.MoveRevisionAsync(expected, destination, requireRevision, ct).ConfigureAwait(false); }
        catch (Exception error) when (FailureRetryOwner.ScopeOf(error, ct) == FailureRetryOwner.FailureScope.Item)
        {
            throw new UncertainEffectException("The case rename checkpoint requires reconciliation: " + error.Message, error);
        }
    }

    private static string TemporaryCasePath(SyncAction action)
    {
        var from = action.FromPath ?? throw new ArgumentException("A case rename requires its original path.", nameof(action));
        var parent = from[..(from.LastIndexOf('/') + 1)];
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(action.ActionId)))[..24].ToLowerInvariant();
        return parent + LocalItems.CaseRenameArtifactPrefix + suffix;
    }
}
