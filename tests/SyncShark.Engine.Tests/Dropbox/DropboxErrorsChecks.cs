using System.Net;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for DropboxErrors.</summary>
public static class DropboxErrorsChecks
{
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement Union(string tag) => Element(new Dictionary<string, object>
        { [DropboxProtocol.Tag] = tag });

    private static JsonElement Tagged(string outer, string inner) => Element(new Dictionary<string, object>
    {
        [DropboxProtocol.Tag] = outer,
        [outer] = new Dictionary<string, object> { [DropboxProtocol.Tag] = inner }
    });

    private static JsonElement Reply(string summary, JsonElement failure) => Element(new Dictionary<string, object>
    {
        [DropboxProtocol.ErrorSummary] = summary,
        [DropboxProtocol.Error] = failure
    });

    private static JsonElement SummaryOnly(string summary) => Element(new Dictionary<string, object>
        { [DropboxProtocol.ErrorSummary] = summary });

    private static JsonElement EmptyBody() => Element(new Dictionary<string, object>());

    // An upload/finish WriteError (session-finish shape: nested under path).
    private static JsonElement WriteReason(string reason) => Element(new Dictionary<string, object>
    {
        [DropboxProtocol.Tag] = DropboxProtocol.Path,
        [DropboxProtocol.Path] = Union(reason)
    });

    // A well-formed conflict: {.tag: path, path: {.tag: conflict, conflict: {.tag: file}}}.
    private static JsonElement WriteConflict() => Element(new Dictionary<string, object>
    {
        [DropboxProtocol.Tag] = DropboxProtocol.Path,
        [DropboxProtocol.Path] = new Dictionary<string, object>
        {
            [DropboxProtocol.Tag] = DropboxProtocol.Conflict,
            [DropboxProtocol.Conflict] = Union(DropboxProtocol.TagFile)
        }
    });

    private static JsonElement MoveError(string inner) => Tagged(DropboxProtocol.RelocationError, inner);

    /// <summary>Verifies the DropboxErrors contract through the module surface.</summary>
    public static Task RunAsync()
    {
        var errors = new DropboxErrors();
        var readTransient = new DropboxErrors.RequestEvidence(Read: true, Dispatched: true, ProvenNoEffect: true, Transient: true);
        var readPermanent = new DropboxErrors.RequestEvidence(true, true, true, false);
        var writeDispatched = new DropboxErrors.RequestEvidence(false, true, false, true);
        var writeNoEffect = new DropboxErrors.RequestEvidence(false, true, true, false);
        var writeTransient = new DropboxErrors.RequestEvidence(false, true, true, true);

        Absence(errors);
        SingleRefusals(errors);
        UnknownWrites(errors, writeDispatched, writeNoEffect, writeTransient);
        TransientAndContention(errors, readTransient, readPermanent, writeDispatched);
        BatchMembers(errors);
        return Task.CompletedTask;
    }

    // Finding 1: absence needs a usable observation; a prefix or a contradictory structured refusal is not absence.
    private static void Absence(DropboxErrors errors)
    {
        Check.Require(errors.Classify(HttpStatusCode.Conflict, SummaryOnly(DropboxProtocol.PathNotFound),
                DropboxProtocol.GetMetadata, false, default).Absent, "An exact path/not_found summary is absence");
        Check.Require(errors.Classify(HttpStatusCode.Conflict, Reply(string.Empty,
                Tagged(DropboxProtocol.Path, "not_found")), DropboxProtocol.GetMetadata, false, default).Absent,
            "An exact structured path/not_found is absence");
        Check.Require(!errors.Classify(HttpStatusCode.Conflict, SummaryOnly(DropboxProtocol.PathNotFound + "_extra"),
                DropboxProtocol.GetMetadata, false, default).Absent, "A prefixed not_found is not absence");
        Check.Require(!errors.Classify(HttpStatusCode.Conflict, Reply(DropboxProtocol.PathNotFound,
                Tagged(DropboxProtocol.Path, "not_file")), DropboxProtocol.GetMetadata, false, default).Absent,
            "The summary cannot override a contradictory structured refusal");
        Check.Require(!errors.Classify(HttpStatusCode.Conflict, Reply(string.Empty,
                Tagged(DropboxProtocol.Path, "not_file")), DropboxProtocol.GetMetadata, false, default).Absent,
            "Only the structured not_found tag is absence");
        Check.Require(!errors.Classify(HttpStatusCode.Conflict, Reply(DropboxProtocol.PathNotFound,
                Union(DropboxProtocol.OperationSuppressed)), DropboxProtocol.GetMetadata, false, default).Absent,
            "A structured non-path refusal contradicts the absence summary");
    }

