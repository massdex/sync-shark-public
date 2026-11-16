using System.Net;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

public static partial class DropboxWriterChecks
{
    private static async Task MoveBatchContractsAsync()
    {
        foreach (var poll in new[] { false, true })
        {
            foreach (var (failure, cause) in MoveFailureCases())
                await MoveFailureReplyAsync(poll, failure, cause);
            await MoveBatchAttributionAsync(poll);
        }
        await MoveBatchCustodyAsync();
        await SideMoveBatchesAsync();
    }

    private static IEnumerable<(string Failure, TextKey? Cause)> MoveFailureCases()
    {
        yield return ("{\".tag\":\"too_many_write_operations\"}", TextKey.WaitTooManyWrites);
        foreach (var tag in new[] { "too_many_files", "cant_copy_shared_folder", "cant_nest_shared_folder",
            "cant_move_folder_into_itself", "duplicated_or_nested_paths", "cant_transfer_ownership",
            "insufficient_quota", "cant_move_shared_folder", "internal_error", "other", "undocumented" })
            yield return (Relocation("{\".tag\":\"" + tag + "\"}"), tag switch
            {
                "too_many_files" => TextKey.FailDropboxTooManyFiles,
                "internal_error" or "other" or "undocumented" => null,
                _ => TextKey.DropboxRefused
            });
        yield return (Relocation("{\".tag\":\"from_lookup\",\"from_lookup\":{\".tag\":\"not_found\"}}"), TextKey.FailChanged);
        foreach (var branch in new[] { "from_lookup", "from_write", "to" })
        {
            foreach (var payload in new[] { "\"bad path\"", "null", "42", "{}", "[]" })
                yield return (RelocationPath(branch, "{\".tag\":\"malformed_path\",\"malformed_path\":" + payload + "}"),
                    payload is "\"bad path\"" or "null" ? TextKey.FailDropboxNameRejected : null);
            yield return (RelocationPath(branch, "{\".tag\":\"malformed_path\"}"), null);
            foreach (var payload in new[] { "null", "42", "[]", "{}", "{\".tag\":42}" })
                yield return (RelocationPath(branch, payload), null);
            yield return (Relocation("{\".tag\":\"" + branch + "\"}"), null);
        }
        foreach (var branch in new[] { "from_write", "to" })
        {
            foreach (var tag in new[] { "too_many_write_operations", "disallowed_name", "no_write_permission",
                "insufficient_space", "team_folder", "operation_suppressed", "other", "undocumented" })
                yield return (RelocationPath(branch, "{\".tag\":\"" + tag + "\"}"), tag switch
                {
                    "too_many_write_operations" => TextKey.WaitTooManyWrites,
                    "disallowed_name" => TextKey.FailDropboxNameRejected,
                    "no_write_permission" => TextKey.FailDropboxNoPermission,
                    "insufficient_space" => TextKey.FailDropboxNoSpace,
                    "team_folder" or "operation_suppressed" => TextKey.DropboxRefused,
                    _ => null
                });
            foreach (var conflict in new[] { "file", "folder", "file_ancestor", "other", "undocumented" })
                yield return (RelocationPath(branch, "{\".tag\":\"conflict\",\"conflict\":{\".tag\":\"" + conflict + "\"}}"),
                    conflict is "other" or "undocumented" ? null : branch == "to" ? TextKey.ClashNameTaken : TextKey.DropboxRefused);
            foreach (var payload in new[] { "null", "42", "\"file\"", "[]", "{}", "{\".tag\":42}" })
                yield return (RelocationPath(branch, "{\".tag\":\"conflict\",\"conflict\":" + payload + "}"), null);
            yield return (RelocationPath(branch, "{\".tag\":\"conflict\"}"), null);
        }
        foreach (var failure in new[] { "null", "[]", "42", "{}", "{\".tag\":42}",
            "{\".tag\":\"internal_error\"}", "{\".tag\":\"other\"}", "{\".tag\":\"relocation_error\"}",
            "{\".tag\":\"relocation_error\",\"relocation_error\":null}" })
            yield return (failure, null);
    }

    private static string Relocation(string payload) =>
        "{\".tag\":\"relocation_error\",\"relocation_error\":" + payload + "}";

    private static string RelocationPath(string branch, string payload) =>
        Relocation("{\".tag\":\"" + branch + "\",\"" + branch + "\":" + payload + "}");

