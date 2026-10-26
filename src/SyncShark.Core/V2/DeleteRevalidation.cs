namespace Massdex.SyncShark.V2;

internal static class DeleteRevalidation
{
    internal static async Task RevalidateCurrentAsync(SyncAction? action)
    {
        if (action?.Kind != ActionKind.Delete) return;
        var context = ProviderEffectContext.Current.Value;
        var matches = context?.Action?.ActionId == action.ActionId;
        var observation = matches && context?.RevalidateDelete is { } check
            ? await check().ConfigureAwait(false) : new AbsenceObservation(null, "No delete recheck supplied.");
        if (observation.Absent == true) return;
        var refusal = matches && context?.RefuseDelete is { } refuse ? await refuse(observation).ConfigureAwait(false) : null;
        throw new DeleteSourceUnconfirmedException(refusal ?? new ActionResult(action.ActionId,
            Outcome.Skipped, null, null, null, observation.Reason) { DeleteSourceUnconfirmed = true });
    }

}
