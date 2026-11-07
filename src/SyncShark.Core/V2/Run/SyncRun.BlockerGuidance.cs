namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private string ReviewNoticeDeletion(PairDef pair, Notice notice)
    {
        var retry = _store.SyncRetryAt(pair.PairId, notice.Path);
        var lines = new List<string> { notice.Detail,
            "This is recorded evidence, not fresh deletion permission. Check history again schedules re-observation; existing holds and Recovery requirements remain in force." };
        if (retry is null) lines.Add("No per-item deletion proposal is recorded. A folder may be waiting for a child's history; no deletion is authorized here.");
        else
        {
            lines.Add("Pending reason: " + retry.LastError);
            lines.Add("State: " + retry.Kind + (retry.Held is not null ? "; deletion evidence is held" : ""));
            if (retry.ProposedActions is { } proposed)
            {
                foreach (var action in proposed.Take(50)) lines.Add(action.Kind + ": " + action.Path);
                if (proposed.Count > 50) lines.Add("Only the first 50 proposed actions are shown.");
                lines.Add("Use the separate deletion-review notice to Keep / restore, or to choose a verified Recovery-first deletion. Reviewing this text changes no authority.");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string ReviewNoticeReserve(Notice notice)
    {
        var space = notice.FreeSpace ?? throw new SyncRunRefusedException("Refresh this notice for current storage details.");
        return $"Last observed storage: {space.VolumeRoot ?? "unknown volume"}. Required: {space.RequiredBytes} bytes; free: {space.FreeBytes?.ToString() ?? "unknown"}; reserve: {space.ReserveBytes?.ToString() ?? "unknown"}. " +
            "The reserve protects local writes and the state database. Free storage or choose another Recovery/destination location, then Check space again. This action does not lower the reserve or delete any files.";
    }
}
