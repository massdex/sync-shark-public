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

    private static JsonElement ReplyNullError(string summary) => Element(new Dictionary<string, object?>
        { [DropboxProtocol.ErrorSummary] = summary, [DropboxProtocol.Error] = null });

    private static JsonElement ReplyStringError(string summary) => Element(new Dictionary<string, object?>
        { [DropboxProtocol.ErrorSummary] = summary, [DropboxProtocol.Error] = "unusable" });

    // An error object that is present but carries no .tag (malformed evidence).
    private static JsonElement ErrorWithoutTag() => Element(new Dictionary<string, object> { ["payload"] = true });

    // An upload/finish WriteError (session-finish shape: nested under path).
    private static JsonElement WriteReason(string reason) => Element(new Dictionary<string, object>
    {
        [DropboxProtocol.Tag] = DropboxProtocol.Path,
        [DropboxProtocol.Path] = Union(reason)
    });

    private static JsonElement WriteMalformed(long payload) => Element(new Dictionary<string, object>
    {
        [DropboxProtocol.Tag] = DropboxProtocol.Path,
        [DropboxProtocol.Path] = new Dictionary<string, object>
        {
            [DropboxProtocol.Tag] = DropboxProtocol.MalformedPath,
            [DropboxProtocol.MalformedPath] = payload
        }
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
        var readPreDispatchTransient = new DropboxErrors.RequestEvidence(true, false, true, true);
        var readPermanent = new DropboxErrors.RequestEvidence(true, true, true, false);
        var writeDispatched = new DropboxErrors.RequestEvidence(false, true, false, true);
        var writeNoEffect = new DropboxErrors.RequestEvidence(false, true, true, false);
        var writeTransient = new DropboxErrors.RequestEvidence(false, true, true, true);

        Absence(errors);
        SingleRefusals(errors);
        UnknownWrites(errors, writeDispatched, writeNoEffect, writeTransient);
        TransientAndContention(errors, readTransient, readPreDispatchTransient, readPermanent, writeDispatched);
        BatchMembers(errors);
        return Task.CompletedTask;
    }

    // Finding 1: only the exact structured miss is absence; a summary-only or malformed/contradictory reply is not.
    private static void Absence(DropboxErrors errors)
    {
        Check.Require(errors.Classify(HttpStatusCode.Conflict, Reply(string.Empty,
                Tagged(DropboxProtocol.Path, "not_found")), DropboxProtocol.GetMetadata, false, default).Absent,
            "The exact structured path/not_found is absence");
        Check.Require(!errors.Classify(HttpStatusCode.Conflict, SummaryOnly(DropboxProtocol.PathNotFound),
                DropboxProtocol.GetMetadata, false, default).Absent, "A summary-only not_found is not absence");
        Check.Require(!errors.Classify(HttpStatusCode.Conflict, ReplyNullError(DropboxProtocol.PathNotFound),
                DropboxProtocol.GetMetadata, false, default).Absent, "A null error payload is not absence");
        Check.Require(!errors.Classify(HttpStatusCode.Conflict, ReplyStringError(DropboxProtocol.PathNotFound),
                DropboxProtocol.GetMetadata, false, default).Absent, "A non-object error payload is not absence");
        Check.Require(!errors.Classify(HttpStatusCode.Conflict, Reply(DropboxProtocol.PathNotFound, ErrorWithoutTag()),
                DropboxProtocol.GetMetadata, false, default).Absent, "An error object without a tag is not absence");
        Check.Require(!errors.Classify(HttpStatusCode.Conflict, Reply(string.Empty,
                Tagged(DropboxProtocol.Path, "not_file")), DropboxProtocol.GetMetadata, false, default).Absent,
            "Only the structured not_found tag is absence");
        Check.Require(!errors.Classify(HttpStatusCode.Conflict, Reply(DropboxProtocol.PathNotFound,
                Union(DropboxProtocol.OperationSuppressed)), DropboxProtocol.GetMetadata, false, default).Absent,
            "A structured non-path refusal contradicts the absence summary");
    }

    private static void SingleRefusals(DropboxErrors errors)
    {
        // A create-only upload conflict is a taken name; a revision-bound one is a changed target.
        var conflict = Reply(DropboxProtocol.PathConflict + "/file", WriteConflict());
        var add = errors.Classify(HttpStatusCode.Conflict, conflict, DropboxProtocol.Upload, true, default);
        var update = errors.Classify(HttpStatusCode.Conflict, conflict, DropboxProtocol.Upload, false, default);
        Check.Require(add.Cause == TextKey.ClashNameTaken && add.Disposition == FailureDisposition.Permanent &&
            add.ProvenNoEffect, "An add conflict is a taken name");
        Check.Require(update.Cause == TextKey.FailChanged && update.Disposition == FailureDisposition.Permanent,
            "An update-by-revision conflict is a changed target");

        // A documented summary-only conflict needs its subtype segment.
        Check.Require(errors.Classify(HttpStatusCode.Conflict,
                SummaryOnly(DropboxProtocol.PathConflictFile), DropboxProtocol.Upload, true, default).Cause ==
            TextKey.ClashNameTaken, "A summary conflict with its subtype is a taken name");

        var noSpace = errors.Classify(HttpStatusCode.Conflict,
            Reply(DropboxProtocol.UploadWritePrefix + DropboxProtocol.InsufficientSpace,
                WriteReason(DropboxProtocol.InsufficientSpace)), DropboxProtocol.Upload, true, default);
        Check.Require(noSpace.Cause == TextKey.FailDropboxNoSpace && noSpace.ProvenNoEffect,
            "A documented write reason maps to its cause");

        var oversized = errors.Classify(HttpStatusCode.Conflict, SummaryOnly(DropboxProtocol.TooManyFiles),
            DropboxProtocol.Move, false, default);
        Check.Require(oversized.Cause == TextKey.FailDropboxTooManyFiles && oversized.ProvenNoEffect,
            "A summary-only too_many_files move refusal is attributable");

        Check.Require(errors.Classify(HttpStatusCode.Conflict, SummaryOnly(DropboxProtocol.PathNotFound)) ==
            FailureDisposition.Permanent, "The frozen two-argument surface still answers a disposition");
    }

    // Finding 2: a malformed or contradictory write reply is Unknown, never proof of no effect.
    private static void UnknownWrites(DropboxErrors errors, DropboxErrors.RequestEvidence writeDispatched,
        DropboxErrors.RequestEvidence writeNoEffect, DropboxErrors.RequestEvidence writeTransient)
    {
        Check.Require(errors.Classify(HttpStatusCode.Conflict, EmptyBody(), DropboxProtocol.Upload, true,
                writeDispatched).Disposition == FailureDisposition.UnknownEffect, "An empty 409 on a write is unknown");
        Check.Require(errors.Classify(HttpStatusCode.Conflict, Reply(string.Empty, Union("future_error")),
                DropboxProtocol.Upload, true, writeDispatched).Disposition == FailureDisposition.UnknownEffect,
            "An unrecognized 409 refusal is unknown");

        // A structured refusal we cannot decode must not be overridden by a summary that looks like a refusal.
        var move = errors.Classify(HttpStatusCode.Conflict,
            Reply(DropboxProtocol.TooManyFiles, MoveError("future_relocation")), DropboxProtocol.Move, false,
            writeDispatched);
        Check.Require(move.Disposition == FailureDisposition.UnknownEffect && !move.ProvenNoEffect,
            "A contradictory structured move refusal is unknown");
        var noTag = errors.Classify(HttpStatusCode.Conflict,
            Reply(DropboxProtocol.PathConflictFile, ErrorWithoutTag()), DropboxProtocol.Upload, true, writeDispatched);
        Check.Require(noTag.Disposition == FailureDisposition.UnknownEffect && !noTag.ProvenNoEffect,
            "An error object without a tag never proves a conflict");

        // A dispatched write the caller proved had no effect is not unknown; transient evidence makes it temporary.
        Check.Require(errors.Classify(HttpStatusCode.ServiceUnavailable, default, DropboxProtocol.Upload, true,
                writeNoEffect).Disposition == FailureDisposition.Permanent, "A proved no-effect non-transient write is permanent");
        Check.Require(errors.Classify(HttpStatusCode.RequestTimeout, default, DropboxProtocol.Upload, true,
                writeTransient).Disposition == FailureDisposition.TemporaryNoEffect,
            "A proved no-effect transient write is temporary");
        Check.Require(errors.Classify(HttpStatusCode.ServiceUnavailable, default, DropboxProtocol.Upload, true,
                writeDispatched).Disposition == FailureDisposition.UnknownEffect,
            "A dispatched write without proof is unknown");
    }

    // Finding 3/5: contention takes the write rule on any status; a transient read (even before dispatch) is temporary.
    private static void TransientAndContention(DropboxErrors errors, DropboxErrors.RequestEvidence readTransient,
        DropboxErrors.RequestEvidence readPreDispatchTransient, DropboxErrors.RequestEvidence readPermanent,
        DropboxErrors.RequestEvidence writeDispatched)
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
        Check.Require(errors.Classify(HttpStatusCode.ServiceUnavailable, default, DropboxProtocol.GetMetadata, false,
                readPreDispatchTransient).Disposition == FailureDisposition.TemporaryNoEffect,
            "A transient read that failed before dispatch is still temporary");
        Check.Require(errors.Classify(HttpStatusCode.InternalServerError, default, DropboxProtocol.GetMetadata, false,
                readPermanent).Disposition == FailureDisposition.Permanent, "A decided 500 read stays a failure");
    }

    private static void BatchMembers(DropboxErrors errors)
    {
        // A move member: an inner attributable refusal is no-effect; an unknown one is UnknownEffect.
        var moveOversized = errors.ClassifyMember(MoveError(DropboxProtocol.TooManyFiles), DropboxProtocol.MoveBatch, false);
        Check.Require(moveOversized.Cause == TextKey.FailDropboxTooManyFiles && moveOversized.ProvenNoEffect,
            "A move too_many_files member is attributable");
        Check.Require(errors.ClassifyMember(Union(DropboxProtocol.TooManyWriteOperations), DropboxProtocol.MoveBatch, false)
            .Disposition == FailureDisposition.TemporaryNoEffect, "A move contention member is temporary");
        Check.Require(errors.ClassifyMember(MoveError("future_relocation"), DropboxProtocol.MoveBatch, false)
            .Disposition == FailureDisposition.UnknownEffect, "An unknown move tag proves nothing");

        // The exact structured by-id delete not_found proves the id gone (Absent) but not our effect; nothing else does.
        var deleteMiss = errors.ClassifyMember(Tagged(DropboxProtocol.PathLookup, "not_found"),
            DropboxProtocol.DeleteBatch, false);
        Check.Require(deleteMiss.Absent && deleteMiss.Cause == TextKey.FailChanged &&
            deleteMiss.Disposition == FailureDisposition.UnknownEffect && !deleteMiss.ProvenNoEffect,
            "A by-id delete not_found proves the identity gone, not our effect");
        Check.Require(!errors.ClassifyMember(Tagged(DropboxProtocol.PathLookup, "not_file"),
            DropboxProtocol.DeleteBatch, false).Absent, "A delete lookup tag other than not_found is not absence");
        Check.Require(errors.ClassifyMember(Tagged(DropboxProtocol.PathWrite, DropboxProtocol.InsufficientSpace),
            DropboxProtocol.DeleteBatch, false).Disposition == FailureDisposition.UnknownEffect,
            "A delete write reason proves no outcome");

        // The same exact reply on the single delete_v2 path also carries absence; a summary-only lookup is not absence.
        var singleMiss = errors.Classify(HttpStatusCode.Conflict, Reply(string.Empty, Tagged(DropboxProtocol.PathLookup, "not_found")),
            DropboxProtocol.Delete, false, new DropboxErrors.RequestEvidence(false, true, false, true));
        Check.Require(singleMiss.Absent && singleMiss.Disposition == FailureDisposition.UnknownEffect,
            "A single delete_v2 exact not_found carries absence with an unknown effect");
        Check.Require(!errors.Classify(HttpStatusCode.Conflict, SummaryOnly(DropboxProtocol.PathLookupNotFound),
                DropboxProtocol.Delete, false, new DropboxErrors.RequestEvidence(false, true, false, true)).Absent,
            "A summary-only delete miss is not absence");

        // An upload member: the member's original request context (expectedIsNull = create-only vs revision-bound).
        Check.Require(errors.ClassifyMember(WriteConflict(), DropboxProtocol.UploadSessionFinishBatch, true).Cause ==
            TextKey.ClashNameTaken, "An upload add conflict member is a taken name");
        Check.Require(errors.ClassifyMember(WriteConflict(), DropboxProtocol.UploadSessionFinishBatch, false).Cause ==
            TextKey.FailChanged, "An upload update conflict member is a changed target");
        Check.Require(errors.ClassifyMember(WriteReason(DropboxProtocol.Conflict),
            DropboxProtocol.UploadSessionFinishBatch, true).Disposition == FailureDisposition.UnknownEffect,
            "A conflict without its required subtype proves nothing");
        Check.Require(errors.ClassifyMember(WriteMalformed(5),
            DropboxProtocol.UploadSessionFinishBatch, true).Disposition == FailureDisposition.UnknownEffect,
            "A malformed_path with a non-string payload proves nothing");
        Check.Require(errors.ClassifyMember(WriteReason("future_write_error"), DropboxProtocol.UploadSessionFinishBatch, true)
            .Disposition == FailureDisposition.UnknownEffect, "An unknown nested write tag proves nothing");
        Check.Require(errors.ClassifyMember(Union(DropboxProtocol.TagOther), DropboxProtocol.UploadSessionFinishBatch, true)
            .ProvenNoEffect, "A documented top-level union tag is a refusal");
        Check.Require(errors.ClassifyMember(Element("text"), DropboxProtocol.UploadSessionFinishBatch, true)
            .Disposition == FailureDisposition.UnknownEffect, "A malformed member proves nothing");

        // A create-folder batch refusal is classified at this owner with create semantics.
        Check.Require(errors.ClassifyMember(WriteConflict(), DropboxProtocol.CreateFolderBatch, false).Cause ==
            TextKey.ClashNameTaken, "A create-folder batch conflict is a taken name");
        Check.Require(errors.ClassifyMember(Union("future_create_error"), DropboxProtocol.CreateFolderBatch, true)
            .Disposition == FailureDisposition.UnknownEffect, "An unknown create-folder member proves nothing");
        Check.Require(errors.ClassifyMember(WriteConflict(), "files/unrecognized_batch", true).Disposition ==
            FailureDisposition.UnknownEffect, "An unrecognized batch route never guesses a refusal");
    }
}