    private static async Task MoveFailureReplyAsync(bool poll, string failure, TextKey? cause)
    {
        using var fixture = new Fixture();
        var raw = "{\".tag\":\"complete\",\"entries\":[{\".tag\":\"failure\",\"failure\":" + failure + "}]}";
        fixture.Handler.Steps.Enqueue(new Step(poll ? DropboxProtocol.MoveBatchCheck : DropboxProtocol.MoveBatch,
            arg => InspectMoveRequest(arg, poll), new { }, Raw: raw));
        var results = await MoveReplyAsync(fixture, poll, 1);
        Check.Require(results.Count == 1 && results[0] == (cause is { } known
                ? (FolderMemberResult)new FolderMemberResult.KnownNoEffect(0, known)
                : new FolderMemberResult.Unknown(0, null)),
            "A documented move refusal needs its full payload; uncertain members retain their ordinal: " + failure);
        Check.Require(fixture.Waits.Count == (cause == TextKey.WaitTooManyWrites ? 1 : 0),
            "Only a proven member throttle emits one feedback event.");
        Check.Require(fixture.Handler.Requests == 1, "Decoding a move receipt never replays its mutation.");
        fixture.Done();
    }

    private static void InspectMoveRequest(JsonElement arg, bool poll)
    {
        if (poll)
        {
            Check.Require(arg.GetProperty("async_job_id").GetString() == "job", "Poll uses its persisted job id.");
            return;
        }
        var member = arg.GetProperty("entries")[0];
        Check.Require(member.GetProperty("from_path").GetString() == "id:source" &&
            member.GetProperty("to_path").GetString() == "/Parent/Destination/Child" &&
            !arg.GetProperty("autorename").GetBoolean() && !arg.GetProperty("allow_ownership_transfer").GetBoolean(),
            "The batch addresses the original identity verbatim and cannot overwrite, autorename or transfer ownership.");
    }

    private static async Task<IReadOnlyList<FolderMemberResult>> MoveReplyAsync(Fixture fixture, bool poll, int count)
    {
        var attempt = new RequestAttempt(new object(), AttemptOwner.Recovery);
        if (poll)
            return ((FolderJobResult.Terminal)await fixture.Writer.ReadMoveBatchAsync("job", count, attempt, default)).Results;
        return ((FolderSubmitResult.Terminal)await fixture.Writer.SubmitMoveBatchAsync(
            Enumerable.Range(0, count).Select(_ => ("id:source", "Destination/Child")).ToArray(), attempt, default)).Results;
    }

    private static async Task MoveBatchAttributionAsync(bool poll)
    {
        using var fixture = new Fixture();
        var route = poll ? DropboxProtocol.MoveBatchCheck : DropboxProtocol.MoveBatch;
        fixture.Handler.Steps.Enqueue(new Step(route, _ => { }, new Dictionary<string, object>
        {
            [".tag"] = "complete",
            ["entries"] = new object[]
            {
                new Dictionary<string, object> { [".tag"] = "success", ["success"] = Metadata("/Parent/Destination/Child") },
                new { },
                new Dictionary<string, object> { [".tag"] = "success", ["metadata"] = Metadata() }
            }
        }));
        var results = await MoveReplyAsync(fixture, poll, MixedOutcomeCount);
        Check.Require(results[0] is FolderMemberResult.Receipt { Ordinal: 0, Entry.Path: "Destination/Child" } &&
            results[1] == new FolderMemberResult.Unknown(1, null) && results[2] == new FolderMemberResult.Unknown(2, null),
            "Move success is nested under success; malformed siblings and the delete envelope stay unattributed.");
        foreach (var count in new[] { 0, 1, MixedOutcomeCount })
        {
            fixture.Handler.Steps.Enqueue(new Step(route, _ => { }, new Dictionary<string, object>
            {
                [".tag"] = "complete", ["entries"] = Enumerable.Repeat(new { }, count).ToArray()
            }));
            var wrong = await MoveReplyAsync(fixture, poll, PairCount);
            Check.Require(wrong.SequenceEqual(Enumerable.Range(0, PairCount).Select(i => new FolderMemberResult.Unknown(i, null))),
                "Wrong cardinality preserves every original wire ordinal as Unknown.");
        }
        fixture.Done();
    }

