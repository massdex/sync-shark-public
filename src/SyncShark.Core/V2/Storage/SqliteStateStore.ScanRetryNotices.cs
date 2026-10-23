namespace Massdex.SyncShark.V2;

/// <summary>The one pair-level notice that stands for many transient online-only observations.</summary>
internal enum ObservationSummaryState { None, Open, Ignored }

public sealed partial class SqliteStateStore
{
    /// <summary>Whether the pair currently shows, suppressed, or does not have the transient summary. The scan
    /// uses this to avoid deaggregating into a per-file flood or dropping unresolved failures from view.</summary>
    internal ObservationSummaryState OwnedPointerObservationSummaryState(string pairId) => InStore(() =>
    {
        var pair = RequirePair(pairId);
        if (StoreSchema.Scalar(_connection, null, $"""
            SELECT 1 FROM notice INDEXED BY nt_pair_reason
            WHERE pair_no=$p AND reason_key=$reason AND path='' AND {NoticeTable.ActionableSql("state")}
              AND COALESCE(choice,'')<>'ignore' LIMIT 1;
            """, ("$p", pair), ("$reason", NoticeChoices.PointerObservationSummary)) is not null)
            return ObservationSummaryState.Open;
        return StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM notice INDEXED BY nt_pair_reason WHERE pair_no=$p AND reason_key=$reason AND path='' AND choice='ignore' LIMIT 1;",
            ("$p", pair), ("$reason", NoticeChoices.PointerObservationSummary)) is not null
            ? ObservationSummaryState.Ignored : ObservationSummaryState.None;
    });

    /// <summary>Makes every durable transient-observation retry due once, without touching attempts or other
    /// retry kinds. One statement: an explicit pair Retry must not become a million read/write round trips.</summary>
    internal void RequestOwnedPointerObservationRetriesNow(string pairId, string observation) => InStore(() =>
    {
        var pair = RequirePair(pairId);
        // Only ordinary scan retries whose LastError starts with this exact transient message: a bespoke row or
        // an unrelated cause is never made due. The ISO text is unquoted, so json_set stores one JSON string.
        StoreSchema.Execute(_connection, null, """
            UPDATE sync_retry SET retry_after=$due, json=json_set(json,'$.RetryAfter',$iso)
            WHERE pair_no=$p AND json_extract(json,'$.Kind')='scan'
              AND substr(json_extract(json,'$.LastError'),1,$length)=$needle;
            """, ("$p", pair), ("$due", DateTimeOffset.MinValue.ToUnixTimeMilliseconds()),
            ("$iso", DateTimeOffset.MinValue.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            ("$length", observation.Length), ("$needle", observation));
    });

    /// <summary>Closes the per-file transient notices that one pair summary replaces. The durable sync_retry rows
    /// stay untouched, so retirement and protection behave exactly as before.</summary>
    internal void CloseOwnedPointerObservationNotices(string pairId, string observation) => InStore(() =>
    {
        var pair = RequirePair(pairId);
        _notices.CloseWhere(null, "resolved", $"""
            pair_no=$p AND rule=40 AND reason_key='sync-retry' AND path<>'' AND {NoticeTable.ActionableSql("state")}
              AND substr(detail,1,$length)=$needle;
            """, ("$p", pair), ("$length", observation.Length), ("$needle", observation));
    });

    /// <summary>Closes the one pair-level transient summary once a scan reports no transient failure at all, so a
    /// successful next run clears it without deaggregating into per-file notices.</summary>
    internal void CloseOwnedPointerObservationSummary(string pairId) => InStore(() =>
    {
        var pair = RequirePair(pairId);
        _notices.CloseWhere(null, "resolved", $"""
            pair_no=$p AND rule=40 AND reason_key=$reason AND path='' AND {NoticeTable.ActionableSql("state")} ;
            """, ("$p", pair), ("$reason", NoticeChoices.PointerObservationSummary));
    });

    /// <summary>The user's Ignore outlives only the failures it silenced. Once a scan observes no transient
    /// failure the ignored summary is retired, so a later, unrelated failure raises an actionable notice again
    /// instead of staying suppressed forever. The ignored row is closed as expired, never reopened here.</summary>
    internal void ExpireIgnoredOwnedPointerObservationSummary(string pairId) => InStore(() =>
    {
        var pair = RequirePair(pairId);
        _notices.CloseWhere(null, "expired", """
            pair_no=$p AND rule=40 AND reason_key=$reason AND path='' AND choice='ignore';
            """, ("$p", pair), ("$reason", NoticeChoices.PointerObservationSummary));
    });
}