    private static void SingleRefusals(DropboxErrors errors)
    {
        // The same upload conflict is a taken name on add and a changed target on update.
        var conflict = Reply(DropboxProtocol.PathConflict + "/file", WriteConflict());
        var add = errors.Classify(HttpStatusCode.Conflict, conflict, DropboxProtocol.Upload, true, default);
        var update = errors.Classify(HttpStatusCode.Conflict, conflict, DropboxProtocol.Upload, false, default);
        Check.Require(add.Cause == TextKey.ClashNameTaken && add.Disposition == FailureDisposition.Permanent &&
            add.ProvenNoEffect, "An add conflict is a taken name");
        Check.Require(update.Cause == TextKey.FailChanged && update.Disposition == FailureDisposition.Permanent,
            "An update-by-revision conflict is a changed target");

        // The direct-upload WriteError nests the reason under path.reason and must decode the same way.
        var direct = Element(new Dictionary<string, object>
        {
            [DropboxProtocol.Tag] = DropboxProtocol.Path,
            [DropboxProtocol.Reason] = new Dictionary<string, object>
            {
                [DropboxProtocol.Tag] = DropboxProtocol.Conflict,
                [DropboxProtocol.Conflict] = Union(DropboxProtocol.TagFolder)
            }
        });
        Check.Require(errors.Classify(HttpStatusCode.Conflict, Reply(string.Empty, direct), DropboxProtocol.Upload,
                true, default).Cause == TextKey.ClashNameTaken, "A direct-upload conflict decodes like session finish");

        var noSpace = errors.Classify(HttpStatusCode.Conflict,
            Reply(DropboxProtocol.UploadWritePrefix + DropboxProtocol.InsufficientSpace,
                WriteReason(DropboxProtocol.InsufficientSpace)), DropboxProtocol.Upload, true, default);
        Check.Require(noSpace.Cause == TextKey.FailDropboxNoSpace && noSpace.ProvenNoEffect,
            "A documented write reason maps to its cause");

        var oversized = errors.Classify(HttpStatusCode.Conflict, SummaryOnly(DropboxProtocol.TooManyFiles),
            DropboxProtocol.Move, false, default);
        Check.Require(oversized.Cause == TextKey.FailDropboxTooManyFiles && oversized.ProvenNoEffect &&
            oversized.Disposition == FailureDisposition.Permanent, "Too many files is an attributable refusal");

        var gone = errors.Classify(HttpStatusCode.Conflict, SummaryOnly(DropboxProtocol.PathLookupNotFound),
            DropboxProtocol.Delete, false, default);
        Check.Require(gone.Cause == TextKey.FailChanged && gone.ProvenNoEffect, "A delete lookup miss is attributable");
        Check.Require(errors.Classify(HttpStatusCode.Conflict,
                SummaryOnly(DropboxProtocol.PathLookupNotFound + "_extra"), DropboxProtocol.Delete, false,
                new DropboxErrors.RequestEvidence(false, true, false, true)).Disposition ==
            FailureDisposition.UnknownEffect, "A prefixed delete miss on a dispatched write is not a refusal");

        Check.Require(errors.Classify(HttpStatusCode.Conflict, SummaryOnly(DropboxProtocol.PathNotFound)) ==
            FailureDisposition.Permanent, "The frozen two-argument surface still answers a disposition");
    }

