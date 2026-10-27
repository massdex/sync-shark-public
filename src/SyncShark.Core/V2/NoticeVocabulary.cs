namespace Massdex.SyncShark.V2;

/// <summary>Lower notice vocabulary shared by producers and the choice catalogue.</summary>
internal static class NoticeVocabulary
{
    internal const string LinksExcluded = "links-excluded";
    internal const string StubsLeftAlone = "stubs-left-alone";
    internal const string JournalNoticeKey = "journal-capacity";
    internal const string JournalEnlarge = "enlarge-journal";
    internal const string JournalNotNow = "journal-not-now";
    internal const string JournalNever = "journal-never";
    internal const string DefenderHeldCause = "held";
    internal const string DefenderSharedReleaseCause = "shared-release";
    internal const long HydrationConfirmationBytes = 256L * 1024 * 1024;
    internal const string DeleteSourceReasonKey = "delete-source-unconfirmed";
    internal const int RehashRule = 38;
    internal const string RehashReason = "local-rehash-pending";
    internal const string IndexFailurePrefix = "indexing-root-failure:";
    internal const string UncertainDeleteSummary = "uncertain-delete-summary";
    internal const string DeleteReviewSummary = "deletion-review-summary";
    internal const string BlockedDeleteSummary = "blocked-delete-summary";

    internal static bool IsJournalNotice(Notice notice) =>
        notice.ReasonKey.StartsWith(JournalNoticeKey + ":", StringComparison.Ordinal);
    internal static bool IsJournalChoice(string choice) => choice is JournalEnlarge or JournalNotNow or JournalNever;
}
