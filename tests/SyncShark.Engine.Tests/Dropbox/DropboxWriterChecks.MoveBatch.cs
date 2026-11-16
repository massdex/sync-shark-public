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
            // Each caller has its own pass token, as Repair and AutoSync do; the group is shared anyway.
            using var other = new CancellationTokenSource();
            var moves = new[] { ("A", default(CancellationToken)), ("B", other.Token), ("C", default(CancellationToken)) }
                .Select(move => fixture.Side.MoveAsync(Sibling(move.Item1), "Destination/" + move.Item1, move.Item2)).ToArray();
            release.SetResult();
            var moved = await Task.WhenAll(moves);
            Check.Require(moved.Select(entry => entry.Path).SequenceEqual(["Destination/A", "Destination/B", "Destination/C"]),
                "A lone move goes as a batch of one; siblings from any pass queued behind it share the next batch, each with its own entry.");
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
            Check.Require(fixture.Handler.Requests == 3,
                "A folder move keeps the single call (its too_many_files reaches the in-parts fallback) and a case-only rename keeps its two hops.");
            fixture.Done();
        }

        await SideMoveCancellationAsync();
    }

    // A queued member leaves on cancellation; a member taken into a batch waits for that request's accounting and is
    // cancelled only when no effect is proven, so its caller keeps its locks while the move can still happen.
    private static async Task SideMoveCancellationAsync()
    {
        using (var fixture = new Fixture())
        {
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var poll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var polling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatch, arg => Members(arg, "A"), new { },
                Raw: Complete(Success("A")), Release: first.Task));
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatch, arg => Members(arg, "B", "C"), new { },
                Raw: AcceptedJob));
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatchCheck, _ => polling.TrySetResult(),
                new Dictionary<string, object> { [".tag"] = "in_progress" }, Release: poll.Task));
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatchCheck, _ => { }, new { },
                Raw: Complete(Success("B"), Success("C"))));
            using var cancel = new CancellationTokenSource();
            var a = fixture.Side.MoveAsync(Sibling("A"), "Destination/A", default);
            var b = fixture.Side.MoveAsync(Sibling("B"), "Destination/B", cancel.Token);
            var c = fixture.Side.MoveAsync(Sibling("C"), "Destination/C", default);
            first.SetResult();
            await a;
            await polling.Task;
            cancel.Cancel();
            var waited = !b.IsCompleted;
            poll.SetResult();
            Check.Require(waited && (await b).Path == "Destination/B" && (await c).Path == "Destination/C",
                "A caller cancelled after acceptance waits for the job's accounting and gets the move it made.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatch, arg => Members(arg, "A"), new { },
                Raw: Complete(Success("A")), Release: first.Task));
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatch, arg => Members(arg, "B", "C"), new { },
                Raw: AcceptedJob));
            var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var check = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatchCheck, _ => checking.TrySetResult(), Error("other/"),
                HttpStatusCode.Conflict, Release: check.Task));
            using var cancel = new CancellationTokenSource();
            var a = fixture.Side.MoveAsync(Sibling("A"), "Destination/A", default);
            var b = fixture.Side.MoveAsync(Sibling("B"), "Destination/B", cancel.Token);
            var c = fixture.Side.MoveAsync(Sibling("C"), "Destination/C", default);
            first.SetResult();
            await a;
            await checking.Task;
            cancel.Cancel();
            var waited = !b.IsCompleted && !c.IsCompleted;
            check.SetResult();
            foreach (var (move, name) in new[] { (b, "B"), (c, "C") })
            {
                Exception? failed = null;
                try { await move; }
                catch (Exception error) { failed = error; }
                Check.Require(waited && failed is ItemFailedException { Disposition: FailureDisposition.UnknownEffect } unknown &&
                    unknown.Move == new IssueSubject.UnknownMove("id:" + name, "Child/" + name, "Destination/" + name, null, false),
                    "A caller cancelled while its accepted job's check is held waits, and a failed check leaves the cancelled and the surviving member each its unknown move.");
            }
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatch, arg => Members(arg, "A"), new { },
                Raw: Complete(Success("A")), Release: first.Task));
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatch, arg => Members(arg, "B", "C"),
                Error("too_many_requests/"), HttpStatusCode.TooManyRequests));
            using var cancelB = new CancellationTokenSource();
            using var cancelC = new CancellationTokenSource();
            var a = fixture.Side.MoveAsync(Sibling("A"), "Destination/A", default);
            var b = fixture.Side.MoveAsync(Sibling("B"), "Destination/B", cancelB.Token);
            var c = fixture.Side.MoveAsync(Sibling("C"), "Destination/C", cancelC.Token);
            first.SetResult();
            await a;
            // The no-effect 429 puts the request in its retry wait on the fixture clock, which never advances here.
            Check.Require(SpinWait.SpinUntil(() => fixture.Time.HasArmedTimer && fixture.Handler.Steps.Count == 0, AdmissionBound),
                "The rate-limited batch waits to retry.");
            cancelB.Cancel();
            var kept = !b.IsCompleted;
            cancelC.Cancel();
            Exception? stoppedB = null, stoppedC = null;
            try { await b; } catch (Exception error) { stoppedB = error; }
            try { await c; } catch (Exception error) { stoppedC = error; }
            Check.Require(kept && stoppedB is OperationCanceledException && stoppedC is OperationCanceledException,
                "A retrying request runs while one caller is live and stops once all have cancelled; with no effect proven each is cancelled.");
            fixture.Done();
        }

        // Every caller cancels while its effect is uncertain: a held check of an accepted job, or a dispatched
        // submission whose reply has not arrived. Each keeps its own unknown move rather than a cancellation.
        foreach (var held in new[] { DropboxProtocol.MoveBatchCheck, DropboxProtocol.MoveBatch })
        {
            using var fixture = new Fixture();
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatch, arg => Members(arg, "A"), new { },
                Raw: Complete(Success("A")), Release: first.Task));
            fixture.Handler.Steps.Enqueue(held == DropboxProtocol.MoveBatch
                ? new Step(DropboxProtocol.MoveBatch, arg => { Members(arg, "B", "C"); entered.TrySetResult(); }, new { },
                    Raw: AcceptedJob, Release: never.Task)
                : new Step(DropboxProtocol.MoveBatch, arg => Members(arg, "B", "C"), new { }, Raw: AcceptedJob));
            if (held == DropboxProtocol.MoveBatchCheck)
                fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatchCheck, _ => entered.TrySetResult(),
                    new Dictionary<string, object> { [".tag"] = "in_progress" }, Release: never.Task));
            using var cancelB = new CancellationTokenSource();
            using var cancelC = new CancellationTokenSource();
            var a = fixture.Side.MoveAsync(Sibling("A"), "Destination/A", default);
            var b = fixture.Side.MoveAsync(Sibling("B"), "Destination/B", cancelB.Token);
            var c = fixture.Side.MoveAsync(Sibling("C"), "Destination/C", cancelC.Token);
            first.SetResult();
            await a;
            await entered.Task;
            cancelB.Cancel();
            cancelC.Cancel();
            foreach (var (move, name) in new[] { (b, "B"), (c, "C") })
            {
                Exception? failed = null;
                try { await move; }
                catch (Exception error) { failed = error; }
                Check.Require(failed is ItemFailedException { Disposition: FailureDisposition.UnknownEffect } unknown &&
                    unknown.Move == new IssueSubject.UnknownMove("id:" + name, "Child/" + name, "Destination/" + name, null, false),
                    "With every caller cancelled while held in " + held + ", each member keeps its own unknown move: " + failed);
            }
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatch, arg => Members(arg, "A"), new { },
                Raw: Complete("{\".tag\":\"failure\",\"failure\":{\".tag\":\"too_many_write_operations\"}}"), Release: first.Task));
            // B queued behind the held first request; the throttled A rejoins after it.
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatch, arg => Members(arg, "B", "A"), new { },
                Raw: Complete(Success("B"), "{\".tag\":\"failure\",\"failure\":" +
                    Relocation("{\".tag\":\"to\",\"to\":{\".tag\":\"conflict\",\"conflict\":{\".tag\":\"file\"}}}") + "}")));
            using var cancel = new CancellationTokenSource();
            var moving = fixture.Side.MoveAsync(Sibling("A"), "Destination/A", cancel.Token);
            var live = fixture.Side.MoveAsync(Sibling("B"), "Destination/B", default);
            first.SetResult();
            // The throttle's account pause holds the resend in request admission on the fixture clock.
            Check.Require(SpinWait.SpinUntil(() => fixture.Time.HasArmedTimer && fixture.Handler.Steps.Count == 1, AdmissionBound),
                "The throttled member's resend waits in request admission.");
            cancel.Cancel();
            var waited = !moving.IsCompleted;
            fixture.Time.Advance(fixture.Waits[^1].Wait);
            Exception? stopped = null;
            try { await moving; }
            catch (Exception error) { stopped = error; }
            Check.Require(waited && stopped is OperationCanceledException && (await live).Path == "Destination/B",
                "A caller cancelled during request admission waits for the request; with no effect proven it is cancelled.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.MoveBatch, arg => Members(arg, "A"), new { },
                Raw: Complete(Success("A")), Release: first.Task));
            using var cancel = new CancellationTokenSource();
            var a = fixture.Side.MoveAsync(Sibling("A"), "Destination/A", default);
            var unsent = fixture.Side.MoveAsync(Sibling("B"), "Destination/B", cancel.Token);
            cancel.Cancel();
            var left = unsent.IsCanceled;
            first.SetResult();
            Check.Require(left && (await a).Path == "Destination/A",
                "A cancelled member that was only queued leaves the queue at once and is never sent.");
            fixture.Done();
        }
    }

    private const string AcceptedJob = "{\".tag\":\"async_job_id\",\"async_job_id\":\"job\"}";
    private static readonly TimeSpan AdmissionBound = TimeSpan.FromSeconds(30);

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
