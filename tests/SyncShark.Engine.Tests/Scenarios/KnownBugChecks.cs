using System.Net;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;
using Codec = Massdex.SyncShark.Engine.Storage.Encoding;

namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>
/// CI regression checks for the open-bug list at
/// subagents/review/bugs-vs-rebuild-20261123/BUGS-VS-REBUILD.txt.
/// Each named check asserts the correct behaviour the bug report and the product decisions require,
/// so it fails while the bug exists and passes once it is fixed. Rows whose behaviour cannot be
/// observed faithfully on the F1 doubles, or only with real timing/provider/OS input, are stubbed
/// here (NI/DOUBLE) or registered as LIVE in the accompanying report. No assertion is weakened to pass.
/// </summary>
public static class KnownBugChecks
{
    // ---- Real assertions ------------------------------------------------

    /// <summary>B-207: with a saved binding and both inventories complete and empty, terminal absence is
    /// proven and the binding is forgotten through ordinary compare, not held and re-probed.</summary>
    public static Task B207BothSidesGoneAsync() =>
        ReconciliationChecks.CompareAsync(true, [], [], [], [], 0, []);

    /// <summary>B-223: an untagged HTTP 500 on upload-session start is a temporary server error to retry
    /// in place, not an attributed Dropbox refusal. F1 treats only 429 as transient (Dispatcher.cs:271-273)
    /// and otherwise leaves an unproved dispatched write as UnknownEffect with cause DropboxRefused
    /// (DropboxErrors.cs:91-102).</summary>
    public static Task B223Untagged500UploadSessionAsync()
    {
        using var body = JsonDocument.Parse("{}");
        var classified = new DropboxErrors().Classify(HttpStatusCode.InternalServerError, body.RootElement,
            DropboxProtocol.UploadSessionStart, expectedIsNull: false,
            new DropboxErrors.RequestEvidence(false, true, false, false));
        Check.Require(classified.Disposition == FailureDisposition.TemporaryNoEffect,
            $"Untagged 500 on upload_session/start settled as {classified.Disposition}/{classified.Cause}; " +
            "a temporary server error must retry in place, not be reported as a Dropbox refusal (product-0519).");
        return Task.CompletedTask;
    }

    /// <summary>B-230: current member subjects persist enum names and reject integer ordinals, so reordering
    /// an enum cannot silently reinterpret a stored subject (product BC8; Encoding.cs:52-94).</summary>
    public static Task B230ReorderedEnumRejectsOrdinalsAsync()
    {
        var subject = new IssueSubject(new[] { new NoticeItem(Side.Dropbox, "a", "i") }, null);
        var encoded = Codec.Envelope(subject);
        Check.Require(encoded.Contains("\"Dropbox\"", StringComparison.Ordinal) &&
            !encoded.Contains("\"Side\":1", StringComparison.Ordinal),
            "The member envelope must persist enum names, not ordinals that a reorder would reinterpret");
        var decoded = Codec.DecodeMemberSubject(encoded);
        Check.Require(decoded.Items.Single().Side == Side.Dropbox, "Named-enum member envelope did not round trip");
        try { Codec.DecodeMemberSubject("""{"Version":1,"Value":{"Items":[{"Side":1,"Path":"a","Id":"i"}],"Incoming":null}}"""); }
        catch (InvalidDataException) { return Task.CompletedTask; }
        throw new InvalidDataException("A numeric enum ordinal was accepted for a current member subject");
    }