    // Finding 2: an unproved dispatched write stays unknown, never a definite refusal or a no-effect claim.
    private static void UnknownWrites(DropboxErrors errors, DropboxErrors.RequestEvidence writeDispatched,
        DropboxErrors.RequestEvidence writeNoEffect, DropboxErrors.RequestEvidence writeTransient)
    {
        Check.Require(errors.Classify(HttpStatusCode.Conflict, EmptyBody(), DropboxProtocol.Upload, true,
                writeDispatched).Disposition == FailureDisposition.UnknownEffect, "An empty 409 on a write is unknown");
        var unknownBody = errors.Classify(HttpStatusCode.Conflict, Reply(string.Empty, Union("future_error")),
            DropboxProtocol.Upload, true, writeDispatched);
        Check.Require(unknownBody.Disposition == FailureDisposition.UnknownEffect && !unknownBody.ProvenNoEffect,
            "An unrecognized 409 refusal is unknown");

        // A dispatched write the caller proved had no effect is not unknown; transient evidence makes it temporary.
        var proved = errors.Classify(HttpStatusCode.ServiceUnavailable, default, DropboxProtocol.Upload, true,
            writeNoEffect);
        Check.Require(proved.Disposition == FailureDisposition.Permanent && proved.ProvenNoEffect,
            "A proved no-effect non-transient write is permanent");
        Check.Require(errors.Classify(HttpStatusCode.RequestTimeout, default, DropboxProtocol.Upload, true,
                writeTransient).Disposition == FailureDisposition.TemporaryNoEffect,
            "A proved no-effect transient write is temporary");
        Check.Require(errors.Classify(HttpStatusCode.ServiceUnavailable, default, DropboxProtocol.Upload, true,
                writeDispatched).Disposition == FailureDisposition.UnknownEffect,
            "A dispatched write without proof is unknown");
    }

    // Finding 3: documented write contention takes the write rule on any status; a transient read is temporary.
    private static void TransientAndContention(DropboxErrors errors, DropboxErrors.RequestEvidence readTransient,
        DropboxErrors.RequestEvidence readPermanent, DropboxErrors.RequestEvidence writeDispatched)
    {
        var contention = errors.Classify(HttpStatusCode.TooManyRequests, SummaryOnly(DropboxProtocol.TooManyWriteOperations),
            DropboxProtocol.Upload, true, writeDispatched);
        Check.Require(contention.Cause == TextKey.WaitTooManyWrites &&
            contention.Disposition == FailureDisposition.TemporaryNoEffect && contention.ProvenNoEffect,
            "A too_many_write_operations 429 takes the write rule");
        var structured = errors.Classify(HttpStatusCode.ServiceUnavailable,
            Reply(string.Empty, Union(DropboxProtocol.TooManyWriteOperations)), DropboxProtocol.Delete, false, default);
        Check.Require(structured.Cause == TextKey.WaitTooManyWrites &&
            structured.Disposition == FailureDisposition.TemporaryNoEffect, "A structured write throttle on 503 is contention");

        Check.Require(errors.Classify(HttpStatusCode.ServiceUnavailable, default, DropboxProtocol.GetMetadata, false,
                readTransient).Disposition == FailureDisposition.TemporaryNoEffect, "A transient read is temporary");
        Check.Require(errors.Classify(HttpStatusCode.InternalServerError, default, DropboxProtocol.GetMetadata, false,
                readPermanent).Disposition == FailureDisposition.Permanent, "A decided 500 read stays a failure");
    }