    private static async Task MoveBatchCustodyAsync()
    {
        using var fixture = new Fixture();
        fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatch, _ => { },
            new Dictionary<string, object> { [".tag"] = "async_job_id", ["async_job_id"] = "job" }));
        Check.Require(await fixture.Writer.SubmitMoveBatchAsync([("id:source", "Destination/Child")],
            new RequestAttempt(new object(), AttemptOwner.Recovery), default) is FolderSubmitResult.Accepted { JobId: "job" },
            "An accepted job is returned for durable custody before polling.");
        fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatchCheck, _ => { },
            new Dictionary<string, object> { [".tag"] = "in_progress" }));
        Check.Require(await fixture.Writer.ReadMoveBatchAsync("job", 1,
            new RequestAttempt(new object(), AttemptOwner.Recovery), default) is FolderJobResult.InProgress,
            "An active job grants neither terminal attribution nor replay.");
        fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatchCheck, _ => { },
            Refusal("unrelated/", new Dictionary<string, object> { [".tag"] = "invalid_async_job_id" }), HttpStatusCode.Conflict));
        Check.Require((await MoveReplyAsync(fixture, true, 1))[0] == new FolderMemberResult.Unknown(0, null),
            "An invalid move job requires identity resolution, not delete-job blocking.");
        fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatch, _ => throw new HttpRequestException(), new { }));
        Check.Require(await fixture.Writer.SubmitMoveBatchAsync([("id:source", "Destination/Child")],
            new RequestAttempt(new object(), AttemptOwner.Recovery), default) is FolderSubmitResult.Unknown,
            "Lost move acceptance keeps custody without replay.");
        fixture.Done();
        using var unsent = new Fixture(tokenFailure: new TimeoutException());
        Check.Require(await unsent.Writer.SubmitMoveBatchAsync([("id:source", "Destination/Child")],
            new RequestAttempt(new object(), AttemptOwner.Recovery), default) is FolderSubmitResult.KnownNoEffect &&
            unsent.Handler.Requests == 0, "An unsent move timeout proves no effect.");
    }

    private static async Task ExclusiveFolderContractsAsync()
    {
        foreach (var conflict in new[] { "file", "folder" })
        {
            using var fixture = new Fixture();
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.CreateFolder, arg =>
            {
                Path(arg, "/Parent/Child");
                Check.Require(!arg.GetProperty("autorename").GetBoolean(), "Exclusive create cannot autorename.");
            }, Error("path/conflict/" + conflict + "/"), HttpStatusCode.Conflict));
            await ThrowsAsync(typeof(PathOccupiedException), () => fixture.Writer.CreateFolderExclusiveAsync("Child", default));
            Check.Require(fixture.Handler.Requests == 1, "Exclusive create never reads or adopts an occupant.");
            fixture.Done();
        }
        using var created = new Fixture();
        created.Handler.Steps.Enqueue(new Step(DropboxProtocol.CreateFolder, _ => { }, new { metadata = Metadata(folder: true) }));
        Check.Require(await created.Writer.CreateFolderExclusiveAsync("Child", default) is { IsFolder: true, Id: "id:folder" },
            "Only a successful exclusive creation returns the owned destination id.");
        created.Done();
        using var malformed = new Fixture();
        malformed.Handler.Steps.Enqueue(new Step(DropboxProtocol.CreateFolder, _ => { }, new { }));
        await MalformedAsync("Child", typeof(KeyNotFoundException), () => malformed.Writer.CreateFolderExclusiveAsync("Child", default));
        Check.Require(malformed.Handler.Requests == 1, "An uncertain exclusive create is not replayed or adopted.");
        malformed.Done();
    }

    // The side's plain file moves go through move_batch_v2 and keep the single move's outcomes.
    private static async Task SideMoveBatchesAsync()
    {
        using (var fixture = new Fixture())
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatch, arg => Members(arg, "A"), new { },
                Raw: Complete(Success("A")), Release: release.Task));
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatch, arg => Members(arg, "B", "C"), new { },
                Raw: Complete(Success("B"), Success("C"))));
            var moves = new[] { "A", "B", "C" }.Select(name => fixture.Side.MoveAsync(Sibling(name), "Destination/" + name, default)).ToArray();
            release.SetResult();
            var moved = await Task.WhenAll(moves);
            Check.Require(moved.Select(entry => entry.Path).SequenceEqual(["Destination/A", "Destination/B", "Destination/C"]),
                "A lone move goes as a batch of one; siblings queued behind it share the next batch and each gets its own entry.");
            fixture.Done();
        }

        foreach (var (failure, expected) in new (string, Func<Exception, bool>)[]
        {
            (Relocation("{\".tag\":\"to\",\"to\":{\".tag\":\"conflict\",\"conflict\":{\".tag\":\"file\"}}}"),
                error => error is PathOccupiedException { Path: "Destination/A" }),
            (Relocation("{\".tag\":\"from_lookup\",\"from_lookup\":{\".tag\":\"not_found\"}}"),
                error => error is ChangedUnderneathException { Path: "Destination/A" }),
            (Relocation("{\".tag\":\"to\",\"to\":{\".tag\":\"no_write_permission\"}}"),
                error => error is ItemFailedException { Cause: TextKey.FailDropboxNoPermission, Disposition: FailureDisposition.Permanent, Move: null }),
            (Relocation("{\".tag\":\"internal_error\"}"),
                error => error is ItemFailedException { Cause: TextKey.FailDropboxOutcomeUnknown, Disposition: FailureDisposition.UnknownEffect } failed &&
                    failed.Move == new IssueSubject.UnknownMove("id:A", "Child/A", "Destination/A", null, false))
        })
        {
            using var fixture = new Fixture();
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatch, _ => { }, new { },
                Raw: Complete("{\".tag\":\"failure\",\"failure\":" + failure + "}")));
            Exception? caught = null;
            try { await fixture.Side.MoveAsync(Sibling("A"), "Destination/A", default); }
            catch (Exception error) { caught = error; }
            Check.Require(caught is not null && expected(caught),
                "A batch member's refusal is the single move's exception, and an unknown member keeps its intended move: " + failure);
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            // The throttle's account pause passes on the fixture clock, so the retry batch is admitted.
            fixture.Notify = waited => fixture.Time.Advance(waited.Wait);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatch, arg => Members(arg, "A"), new { },
                Raw: Complete("{\".tag\":\"failure\",\"failure\":{\".tag\":\"too_many_write_operations\"}}")));
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatch, arg => Members(arg, "A"), new { },
                Raw: "{\".tag\":\"async_job_id\",\"async_job_id\":\"job\"}"));
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatchCheck, _ => { },
                new Dictionary<string, object> { [".tag"] = "in_progress" }));
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatchCheck, _ => { }, new { }, Raw: Complete(Success("A"))));
            var moved = await fixture.Side.MoveAsync(Sibling("A"), "Destination/A", default);
            Check.Require(moved.Path == "Destination/A" && fixture.Waits.Count(wait => wait.Reason == TextKey.WaitTooManyWrites) == 1,
                "A throttled member goes in the next batch, and an accepted job is polled to its receipt.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            fixture.Handler.Steps.Enqueue(MoveStep("/Parent/Child", "/Parent/Other", true));
            fixture.Handler.Steps.Enqueue(MoveStep("/Parent/Child", "/Parent/child" + fixture.Settings.CaseRenameSuffix, false));
            fixture.Handler.Steps.Enqueue(MoveStep("/Parent/child" + fixture.Settings.CaseRenameSuffix, "/Parent/child", false));
            await fixture.Side.MoveAsync(Source() with { IsFolder = true }, "Other", default);
            await fixture.Side.MoveAsync(Source(), "child", default);
            Check.Require(fixture.Handler.Requests == 3, "A folder move and a case-only rename keep their single moves.");
            fixture.Done();
        }
    }

    private static Entry Sibling(string name) => Source() with { Path = "Child/" + name, Id = "id:" + name };

    private static void Members(JsonElement arg, params string[] names)
    {
        var entries = arg.GetProperty("entries");
        Check.Require(entries.GetArrayLength() == names.Length && names.Select((name, index) =>
                entries[index].GetProperty("from_path").GetString() == "/Parent/Child/" + name &&
                entries[index].GetProperty("to_path").GetString() == "/Parent/Destination/" + name).All(match => match) &&
            !arg.GetProperty("autorename").GetBoolean() && !arg.GetProperty("allow_ownership_transfer").GetBoolean(),
            "A side batch sends each member's source path and destination, without overwrite or ownership transfer.");
    }

    private static string Complete(params string[] members) =>
        "{\".tag\":\"complete\",\"entries\":[" + string.Join(",", members) + "]}";

    private static string Success(string name) =>
        "{\".tag\":\"success\",\"success\":" + JsonSerializer.Serialize(Metadata("/Parent/Destination/" + name)) + "}";
}