    /// <summary>L122: an extremely long provider Retry-After is waited in supported timer chunks and stays
    /// cancellable, instead of overflowing the delay API (EngineSettings.cs BackoffSettings.WaitAsync).</summary>
    public static async Task L122LongRetryAfterAsync()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await ItemChecks.Settings.Backoff.WaitAsync(TimeSpan.FromDays(3650), TimeProvider.System, cancelled.Token);
            throw new InvalidDataException("A long provider Retry-After did not observe cancellation");
        }
        catch (OperationCanceledException) { }
    }

    // ---- NI / DOUBLE stubs with the gap ---------------------------------
    // NotImplementedException is reported as NOT IMPLEMENTED by the runner; the message names why the
    // doubles or the product cannot yet carry the assertion. Live-only rows carry no registered check.

    public static Task B200TempReplaceBudgetAsync() => throw new NotImplementedException(
        "DOUBLE: temp-and-replace refusal call budget needs a change source that scripts a temp-file write " +
        "followed by a rename-over; the doubles expose only terminal entries, so the superseded remote read cannot be counted.");

    public static Task B211MalformedFrameListenerAsync() => throw new NotImplementedException(
        "DOUBLE: the malformed-frame-then-good-client listener check needs an authenticated named-pipe host " +
        "(HostServer.AuthenticatedPeer requires the signed product image), which the doubles cannot provide.");

    public static Task B212ReverseFolderMoveAsync() => throw new NotImplementedException(
        "DOUBLE: the reverse-move-plus-placeholder fixture needs folder rename history both ways with a placeholder " +
        "create; the doubles seed terminal entries only.");

    public static Task B213StaleFolderBindingAsync() => throw new NotImplementedException(
        "DOUBLE: a stale folder binding that authorizes delete needs a saved identity whose path has moved while the " +
        "original folder is alive elsewhere; the in-memory store keys bindings by path.");

    public static Task B214CWakeBehindEchoAsync() => throw new NotImplementedException(
        "DOUBLE: non-starvation behind a folder-move echo backlog needs continuous echo pages; " +
        "F1 CloudEntryEcho/CloudTombstoneEcho are stubs.");

    public static Task B229ScopedJournalRepairAsync() => throw new NotImplementedException(
        "NI: scoped Repair for a verified isolatable journal loss is not implemented (Monitor.cs only separates a " +
        "verified reset from a failed read); there is no scoped-recovery path to assert.");

    public static Task B231UnrelatedEffectKeepsMoveNoticeAsync() => throw new NotImplementedException(
        "DOUBLE: the unrelated-effect notice protection lives in SQLite NoticeTable; the in-memory double keeps a " +
        "single slot by path/kind and cannot reproduce the mapped-move subject rejection (NoticeTable.cs:119).");

    public static Task B240ArrivalFloodOpportunityAsync() => throw new NotImplementedException(
        "DOUBLE: an arrival flood with small unrelated arrivals needs per-change opportunity accounting that the " +
        "change-page doubles do not expose.");

    public static Task B250CapacityWaitCancellationAsync() => throw new NotImplementedException(
        "DOUBLE: injected mid-wait cancellation followed by another active pair needs concurrent pair execution, " +
        "which the single-pair doubles do not model.");

    public static Task B255PathOnlyDeleteUnderRenameAsync() => throw new NotImplementedException(
        "DOUBLE: path-only delete under a renamed folder needs the mapped-range-before-repath acquisition; " +
        "F1 MappedRangeBeforeRepathAsync is a stub.");

    public static Task L60FolderRemovalSaturationAsync() => throw new NotImplementedException(
        "DOUBLE: pending folder removals occupying every continuation slot needs a bounded continuation pool; " +
        "the doubles run removals serially.");

    public static Task L61ChildInUseNoticeAfterParentAsync() => throw new NotImplementedException(
        "DOUBLE: a child in-use notice surviving parent removal needs a folder delete that reports an in-use child; " +
        "the FakeSide delete always succeeds.");

    public static Task L64ChurnOpportunityAsync() => throw new NotImplementedException(
        "DOUBLE: sustained churn starvation needs replenished refused work across attempts; the double pages are finite.");

    public static Task L66SqlOrdinalsAndParserDepthsAsync() => throw new NotImplementedException(
        "DOUBLE: hand-coded SQL ordinals and provider parser depths need the real SQLite NoticeTable and a reordered " +
        "provider payload; the in-memory double cannot express either.");

    public static Task L84FolderScaleLookupsAsync() => throw new NotImplementedException(
        "DOUBLE: folder-scale account/descendant read counts need echo/mapped-range acquisition; " +
        "the F1 echo and mapped-range methods are stubs.");

    public static Task L87ScopeChangeAtIntakeAsync() => throw new NotImplementedException(
        "DOUBLE: a scope change taking effect at intake needs a live scope mutation between pages; " +
        "the doubles seed a fixed PathScope.");

    public static Task L98MissingMappedParentChildAsync() => throw new NotImplementedException(
        "DOUBLE: a bound-elsewhere/missing mapped parent with a child arriving before it needs AutoSync intake; " +
        "the doubles seed terminal entries.");

    public static Task L101ExcludedDescendantFloodAsync() => throw new NotImplementedException(
        "DOUBLE: the excluded-descendant read flood after a folder move needs tombstone routing under exclusion; " +
        "the doubles do not script tombstones for excluded ranges.");

    public static Task L102MissingRootPointerAsync() => throw new NotImplementedException(
        "NI: missing product-root pointer recovery is not implemented at F1 (RecoveryChecks and scenario proof incomplete).");

    public static Task L107AcceptedJobPollingAsync() => throw new NotImplementedException(
        "DOUBLE: evidence-based accepted-job checking without fixed polling needs a live job feed; " +
        "the doubles script discrete job replies.");

    public static Task L109CrashAfterMoveBeforeSaveAsync() => throw new NotImplementedException(
        "NI: crash-after-move-before-save replay needs the crash-cut scenarios that remain stubs at F1.");

    public static Task L110RetryInUseWrongSideAsync() => throw new NotImplementedException(
        "DOUBLE: retry of an in-use failure targeting the wrong side needs a side-specific in-use failure from a real " +
        "Local side; the FakeSide delete never reports in-use.");

    public static Task L112HighChurnTreeAsync() => throw new NotImplementedException(
        "DOUBLE: a selected high-churn nested tree syncing without persistent refusals needs sustained churn; " +
        "the doubles are finite.");

    public static Task L116MissingDeletionEvidenceAsync() => throw new NotImplementedException(
        "DOUBLE: reporting every missing-deletion-evidence reason needs the real deletion-history store with absent " +
        "evidence; the in-memory store cannot express partial history.");

    public static Task L117AutoSyncTouchesChildDuringRepairMoveAsync() => throw new NotImplementedException(
        "DOUBLE: AutoSync touching a child while Repair moves its parent needs concurrent pair execution; " +
        "the doubles are single-threaded.");

    public static Task L119CrashAfterExclusionRemovalAsync() => throw new NotImplementedException(
        "DOUBLE: crash after a no-keep exclusion before commit needs a crash cut at the exclusion path; " +
        "not modelled on the doubles.");
}