    private static void BatchMembers(DropboxErrors errors)
    {
        // A move member: an inner attributable refusal is no-effect; an unknown one is UnknownEffect.
        var moveOversized = errors.ClassifyMember(MoveError(DropboxProtocol.TooManyFiles), DropboxProtocol.MoveBatch, false);
        Check.Require(moveOversized.Cause == TextKey.FailDropboxTooManyFiles && moveOversized.ProvenNoEffect &&
            moveOversized.Disposition == FailureDisposition.Permanent, "A move too_many_files member is attributable");
        var moveContention = errors.ClassifyMember(Union(DropboxProtocol.TooManyWriteOperations), DropboxProtocol.MoveBatch, false);
        Check.Require(moveContention.Cause == TextKey.WaitTooManyWrites &&
            moveContention.Disposition == FailureDisposition.TemporaryNoEffect, "A move contention member is temporary");
        Check.Require(errors.ClassifyMember(MoveError("future_relocation"), DropboxProtocol.MoveBatch, false)
            .Disposition == FailureDisposition.UnknownEffect, "An unknown move tag proves nothing");

        // A delete member's effect stays Unknown; only the exact structured by-id path_lookup/not_found proves the
        // selected identity gone (Absent), which is not the same as "our request deleted it".
        var deleteMiss = errors.ClassifyMember(Tagged(DropboxProtocol.PathLookup, "not_found"),
            DropboxProtocol.DeleteBatch, false);
        Check.Require(deleteMiss.Absent && deleteMiss.Cause == TextKey.FailChanged &&
            deleteMiss.Disposition == FailureDisposition.UnknownEffect && !deleteMiss.ProvenNoEffect,
            "A by-id delete not_found proves the identity gone, not our effect");
        Check.Require(!errors.ClassifyMember(Tagged(DropboxProtocol.PathLookup, "not_file"),
            DropboxProtocol.DeleteBatch, false).Absent, "A delete lookup tag other than not_found is not absence");
        var deleteNoSpace = errors.ClassifyMember(Tagged(DropboxProtocol.PathWrite, DropboxProtocol.InsufficientSpace),
            DropboxProtocol.DeleteBatch, false);
        Check.Require(deleteNoSpace.Disposition == FailureDisposition.UnknownEffect && !deleteNoSpace.Absent,
            "A delete write reason proves no outcome and is not absence");

        // An upload member: the member's original request context (expectedIsNull = create-only vs revision-bound)
        // picks add vs update; unknown/malformed stays Unknown.
        Check.Require(errors.ClassifyMember(WriteConflict(), DropboxProtocol.UploadSessionFinishBatch, true).Cause ==
            TextKey.ClashNameTaken, "An upload add conflict member is a taken name");
        Check.Require(errors.ClassifyMember(WriteConflict(), DropboxProtocol.UploadSessionFinishBatch, false).Cause ==
            TextKey.FailChanged, "An upload update conflict member is a changed target");
        var incomplete = errors.ClassifyMember(WriteReason(DropboxProtocol.Conflict),
            DropboxProtocol.UploadSessionFinishBatch, true);
        Check.Require(incomplete.Disposition == FailureDisposition.UnknownEffect && !incomplete.ProvenNoEffect,
            "A conflict without its required subtype proves nothing");
        Check.Require(errors.ClassifyMember(WriteReason("future_write_error"), DropboxProtocol.UploadSessionFinishBatch, true)
            .Disposition == FailureDisposition.UnknownEffect, "An unknown nested write tag proves nothing");
        Check.Require(errors.ClassifyMember(Union("future_upload_error"), DropboxProtocol.UploadSessionFinishBatch, true)
            .Disposition == FailureDisposition.UnknownEffect, "An unknown upload tag proves nothing");
        Check.Require(errors.ClassifyMember(Union(DropboxProtocol.TagOther), DropboxProtocol.UploadSessionFinishBatch, true)
            .ProvenNoEffect, "A documented top-level union tag is a refusal");
        Check.Require(errors.ClassifyMember(Union(DropboxProtocol.TooManyWriteOperations),
            DropboxProtocol.UploadSessionFinishBatch, true).Disposition == FailureDisposition.TemporaryNoEffect,
            "An upload contention member is temporary");
        Check.Require(errors.ClassifyMember(WriteConflict(), "files/unrecognized_batch", true).Disposition ==
            FailureDisposition.UnknownEffect, "An unrecognized batch route never guesses an upload refusal");
        Check.Require(errors.ClassifyMember(Element("text"), DropboxProtocol.UploadSessionFinishBatch, true)
            .Disposition == FailureDisposition.UnknownEffect, "A malformed member proves nothing");
    }
}
