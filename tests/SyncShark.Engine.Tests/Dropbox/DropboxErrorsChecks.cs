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
    private static JsonElement WriteConflict(string subtype = DropboxProtocol.TagFile) => Element(new Dictionary<string, object>
    {
        [DropboxProtocol.Tag] = DropboxProtocol.Path,
        [DropboxProtocol.Path] = new Dictionary<string, object>
        {
            [DropboxProtocol.Tag] = DropboxProtocol.Conflict,
            [DropboxProtocol.Conflict] = Union(subtype)
        }
    });

    private static JsonElement DirectUpload(JsonElement path) => Element(new Dictionary<string, object>
    {
        [".tag"] = "path", ["reason"] = path.GetProperty("path"), ["upload_session_id"] = "fixture"
    });

    private static JsonElement MoveError(string inner) => Tagged(DropboxProtocol.RelocationError, inner);

    // A full reply whose error payload is present but unusable while the outer object also looks like a union.
    private static JsonElement MixedEnvelope(object? errorValue) => Element(new Dictionary<string, object?>
    {
        [DropboxProtocol.ErrorSummary] = DropboxProtocol.PathNotFound,
        [DropboxProtocol.Error] = errorValue,
        [DropboxProtocol.Tag] = DropboxProtocol.Path,
        [DropboxProtocol.Path] = new Dictionary<string, object?> { [DropboxProtocol.Tag] = "not_found" }
    });

    // A direct singleton move_v2 RelocationError {.tag: to, to: {.tag: conflict, conflict: {.tag: file}}}.
    private static JsonElement MoveToConflict() => Element(new Dictionary<string, object>
    {
        [DropboxProtocol.Tag] = "to",
        ["to"] = new Dictionary<string, object>
        {
            [DropboxProtocol.Tag] = DropboxProtocol.Conflict,
            [DropboxProtocol.Conflict] = Union(DropboxProtocol.TagFile)
        }
    });

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

        RouteEvidence(errors, writeDispatched);
        Absence(errors);
        SingleRefusals(errors);
        UnknownWrites(errors, writeDispatched, writeNoEffect, writeTransient);
        TransientAndContention(errors, readTransient, readPreDispatchTransient, readPermanent, writeDispatched);
        BatchMembers(errors);
        RoundThree(errors, writeDispatched);
        return Task.CompletedTask;
    }

    // Round-3 findings: every expectation is collected so one red run reports each wrong outcome it detects.
    private static void RoundThree(DropboxErrors errors, DropboxErrors.RequestEvidence writeDispatched)
    {
        var failures = new List<string>();
        void Expect(bool condition, string message) { if (!condition) failures.Add(message); }

        // (1) A present but unusable error payload is never replaced by the outer object's union, so no false absence.
        Expect(!errors.Classify(HttpStatusCode.Conflict, MixedEnvelope(null), DropboxProtocol.GetMetadata, false, default).Absent,
            "a null error inside an outer path/not_found union is not absence");
        Expect(!errors.Classify(HttpStatusCode.Conflict, MixedEnvelope("bad"), DropboxProtocol.GetMetadata, false, default).Absent,
            "a string error inside an outer path/not_found union is not absence");
        Expect(!errors.Classify(HttpStatusCode.Conflict, MixedEnvelope(new Dictionary<string, object> { ["x"] = true }),
                DropboxProtocol.GetMetadata, false, default).Absent,
            "an untagged error object inside an outer path/not_found union is not absence");

        // (2) The named upload errors are not proof of no write; they stay Unknown.
        Expect(errors.Classify(HttpStatusCode.Conflict, Reply(string.Empty, Union("properties_error")),
                DropboxProtocol.Upload, true, writeDispatched).Disposition == FailureDisposition.UnknownEffect,
            "properties_error on a single upload is not proof of no effect");
        Expect(errors.ClassifyMember(Union("properties_error"), DropboxProtocol.UploadSessionFinishBatch, true)
                .Disposition == FailureDisposition.UnknownEffect,
            "a properties_error member is not proof of no effect");
        Expect(errors.Classify(HttpStatusCode.Conflict, Reply(string.Empty, Union("lookup_failed")),
                DropboxProtocol.Upload, true, writeDispatched).Disposition == FailureDisposition.UnknownEffect,
            "a payload-less lookup_failed on upload is not proof of no effect");
        Expect(errors.Classify(HttpStatusCode.Conflict, Reply(string.Empty, Union("lookup_failed")),
                DropboxProtocol.UploadSessionFinish, true, writeDispatched).Disposition == FailureDisposition.UnknownEffect,
            "a finish lookup_failed without its nested error is not proof of no effect");
        Expect(errors.Classify(HttpStatusCode.Conflict, SummaryOnly("to/conflict"), DropboxProtocol.Move, false,
                writeDispatched).Disposition == FailureDisposition.UnknownEffect,
            "a summary move conflict without its subtype is not proof of no effect");

        // (3) The direct singleton move_v2 union classifies as its refusal (batch wraps; singleton does not).
        Expect(errors.Classify(HttpStatusCode.Conflict, Reply("too_many_files", Union("too_many_files")),
                DropboxProtocol.Move, false, writeDispatched) == new DropboxErrors.Classified(TextKey.FailDropboxTooManyFiles, FailureDisposition.Permanent, true),
            "a direct singleton move too_many_files refusal is attributable");
        Expect(errors.Classify(HttpStatusCode.Conflict, Reply("from_lookup/not_found", Tagged("from_lookup", "not_found")),
                DropboxProtocol.Move, false, default).Cause == TextKey.FailChanged,
            "a direct singleton move from_lookup not_found is a changed target");
        Expect(errors.Classify(HttpStatusCode.Conflict, Reply("to/conflict/file", MoveToConflict()),
                DropboxProtocol.Move, false, default).Cause == TextKey.ClashNameTaken,
            "a direct singleton move to/conflict is a taken name");

        var reset = Reply(string.Empty, Union(DropboxProtocol.TagReset));
        foreach (var (status, body, route, expected) in new[]
        {
            (HttpStatusCode.Conflict, reset, DropboxProtocol.ListFolderContinue, true),
            (HttpStatusCode.Conflict, SummaryOnly("reset/.."), DropboxProtocol.ListFolderContinue, false),
            (HttpStatusCode.Conflict, reset, DropboxProtocol.ListFolder, false),
            (HttpStatusCode.BadRequest, reset, DropboxProtocol.ListFolderContinue, false),
            (HttpStatusCode.Conflict, Union(DropboxProtocol.TagReset), DropboxProtocol.ListFolderContinue, false),
            (HttpStatusCode.Conflict, Reply("reset/..", Union("other")), DropboxProtocol.ListFolderContinue, false)
        }) Expect(errors.Classify(status, body, route, false, new(true, true, false, false)).CursorReset == expected,
            "continuation reset must match the structured HTTP refusal on its own route");

        // (4) Only the exact structured invalid_async_job_id on a poll-check route is the invalid-job cause.
        foreach (var checkRoute in new[]
                 { DropboxProtocol.DeleteBatchCheck, DropboxProtocol.MoveBatchCheck, DropboxProtocol.CreateFolderBatchCheck })
            Expect(errors.Classify(HttpStatusCode.Conflict, Reply(string.Empty, Union("invalid_async_job_id")),
                    checkRoute, false, default).Cause == TextKey.FailDropboxDeleteJobInvalid,
                "an exact invalid_async_job_id poll-check refusal maps to the invalid-job cause on " + checkRoute);
        Expect(errors.Classify(HttpStatusCode.Conflict, SummaryOnly("invalid_async_job_id"),
                DropboxProtocol.DeleteBatchCheck, false, default).Cause != TextKey.FailDropboxDeleteJobInvalid,
            "a summary-only invalid_async_job_id is not the invalid-job cause");
        Expect(errors.Classify(HttpStatusCode.Conflict, Reply(string.Empty, Union("invalid_async_job_id")),
                DropboxProtocol.Upload, true, default).Cause != TextKey.FailDropboxDeleteJobInvalid,
            "a wrong-route invalid_async_job_id is not the invalid-job cause");

        // W13 job reader: the exact tag wins over a conflicting summary; an unrelated structured error under an
        // invalid summary, or a malformed/non-object envelope, is not the invalid-job cause.
        Expect(errors.Classify(HttpStatusCode.Conflict, Reply("path/not_found", Union("invalid_async_job_id")),
                DropboxProtocol.DeleteBatchCheck, false, default).Cause == TextKey.FailDropboxDeleteJobInvalid,
            "an exact invalid_async_job_id wins over a conflicting summary");
        Expect(errors.Classify(HttpStatusCode.Conflict, Reply("invalid_async_job_id", Union(DropboxProtocol.TagOther)),
                DropboxProtocol.DeleteBatchCheck, false, default).Cause != TextKey.FailDropboxDeleteJobInvalid,
            "an unrelated structured error under an invalid summary is not the invalid-job cause");
        Expect(errors.Classify(HttpStatusCode.Conflict, default, DropboxProtocol.DeleteBatchCheck, false, default).Cause !=
            TextKey.FailDropboxDeleteJobInvalid, "a malformed envelope is not the invalid-job cause");
        Expect(errors.Classify(HttpStatusCode.Conflict, Element("not-json"), DropboxProtocol.DeleteBatchCheck, false,
                default).Cause != TextKey.FailDropboxDeleteJobInvalid,
            "a non-object envelope is not the invalid-job cause");

        Check.Require(failures.Count == 0, string.Join("; ", failures));
    }

    private static void RouteEvidence(DropboxErrors errors, DropboxErrors.RequestEvidence sent)
    {
        var failures = new List<string>();
        var unknown = new DropboxErrors.Classified(TextKey.DropboxRefused, FailureDisposition.UnknownEffect, false);
        var refused = new DropboxErrors.Classified(TextKey.DropboxRefused, FailureDisposition.Permanent, true);
        var busy = new DropboxErrors.Classified(TextKey.WaitTooManyWrites, FailureDisposition.TemporaryNoEffect, true);
        var denied = unknown with { Cause = TextKey.FailDropboxNoPermission };
        var permission = Tagged("path_write", "no_write_permission");
        void Expect(bool ok, string why) { if (!ok) failures.Add(why); }
        DropboxErrors.Classified Http(string route, JsonElement body) =>
            errors.Classify(HttpStatusCode.Conflict, body, route, false, sent);
        Expect(!Http(DropboxProtocol.GetMetadata, Tagged("path", "not_found")).Absent, "bare metadata miss");
        Expect(!Http(DropboxProtocol.Delete, Tagged("path_lookup", "not_found")).Absent, "bare delete miss");
        Expect(Http(DropboxProtocol.DeleteBatchCheck, Union("invalid_async_job_id")).Cause !=
            TextKey.FailDropboxDeleteJobInvalid, "bare invalid job");
        foreach (var (route, failure) in new[]
        {
            (DropboxProtocol.UploadSessionAppend, WriteConflict()), (DropboxProtocol.UploadSessionStart, WriteConflict()),
            (DropboxProtocol.Upload, WriteConflict()), (DropboxProtocol.CreateFolder, Union("content_hash_mismatch")),
            (DropboxProtocol.UploadSessionFinish, Tagged("lookup_failed", "future_tag")),
            (DropboxProtocol.UploadSessionFinish, Tagged("lookup_failed", "incorrect_offset")),
            (DropboxProtocol.Upload, Tagged("future_tag", "too_many_write_operations")),
            (DropboxProtocol.CreateFolderBatch, WriteConflict()), (DropboxProtocol.DeleteBatch, Tagged("path_lookup", "not_found"))
        }) Expect(Http(route, Reply("", failure)) == unknown, route + " wrong or incomplete refusal");
        Expect(Http(DropboxProtocol.Upload, Reply("", Element(new Dictionary<string, object> {
            [".tag"] = "unrelated", ["reason"] = Union("too_many_write_operations") }))) == unknown, "unrelated reason");
        Expect(Http(DropboxProtocol.Upload, SummaryOnly("unrelated/too_many_write_operations")) == unknown, "unrelated summary");
        Expect(Http(DropboxProtocol.UploadSessionFinish, Reply("", Tagged("lookup_failed", "closed"))) == refused,
            "complete session lookup refusal");
        Expect(Http(DropboxProtocol.UploadSessionFinish, Reply("", Element(new Dictionary<string, object> {
            [".tag"] = "lookup_failed", ["lookup_failed"] = new Dictionary<string, object> {
                [".tag"] = "incorrect_offset", ["correct_offset"] = 12 } }))) == refused, "complete offset refusal");
        Expect(Http(DropboxProtocol.Upload, Reply("", Element(new { reason = Union("too_many_write_operations") }))) == busy,
            "rate-limit reason");
        foreach (var route in new[] { DropboxProtocol.CreateFolderBatch, DropboxProtocol.CreateFolderBatchCheck })
        {
            Expect(errors.ClassifyMember(WriteConflict("folder"), route, false) == refused with { Cause = TextKey.ClashNameTaken },
                route + " member folder conflict");
            Expect(errors.ClassifyMember(Union("unrecognized"), route, false) == unknown, route + " unknown member");
        }
        Expect(Http(DropboxProtocol.Download, Reply("", Tagged("path", "not_found"))) == refused with { Cause = TextKey.FailChanged },
            "structured download miss is changed, not absent");
        Expect(Http(DropboxProtocol.Download, Reply("path/not_found", Tagged("path", "not_file"))).Cause != TextKey.FailChanged,
            "structured download wins");
        var oversized = Union(DropboxProtocol.TooManyFiles);
        var split = refused with { Cause = TextKey.FailDropboxTooManyFiles };
        Expect(Http(DropboxProtocol.Delete, Reply("", oversized)) == split, "oversized singleton delete refusal");
        foreach (var route in new[] { DropboxProtocol.DeleteBatch, DropboxProtocol.DeleteBatchCheck })
        {
            Expect(errors.ClassifyMember(oversized, route, false) == split, route + " oversized delete member");
            Expect(Http(route, Reply("", oversized)) == unknown, route + " whole reply cannot authorize a split");
        }
        Expect(Http(DropboxProtocol.Delete, SummaryOnly(DropboxProtocol.TooManyFiles)) == unknown,
            "delete summary does not prove a size refusal");
        Expect(Http(DropboxProtocol.Delete, Reply("", permission)) == denied, "singleton delete permission");
        Expect(Http(DropboxProtocol.Delete, SummaryOnly("path_write/no_write_permission")) == denied, "delete summary permission");
        Expect(errors.ClassifyMember(permission, DropboxProtocol.DeleteBatch, false) == denied, "member delete permission");
        Check.Require(failures.Count == 0, string.Join("; ", failures));
    }

    // Finding 1: only the exact structured miss is absence; a summary-only or malformed/contradictory reply is not.
    private static void Absence(DropboxErrors errors)
    {
        var historyMiss = Tagged(DropboxProtocol.Path, "not_found");
        var historyReply = Reply(string.Empty, historyMiss);
        foreach (var (status, body, absent) in new[]
        {
            (HttpStatusCode.Conflict, historyReply, true),
            (HttpStatusCode.NotFound, historyReply, false),
            (HttpStatusCode.Conflict, SummaryOnly(DropboxProtocol.PathNotFound), false),
            (HttpStatusCode.Conflict, MixedEnvelope(null), false),
            (HttpStatusCode.Conflict, historyMiss, false)
        }) Check.Require(errors.Classify(status, body, DropboxProtocol.ListRevisions, false,
                new(true, true, false, false)).Absent == absent,
            "Only an enveloped 409 path/not_found proves no retained history");
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
        var conflict = Reply(DropboxProtocol.PathConflict + "/file", DirectUpload(WriteConflict()));
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
                DirectUpload(WriteReason(DropboxProtocol.InsufficientSpace))), DropboxProtocol.Upload, true, default);
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
        var readContention = errors.Classify(HttpStatusCode.TooManyRequests,
            Reply(string.Empty, Element(new { reason = Union(DropboxProtocol.TooManyWriteOperations) })),
            DropboxProtocol.ListFolder, false, readTransient);
        Check.Require(readContention == new DropboxErrors.Classified(TextKey.WaitTooManyWrites,
            FailureDisposition.TemporaryNoEffect, true), "A read carrying write contention waits without reducing concurrency");
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
             == new DropboxErrors.Classified(TextKey.DropboxRefused, FailureDisposition.UnknownEffect, false), "An unspecified other error does not establish no effect");
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
