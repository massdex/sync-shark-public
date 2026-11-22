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

    /// <summary>Verifies the DropboxErrors contract through the module surface.</summary>
    public static Task RunAsync()
    {
        var errors = new DropboxErrors();
        var read = new DropboxErrors.RequestEvidence(Read: true, Dispatched: true, ProvenNoEffect: true, Transient: true);
        var writeDispatched = new DropboxErrors.RequestEvidence(false, true, false, true);
        var writeNoEffect = new DropboxErrors.RequestEvidence(false, true, true, true);
        var writeUnsent = new DropboxErrors.RequestEvidence(false, false, false, false);

        // The same upload conflict is a taken name on add and a changed target on update.
        var uploadConflict = Reply(DropboxProtocol.PathConflict + "/file", Tagged(DropboxProtocol.Path,
            DropboxProtocol.Conflict));
        var add = errors.Classify(HttpStatusCode.Conflict, uploadConflict, DropboxProtocol.Upload, true, default);
        var update = errors.Classify(HttpStatusCode.Conflict, uploadConflict, DropboxProtocol.Upload, false, default);
        Check.Require(add.Cause == TextKey.ClashNameTaken && add.Disposition == FailureDisposition.Permanent &&
            add.ProvenNoEffect, "An add conflict is a taken name");
        Check.Require(update.Cause == TextKey.FailChanged && update.Disposition == FailureDisposition.Permanent,
            "An update-by-revision conflict is a changed target");

        var noSpace = errors.Classify(HttpStatusCode.Conflict,
            Reply(DropboxProtocol.UploadWritePrefix + DropboxProtocol.InsufficientSpace,
                Tagged(DropboxProtocol.Path, DropboxProtocol.InsufficientSpace)),
            DropboxProtocol.Upload, true, default);
        Check.Require(noSpace.Cause == TextKey.FailDropboxNoSpace && noSpace.ProvenNoEffect,
            "A documented write reason maps to its cause");

        var contention = errors.Classify(HttpStatusCode.Conflict,
            Reply(DropboxProtocol.TooManyWriteOperations, Union(DropboxProtocol.TooManyWriteOperations)),
            DropboxProtocol.Upload, true, default);
        Check.Require(contention.Disposition == FailureDisposition.TemporaryNoEffect && contention.ProvenNoEffect,
            "Write contention is a temporary no-effect refusal");

        // A single attributable refusal from the summary alone, which is what a folder split may act on.
        var oversized = errors.Classify(HttpStatusCode.Conflict, SummaryOnly(DropboxProtocol.TooManyFiles),
            DropboxProtocol.Move, false, default);
        Check.Require(oversized.Cause == TextKey.FailDropboxTooManyFiles && oversized.ProvenNoEffect &&
            oversized.Disposition == FailureDisposition.Permanent, "Too many files is an attributable refusal");

        var gone = errors.Classify(HttpStatusCode.Conflict, SummaryOnly(DropboxProtocol.PathLookupNotFound),
            DropboxProtocol.Delete, false, default);
        Check.Require(gone.Cause == TextKey.FailChanged, "A delete lookup miss is a changed target");

        // Only get_metadata's own not_found proves the target absent, in either shape.
        Check.Require(errors.Classify(HttpStatusCode.Conflict, SummaryOnly(DropboxProtocol.PathNotFound),
                DropboxProtocol.GetMetadata, false, default).Absent, "A path/not_found summary is proven absence");
        Check.Require(errors.Classify(HttpStatusCode.Conflict,
                Reply(string.Empty, Tagged(DropboxProtocol.Path, "not_found")),
                DropboxProtocol.GetMetadata, false, default).Absent, "A structured path/not_found is proven absence");

        // Disposition outside a 409 follows the request evidence.
        Check.Require(errors.Classify(HttpStatusCode.ServiceUnavailable, default, DropboxProtocol.GetMetadata,
                false, read).Disposition == FailureDisposition.Permanent, "A read failure is permanent");
        Check.Require(errors.Classify(HttpStatusCode.ServiceUnavailable, default, DropboxProtocol.Upload,
                true, writeDispatched).Disposition == FailureDisposition.UnknownEffect,
            "A dispatched write without no-effect proof is an unknown outcome");
        Check.Require(errors.Classify(HttpStatusCode.RequestTimeout, default, DropboxProtocol.Upload,
                true, writeNoEffect).Disposition == FailureDisposition.TemporaryNoEffect,
            "A proven no-effect transient write is temporary");
        Check.Require(errors.Classify(HttpStatusCode.BadRequest, default, DropboxProtocol.Upload,
                true, writeUnsent).Disposition == FailureDisposition.Permanent, "An unsent write is permanent");

        Check.Require(errors.Classify(HttpStatusCode.Conflict, SummaryOnly(DropboxProtocol.PathNotFound)) ==
            FailureDisposition.Permanent, "The frozen two-argument surface still answers a disposition");

        BatchMembers(errors);
        return Task.CompletedTask;
    }

    private static void BatchMembers(DropboxErrors errors)
    {
        // A move member: an inner attributable refusal is no-effect; an unknown one is UnknownEffect.
        var oversized = errors.ClassifyMember(
            Tagged(DropboxProtocol.RelocationError, DropboxProtocol.TooManyFiles), DropboxProtocol.MoveBatch, false);
        Check.Require(oversized.Cause == TextKey.FailDropboxTooManyFiles && oversized.ProvenNoEffect &&
            oversized.Disposition == FailureDisposition.Permanent, "A move too_many_files member is attributable");
        var contention = errors.ClassifyMember(Union(DropboxProtocol.TooManyWriteOperations),
            DropboxProtocol.MoveBatch, false);
        Check.Require(contention.Cause == TextKey.WaitTooManyWrites &&
            contention.Disposition == FailureDisposition.TemporaryNoEffect, "A move contention member is temporary");
        var unknownMove = errors.ClassifyMember(
            Tagged(DropboxProtocol.RelocationError, "future_relocation"), DropboxProtocol.MoveBatch, false);
        Check.Require(unknownMove.Disposition == FailureDisposition.UnknownEffect && !unknownMove.ProvenNoEffect,
            "An unknown move tag proves nothing");

        // No DeleteError tag proves the effect was not applied, so every delete member is Unknown.
        var deleteMiss = errors.ClassifyMember(
            Tagged(DropboxProtocol.PathLookup, "not_found"), DropboxProtocol.DeleteBatch, false);
        Check.Require(deleteMiss.Cause == TextKey.FailChanged &&
            deleteMiss.Disposition == FailureDisposition.UnknownEffect, "A delete lookup miss proves no outcome");
        var deleteNoSpace = errors.ClassifyMember(
            Tagged(DropboxProtocol.PathWrite, DropboxProtocol.InsufficientSpace), DropboxProtocol.DeleteBatch, false);
        Check.Require(deleteNoSpace.Cause == TextKey.FailDropboxNoSpace &&
            deleteNoSpace.Disposition == FailureDisposition.UnknownEffect, "A delete write reason proves no outcome");

        // An upload member: a structured conflict is attributable; a recognized top-level tag is a refusal; an
        // unknown tag or a malformed member makes only its own position Unknown.
        var uploadAdd = errors.ClassifyMember(Tagged(DropboxProtocol.Path, DropboxProtocol.Conflict),
            DropboxProtocol.UploadSessionFinishBatch, expectedIsNull: true);
        Check.Require(uploadAdd.Cause == TextKey.ClashNameTaken && uploadAdd.ProvenNoEffect &&
            uploadAdd.Disposition == FailureDisposition.Permanent, "An upload add conflict member is attributable");
        var uploadThrottle = errors.ClassifyMember(Union(DropboxProtocol.TooManyWriteOperations),
            DropboxProtocol.UploadSessionFinishBatch, true);
        Check.Require(uploadThrottle.Disposition == FailureDisposition.TemporaryNoEffect,
            "An upload contention member is temporary");
        var uploadOther = errors.ClassifyMember(Union(DropboxProtocol.TagOther),
            DropboxProtocol.UploadSessionFinishBatch, true);
        Check.Require(uploadOther.Disposition == FailureDisposition.Permanent && uploadOther.ProvenNoEffect,
            "A documented top-level union tag is a refusal");
        var uploadUnknown = errors.ClassifyMember(Union("future_upload_error"),
            DropboxProtocol.UploadSessionFinishBatch, true);
        Check.Require(uploadUnknown.Disposition == FailureDisposition.UnknownEffect && !uploadUnknown.ProvenNoEffect,
            "An unknown upload tag proves nothing");
        var malformed = errors.ClassifyMember(Element("text"), DropboxProtocol.UploadSessionFinishBatch, true);
        Check.Require(malformed.Disposition == FailureDisposition.UnknownEffect,
            "A malformed member proves nothing");
    }
}
