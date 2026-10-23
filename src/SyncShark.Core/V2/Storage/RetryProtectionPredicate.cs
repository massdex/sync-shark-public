namespace Massdex.SyncShark.V2;

/// <summary>The one eligibility rule for retry protection, shared by live and pinned SQLite reads.</summary>
internal static class RetryProtectionPredicate
{
    internal static bool Includes(SyncRetry retry, bool restoredOnly = false) =>
        (!retry.ReprobeEvidence || retry.RetryAfter < DateTimeOffset.MaxValue) &&
        (!restoredOnly || retry.ReprobeEvidence);

    internal static string Sql(bool restoredOnly = false) =>
        " AND NOT (retry_after=" + DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() +
        " AND COALESCE(json_extract(json,'$.ReprobeEvidence'),0)=1)" +
        (restoredOnly ? " AND json_extract(json,'$.ReprobeEvidence')=1" : "");
}
