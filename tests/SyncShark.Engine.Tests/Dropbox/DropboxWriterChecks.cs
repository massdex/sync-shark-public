using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;
using DropboxWriter = Massdex.SyncShark.Engine.Dropbox.DropboxWriter;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Checks for DropboxWriter (`--engine-checks dropbox`), written by DropboxWriter's writer.</summary>
public static class DropboxWriterChecks
{
    private const int OneByte = 1;
    private const int InitialConcurrency = 1;
    private const int LogRetentionDays = 1;
    private const int PairCount = 2;
    // One member for each outcome: published, terminal refusal and unknown.
    private const int MixedOutcomeCount = 3;
    private const string UnknownMemberTag = "unknown";
    private const string SecondPath = "Other";
    private const string ThirdPath = "Extra";
    private const string ThirdRemotePath = "/Parent/Extra";
    private const string FollowingPath = "Following";
    private const string FollowingRemotePath = "/Parent/Following";
    private const string CommitProperty = "commit";
    private const string CohortCountCheck = "ExactQueuedCohortCount";
    private const string MixedOutcomeCheck = "SuccessTerminalFailureUnknownKeepAttribution";
    private const string FollowingDrainCheck = "UnrelatedQueuedWorkDrainsWithoutReplay";
    private const int ProbeCalls = 1000;
    private const int LogBufferCharacters = 262144;
    private const int DownloadBufferBytes = 16;
    private const int WrongKindNumber = 123;
    private const double BackoffInitialSeconds = 1;
    private const double BackoffFactor = 2;
    private const double BackoffMaximumSeconds = 120;
    private static readonly DateTimeOffset SourceTime = DateTimeOffset.Parse("2024-01-02T03:04:05.123+02:00");
    private static readonly string OneByteHash = Digest(OneByte);

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        AllocationProbe();
        await SingleUploadsAsync();
        await NonSingletonUploadAsync();
        await RevokedSingletonUploadAsync();
        await SessionUploadsAsync();
        await DownloadAsync();
        await FoldersAsync();
        await DeletesAndMovesAsync();
        await StaleAncestorCasingAsync();
        await ErrorsAsync();
        await StructuredFailuresAsync();
        await MalformedRepliesAsync();
        await ReturnedSizeAndHashAsync();
        await MixedMembersAsync();
        await ThreeOutcomeCohortAsync();
        await OccupiedParentAsync();
        await DirectRateThrottleDoesNotResetCommitStreak();
        await DirectSummaryThrottleDoesNotResetCommitStreak();
        await AllFailedThrottledCohortPreservesNotificationFailure();
        await FolderSubmissionRejectsKindMismatchBeforeIo();
        await ConditionalFileRequiresFrozenRevision();
        await UnknownFolderSubmissionKeepsCustodyAsync();
        await FolderJobsAsync();
    }

    private static void AllocationProbe()
    {
        const string newPath = "Docs/child";
        var settings = new EngineSettings("{name}", "yyyyMMdd", "local", "dropbox", ".case-rename",
            RecycleFallback.Fail, LogRetentionDays, FirstRunPolicy.NewestWins, InitialConcurrency, "", LogBufferCharacters, ScanTestSettings.BatchRows) { JournalPageBufferBytes = ScanTestSettings.JournalPageBufferBytes, LogReadBufferCharacters = ScanTestSettings.LogReadBufferCharacters };
        Action<int> baseline = index => Sink(newPath + settings.CaseRenameSuffix);
        Action<int> candidate = index => Sink(PathNames.CaseRenamePath(newPath, settings.CaseRenameSuffix));

        var baselineBytes = AllocProbe.Measure(baseline, ProbeCalls);
        var candidateBytes = AllocProbe.Measure(candidate, ProbeCalls);
        Check.Require(baselineBytes == candidateBytes,
            "CaseRenamePath allocates the same bytes per call as the original concatenation on the taken case-only branch.");
    }

    // A typed, non-inlined sink keeps the probed call's result live without boxing.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(string? value) => _string = value;

    private static string? _string;

    private static async Task SingleUploadsAsync()
    {
        foreach (var length in new[] { 0L, (long)OneByte, DropboxApiLimits.SingleUploadMaxBytes })
        foreach (var replace in new[] { false, true })
        {
            using var fixture = new Fixture();
            using var content = new SizedStream(length);
            var source = Source(length);
            await using var work = await fixture.Limiter.EnterWorkAsync(true, default);
            await using var staged = await fixture.Writer.StageAsync("Child", content, source, default);
            Check.Require(fixture.Handler.Requests == 0, "A single upload is not sent during staging.");
            Check.Require(((DropboxStaged)staged).ContentHash is null && content.BytesRead == 0,
                "A direct stage has no digest and has not read its source.");
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.Upload,
                arg => Commit(arg, replace), (Func<object>)(() => Metadata(tagged: false, size: length, hash: fixture.Handler.ReceivedHash)), BodyLength: length));
            var result = await fixture.Writer.PublishAsync(staged, replace ? source : null, null, default);
            Check.Require(result.Outcome == RemovalOutcome.Published && result.OperationId is null,
                "A completed direct publication is classified Published with no pending operation.");
            Check.Require(result.Final is { Path: "Child", Revision: "new-rev" },
                "Single upload parses file metadata into the verified final entry.");
            Check.Require(result.Final!.Size == length && result.Final.ContentHash == fixture.Handler.ReceivedHash && content.BytesRead == length,
                "Direct publication verifies the exact received size and digest after one source pass.");
            await staged.DisposeAsync();
            Check.Require(content.CanRead, "Publishing and disposing a stage leave caller content open.");
            fixture.Done();
        }
    }

    private static async Task NonSingletonUploadAsync()
    {
        // Without the sole single-file work scope the same small content is a closed session, never a direct upload.
        using var fixture = new Fixture();
        using var content = new SizedStream(OneByte);
        fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionStart, arg =>
            Check.Require(arg.GetProperty("close").GetBoolean(), "A one-piece session is closed at staging."),
            new { session_id = "session" }, BodyLength: OneByte));
        await using var staged = await fixture.Writer.StageAsync("Child", content, Source(), default);
        fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionFinishBatch,
            arg => BatchEntry(arg, OneByte, false), Batch(Finished())));
        var result = await fixture.Writer.PublishAsync(staged, null, null, default);
        Check.Require(result.Outcome == RemovalOutcome.Published,
            "A non-singleton small upload publishes as one batch member.");
        fixture.Done();
    }

    private static async Task RevokedSingletonUploadAsync()
    {
        // Even the sole single-file scope takes the session route once a caller withdraws direct eligibility.
        using var fixture = new Fixture();
        using var content = new SizedStream(OneByte);
        await using var sole = await fixture.Limiter.EnterWorkAsync(true, default);
        fixture.Limiter.DisallowDirectUpload();
        fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionStart, arg =>
            Check.Require(arg.GetProperty("close").GetBoolean(), "A withdrawn direct choice still closes one session."),
            new { session_id = "session" }, BodyLength: OneByte));
        await using var staged = await fixture.Writer.StageAsync("Child", content, Source(), default);
        Check.Require(((DropboxStaged)staged).SessionId is not null, "A withdrawn direct choice takes the session route.");
        fixture.Done();
    }

    private static async Task SessionUploadsAsync()
    {
        foreach (var length in new[] { DropboxApiLimits.SingleUploadMaxBytes + 1,
            DropboxApiLimits.SingleUploadMaxBytes * PairCount, DropboxApiLimits.SingleUploadMaxBytes * PairCount + 1 })
        {
            using var fixture = new Fixture();
            using var content = new SizedStream(length);
            var offset = 0L;
            while (offset < length)
            {
                var start = offset;
                var size = Math.Min(DropboxApiLimits.SingleUploadMaxBytes, length - offset);
                var last = start + size >= length;
                fixture.Handler.Steps.Enqueue(new Step(start == 0 ? DropboxProtocol.UploadSessionStart : DropboxProtocol.UploadSessionAppend,
                    arg =>
                    {
                        Check.Require(arg.GetProperty("close").GetBoolean() == last,
                            "Only the final piece closes the upload session.");
                        if (start != 0) Cursor(arg.GetProperty("cursor"), start);
                    }, start == 0 ? new { session_id = "session" } : (object)new { }, BodyLength: size, BodyOffset: start));
                offset += size;
            }
            var count = fixture.Handler.Steps.Count;
            await using var staged = await fixture.Writer.StageAsync("Child", content, Source(length), default);
            Check.Require(staged is DropboxStaged session && session.ContentHash == fixture.Handler.ReceivedHash &&
                content.BytesRead == length, "Session hashing uses the one transmitted source pass.");
            Check.Require(fixture.Handler.Requests == count, "Staging sends every maximum-sized piece before returning.");
            Check.Require(content.CanRead, "Session slices do not dispose their source.");
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionFinishBatch,
                arg => BatchEntry(arg, length, false), Batch(Finished(size: length, hash: fixture.Handler.ReceivedHash))));
            var result = await fixture.Writer.PublishAsync(staged, null, null, default);
            Check.Require(result.Outcome == RemovalOutcome.Published && result.Final!.Id == "id:file",
                "Session finish parses metadata from the batch member.");
            fixture.Done();
        }
    }

    private static async Task DownloadAsync()
    {
        using (var fixture = new Fixture())
        {
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.Download,
                arg => Check.Require(arg.GetProperty("path").GetString() == "rev:old-rev", "Download requests the exact revision."),
                "payload"));
            var attempt = new RequestAttempt(new object(), AttemptOwner.Transfer);
            await using var stream = await fixture.Writer.OpenReadAsync(Source(), attempt, default);
            var bytes = new byte[DownloadBufferBytes];
            var total = 0;
            int read;
            while ((read = await stream.ReadAsync(bytes)) != 0) total += read;
            Check.Require(total == "payload".Length, "Download returns readable content (callers never seek it).");
            Check.Require(attempt.Completion is null,
                "A fully read body is not staging success; the transfer owner completes the attempt after validation.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.Download, _ => { },
                Error("other/"), HttpStatusCode.Conflict));
            var attempt = new RequestAttempt(new object(), AttemptOwner.Transfer);
            await ThrowsAsync(typeof(ItemFailedException),
                () => fixture.Writer.OpenReadAsync(Source(), attempt, default));
            Check.Require(attempt.Completion?.Result == RequestResult.PermanentFailure &&
                attempt.Completion?.NoEffectProven == true,
                "A refused read publishes one permanent no-effect disposition for its attempt.");
            Check.Require(fixture.Handler.Requests == 1, "A refused download is not replayed by the writer.");
            fixture.Done();
        }
    }

    private static async Task FoldersAsync()
    {
        foreach (var root in new[] { "/Parent", "/" })
        foreach (var state in new[] { "existing", "file", "absent", "parallel" })
        {
            using var fixture = new Fixture(root);
            var remote = root == "/" ? "/Child" : "/Parent/Child";
            var present = state is "existing" or "file";
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.GetMetadata,
                arg => Path(arg, remote), present ? Metadata(remote, state == "existing") : Error("path/not_found/"),
                present ? HttpStatusCode.OK : HttpStatusCode.Conflict));
            if (!present)
            {
                fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.CreateFolder, arg =>
                {
                    Path(arg, remote);
                    Check.Require(!arg.GetProperty("autorename").GetBoolean(), "Folder creates do not autorename.");
                }, state == "parallel" ? Error("path/conflict/folder/") : new { metadata = Metadata(remote, true, tagged: false) },
                    state == "parallel" ? HttpStatusCode.Conflict : HttpStatusCode.OK));
                if (state == "parallel")
                    fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.GetMetadata, arg => Path(arg, remote), Metadata(remote, true)));
            }
            if (state == "file")
                await ThrowsAsync(typeof(PathOccupiedException), () => fixture.Writer.EnsureFolderAsync("Child", default));
            else
            {
                var result = await fixture.Writer.EnsureFolderAsync("Child", default);
                Check.Require(result.IsFolder && result.Path == "Child", "Folder metadata is parsed, including the create envelope.");
            }
            fixture.Done();
        }
        using var vanished = new Fixture();
        vanished.Handler.Steps.Enqueue(new Step(DropboxProtocol.GetMetadata, _ => { },
            Error("path/not_found/"), HttpStatusCode.Conflict));
        vanished.Handler.Steps.Enqueue(new Step(DropboxProtocol.CreateFolder, _ => { },
            Error("path/conflict/folder/"), HttpStatusCode.Conflict));
        vanished.Handler.Steps.Enqueue(new Step(DropboxProtocol.GetMetadata, _ => { },
            Error("path/not_found/"), HttpStatusCode.Conflict));
        try
        {
            await vanished.Writer.EnsureFolderAsync("Child", default);
            Check.Require(false, "A folder conflict followed by missing metadata must fail.");
        }
        catch (ChangedUnderneathException error)
        {
            Check.Require(error.Path == "Child", "A vanished parallel folder create is a changed-underneath race.");
        }
        vanished.Done();

        using var rootFixture = new Fixture("/");
        rootFixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.GetMetadata,
            arg => Path(arg, ""), Metadata("", true)));
        Check.Require((await rootFixture.Writer.EnsureFolderAsync("", default)).IsFolder,
            "Account-root metadata lookup uses the empty root argument.");
        rootFixture.Done();
    }

    private static async Task DeletesAndMovesAsync()
    {
        using (var fixture = new Fixture())
        {
            var source = Source();
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.Delete, arg =>
            {
                Path(arg, "/Parent/Child");
                Check.Require(arg.GetProperty("parent_rev").GetString() == source.Revision,
                    "File deletion is conditional on the expected revision.");
            }, new { metadata = Metadata() }));
            var removed = await fixture.Writer.DeleteAsync(source, null, default);
            Check.Require(removed.Outcome == RemovalOutcome.Removed,
                "A confirmed file delete is classified Removed.");
            fixture.Done();
        }

        using (var folders = new Fixture())
        {
            Exception? refusal = null;
            RemovalResult? result = null;
            try
            {
                result = await folders.Writer.DeleteAsync(Source() with { IsFolder = true }, null, default);
            }
            catch (Exception error)
            {
                refusal = error;
            }
            Check.Require(folders.Handler.Requests == 0,
                "A direct folder delete refuses before sending; only the selected folder operation may remove a tree.");
            Check.Require(refusal is not null || result is { Outcome: not RemovalOutcome.Removed },
                "A direct folder delete never reports a removal.");
        }

        foreach (var folder in new[] { false, true })
        foreach (var destination in new[] { "Other", "child" })
        {
            using var moving = new Fixture();
            var from = "/Parent/Child";
            if (destination == "child")
            {
                var intermediate = "/Parent/child" + moving.Settings.CaseRenameSuffix;
                moving.Handler.Steps.Enqueue(MoveStep(from, intermediate, folder));
                from = intermediate;
            }
            moving.Handler.Steps.Enqueue(MoveStep(from, "/Parent/" + destination, folder));
            var moved = await moving.Writer.MoveAsync(Source() with { IsFolder = folder }, destination, default);
            Check.Require(moved.Path == destination && moved.IsFolder == folder,
                "Normal and case-only moves return final envelope metadata for files and folders.");
            moving.Done();
        }
        using var interrupted = new Fixture();
        interrupted.Handler.Steps.Enqueue(MoveStep("/Parent/Child", "/Parent/child" + interrupted.Settings.CaseRenameSuffix, false));
        interrupted.Handler.Steps.Enqueue(new Step(DropboxProtocol.Move, _ => { }, Error("to/conflict/file/"), HttpStatusCode.Conflict));
        await ThrowsAsync(typeof(PathOccupiedException), () => interrupted.Writer.MoveAsync(Source(), "child", default));
        interrupted.Done();
    }

    private static async Task StaleAncestorCasingAsync()
    {
        const string requested = "Docs/A/f.txt";
        const string stale = "/Parent/docs/A/f.txt";

        using (var fixture = new Fixture())
        {
            using var content = new SizedStream(OneByte);
            await using var work = await fixture.Limiter.EnterWorkAsync(true, default);
            await using var staged = await fixture.Writer.StageAsync(requested, content, Source(), default);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.Upload, _ => { }, Metadata(stale), BodyLength: OneByte));
            var result = await fixture.Writer.PublishAsync(staged, null, null, default);
            Check.Require(result.Final!.Path == requested,
                "An upload reply with a stale-cased ancestor is respelled to the requested path.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            var remote = "/Parent/" + requested;
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.GetMetadata, arg => Path(arg, remote),
                Error("path/not_found/"), HttpStatusCode.Conflict));
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.CreateFolder, _ => { },
                new { metadata = Metadata(stale, folder: true, tagged: false) }));
            var result = await fixture.Writer.EnsureFolderAsync(requested, default);
            Check.Require(result.Path == requested,
                "A create_folder reply with a stale-cased ancestor is respelled to the requested path.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.Move, arg => Check.Require(
                arg.GetProperty("to_path").GetString() == "/Parent/" + requested, "Move requests the destination path."),
                new { metadata = Metadata(stale) }));
            var result = await fixture.Writer.MoveAsync(Source(), requested, default);
            Check.Require(result.Path == requested,
                "A move reply with a stale-cased ancestor is respelled to the destination path.");
            fixture.Done();
        }
    }

    private static async Task ErrorsAsync()
    {
        var cases = new (string Route, string Summary, bool Replace, Type Exception, TextKey Cause)[]
        {
            (DropboxProtocol.Download, "path/not_found/", false, typeof(ChangedUnderneathException), TextKey.DropboxRefused),
            (DropboxProtocol.Upload, "path/conflict/file/", false, typeof(PathOccupiedException), TextKey.DropboxRefused),
            (DropboxProtocol.Upload, "path/conflict/file/", true, typeof(ChangedUnderneathException), TextKey.DropboxRefused),
            (DropboxProtocol.CreateFolder, "path/conflict/file/", false, typeof(PathOccupiedException), TextKey.DropboxRefused),
            (DropboxProtocol.Delete, "path_lookup/not_found/", false, typeof(ChangedUnderneathException), TextKey.DropboxRefused),
            (DropboxProtocol.Delete, "path_lookup/not_file/", false, typeof(ItemFailedException), TextKey.DropboxRefused),
            (DropboxProtocol.Delete, "path_write/no_write_permission/", false, typeof(ItemFailedException), TextKey.FailDropboxNoPermission),
            (DropboxProtocol.Move, "from_lookup/not_found/", false, typeof(ChangedUnderneathException), TextKey.DropboxRefused),
            (DropboxProtocol.Move, "to/conflict/file/", false, typeof(PathOccupiedException), TextKey.DropboxRefused)
        };
        foreach (var row in cases)
            await ErrorCaseAsync(row.Route, row.Summary, row.Replace, row.Exception, cause: row.Cause);
        foreach (var route in new[] { DropboxProtocol.Download, DropboxProtocol.Upload, DropboxProtocol.UploadSessionStart,
            DropboxProtocol.UploadSessionAppend, DropboxProtocol.UploadSessionFinishBatch, DropboxProtocol.CreateFolder,
            DropboxProtocol.Delete, DropboxProtocol.Move })
        {
            await ErrorCaseAsync(route, "other/", false, typeof(ItemFailedException));
            await ErrorCaseAsync(route, "", false, typeof(ItemFailedException), new HttpRequestException());
            await ErrorCaseAsync(route, "", false, typeof(OperationCanceledException), new OperationCanceledException());
        }
    }

    private static async Task StructuredFailuresAsync()
    {
        // The shared structured classifier owns the tag paths; the writer settles its answer and never replays.
        var cases = new (string Tag, bool Replace, Type Exception, TextKey Cause)[]
        {
            (DropboxProtocol.Conflict, false, typeof(PathOccupiedException), TextKey.DropboxRefused),
            (DropboxProtocol.Conflict, true, typeof(ChangedUnderneathException), TextKey.DropboxRefused),
            (DropboxProtocol.InsufficientSpace, false, typeof(ItemFailedException), TextKey.FailDropboxNoSpace),
            (DropboxProtocol.NoWritePermission, false, typeof(ItemFailedException), TextKey.FailDropboxNoPermission),
            (DropboxProtocol.MalformedPath, false, typeof(ItemFailedException), TextKey.FailDropboxNameRejected),
            (DropboxProtocol.DisallowedName, false, typeof(ItemFailedException), TextKey.FailDropboxNameRejected)
        };
        foreach (var row in cases)
        {
            using (var fixture = new Fixture())
            using (var content = new SizedStream(OneByte))
            {
                fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.Upload, _ => { },
                    Refusal("unrelated/", DirectFailure(row.Tag)), HttpStatusCode.Conflict, BodyLength: OneByte));
                await RequiresMappedAsync(row.Exception,
                    () => fixture.Writer.PublishAsync(DropboxStaged.Single("Child", content, Source()),
                        row.Replace ? Source() : null, null, default), row.Cause);
                Check.Require(fixture.Handler.Requests == 1, "A structured direct refusal is not replayed.");
                fixture.Done();
            }

            using (var fixture = new Fixture())
            {
                fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionFinishBatch, _ => { },
                    Batch(Failed(row.Tag))));
                await RequiresMappedAsync(row.Exception,
                    () => fixture.Writer.PublishAsync(DropboxStaged.Session("Child", "session", OneByte, Source(), OneByteHash),
                        row.Replace ? Source() : null, null, default), row.Cause);
                Check.Require(fixture.Handler.Requests == 1, "A structured batch-member refusal is not replayed.");
                fixture.Done();
            }
        }

        using (var fixture = new Fixture())
        using (var content = new SizedStream(OneByte))
        {
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.Upload, _ => { },
                Refusal(DropboxProtocol.UploadWritePrefix + DropboxProtocol.TooManyWriteOperations +
                    DropboxProtocol.ErrorSummarySeparator, DirectFailure(DropboxProtocol.TooManyWriteOperations)),
                HttpStatusCode.Conflict, BodyLength: OneByte));
            await RequiresMappedAsync(typeof(ItemFailedException),
                () => fixture.Writer.PublishAsync(DropboxStaged.Single("Child", content, Source()), null, null, default),
                TextKey.DropboxRefused);
            Check.Require(fixture.Handler.Requests == 1, "A write-throttled upload is not replayed.");
            Check.Require(fixture.Waits.Count(waited => waited.Reason == TextKey.WaitTooManyWrites) == 1,
                "Write contention is reported once through the shared account owner.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionFinishBatch, _ => { },
                Batch(Failed(DropboxProtocol.TooManyWriteOperations))));
            await RequiresMappedAsync(typeof(ItemFailedException),
                () => fixture.Writer.PublishAsync(DropboxStaged.Session("Child", "session", OneByte, Source(), OneByteHash), null, null, default),
                TextKey.DropboxRefused);
            Check.Require(fixture.Handler.Requests == 1, "A write-throttled batch member is not replayed.");
            Check.Require(fixture.Waits.Count(waited => waited.Reason == TextKey.WaitTooManyWrites) == 1,
                "A batch member's write contention is reported once by the batch owner.");
            fixture.Done();
        }
    }

    private static async Task ErrorCaseAsync(string route, string summary, bool replace, Type exception,
        Exception? transport = null, TextKey cause = TextKey.DropboxRefused)
    {
        using var fixture = new Fixture();
        using var content = new SizedStream(route is DropboxProtocol.UploadSessionStart or DropboxProtocol.UploadSessionAppend
            ? DropboxApiLimits.SingleUploadMaxBytes + 1 : OneByte);
        if (route == DropboxProtocol.CreateFolder)
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.GetMetadata, _ => { }, Error("path/not_found/"), HttpStatusCode.Conflict));
        if (route == DropboxProtocol.UploadSessionAppend)
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionStart, _ => { }, new { session_id = "session" },
                BodyLength: DropboxApiLimits.SingleUploadMaxBytes));
        fixture.Handler.Steps.Enqueue(new Step(route, _ => { if (transport is not null) throw transport; },
            Error(summary), HttpStatusCode.Conflict,
            BodyOffset: route == DropboxProtocol.UploadSessionAppend ? DropboxApiLimits.SingleUploadMaxBytes : 0));
        var expected = replace ? Source() : null;
        await ThrowsAsync(exception, async () =>
        {
            switch (route)
            {
                case DropboxProtocol.Download:
                    await using (await fixture.Writer.OpenReadAsync(Source(),
                        new RequestAttempt(new object(), AttemptOwner.Request), default)) { }
                    break;
                case DropboxProtocol.Upload:
                    await fixture.Writer.PublishAsync(DropboxStaged.Single("Child", content, Source()), expected, null, default);
                    break;
                case DropboxProtocol.UploadSessionFinishBatch:
                    await fixture.Writer.PublishAsync(DropboxStaged.Session("Child", "session", content.Length, Source(content.Length), OneByteHash), expected, null, default);
                    break;
                case DropboxProtocol.UploadSessionStart:
                case DropboxProtocol.UploadSessionAppend:
                    await fixture.Writer.StageAsync("Child", content, Source(content.Length), default);
                    break;
                case DropboxProtocol.CreateFolder:
                    await fixture.Writer.EnsureFolderAsync("Child", default);
                    break;
                case DropboxProtocol.Delete:
                    await fixture.Writer.DeleteAsync(Source(), null, default);
                    break;
                case DropboxProtocol.Move:
                    await fixture.Writer.MoveAsync(Source(), "Other", default);
                    break;
                default: throw new InvalidOperationException(route);
            }
        }, transport, cause);
        Check.Require(content.CanRead, "An upload error leaves the caller's stream open.");
        fixture.Done();
    }

    private static async Task ReturnedSizeAndHashAsync()
    {
        foreach (var direct in new[] { false, true })
        foreach (var mismatch in new[] { "size", "hash", "both" })
        {
            using var fixture = new Fixture();
            using var content = new SizedStream(OneByte);
            await using var staged = direct ? DropboxStaged.Single("Child", content, Source())
                : DropboxStaged.Session("Child", "session", OneByte, Source(), OneByteHash);
            var metadata = Metadata(size: mismatch == "hash" ? OneByte : 0,
                hash: mismatch == "size" ? OneByteHash : Digest(0));
            if (!direct) ((Dictionary<string, object>)metadata)[DropboxProtocol.Tag] = DropboxProtocol.TagSuccess;
            fixture.Handler.Steps.Enqueue(new Step(direct ? DropboxProtocol.Upload : DropboxProtocol.UploadSessionFinishBatch,
                _ => { }, direct ? metadata : Batch(metadata), BodyLength: direct ? OneByte : null));
            await RequiresMappedAsync(typeof(ItemFailedException),
                () => fixture.Writer.PublishAsync(staged, null, null, default), TextKey.DropboxRefused,
                providerEvidence: false);
            Check.Require(fixture.Handler.Requests == 1, "A size or digest refusal neither deletes nor reuploads.");
            fixture.Done();
        }
    }

    // Hold the first request so the following members enter one real writer cohort.
    private static async Task<Exception?[]> CohortAsync(Fixture fixture, object[] reply, bool replaceSecond = false,
        bool includeThird = false)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionFinishBatch, _ => { },
            Batch(Finished("/Parent/Blocker")), Release: release.Task));
        var blocker = fixture.Writer.PublishAsync(
            DropboxStaged.Session("Blocker", "blocker", OneByte, Source(), OneByteHash), null, null, default);
        Check.Require(fixture.Handler.Requests > 0 && !blocker.IsCompleted, "The transport gate holds the drain owner.");
        fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionFinishBatch, arg =>
        {
            var entries = arg.GetProperty(DropboxProtocol.Entries);
            Check.Require(entries.GetArrayLength() == (includeThird ? MixedOutcomeCount : PairCount), CohortCountCheck);
            Path(entries[0].GetProperty("commit"), "/Parent/Child");
            Path(entries[1].GetProperty("commit"), "/Parent/Other");
            if (includeThird) Path(entries[MixedOutcomeCount - 1].GetProperty(CommitProperty), ThirdRemotePath);
        }, Batch(reply)));
        var first = fixture.Writer.PublishAsync(
            DropboxStaged.Session("Child", "first", OneByte, Source(), OneByteHash), null, null, default);
        var second = fixture.Writer.PublishAsync(
            DropboxStaged.Session("Other", "second", OneByte, Source(), OneByteHash),
            replaceSecond ? Source() with { Path = "Other" } : null, null, default);
        var observations = new List<Task<Exception?>> { ObserveAsync(first, "Child"), ObserveAsync(second, "Other") };
        Task<RemovalResult>? following = null;
        using var followingToken = new CancellationTokenSource();
        if (includeThird)
        {
            observations.Add(ObserveAsync(fixture.Writer.PublishAsync(
                DropboxStaged.Session(ThirdPath, ThirdPath, OneByte, Source(), OneByteHash), null, null, default), ThirdPath));
            // A distinct cancellation cohort queues behind the mixed reply, without a timing-dependent fill wait.
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionFinishBatch, arg =>
            {
                var entries = arg.GetProperty(DropboxProtocol.Entries);
                Check.Require(entries.GetArrayLength() == 1, FollowingDrainCheck);
                Path(entries[0].GetProperty(CommitProperty), FollowingRemotePath);
            }, Batch(Finished(FollowingRemotePath))));
            following = fixture.Writer.PublishAsync(DropboxStaged.Session(
                FollowingPath, FollowingPath, OneByte, Source(), OneByteHash), null, null, followingToken.Token);
        }
        release.SetResult();
        await blocker;
        var errors = await Task.WhenAll(observations);
        if (following is not null)
        {
            var result = await following;
            Check.Require(result.Outcome == RemovalOutcome.Published && result.Final?.Path == FollowingPath,
                FollowingDrainCheck);
        }
        return errors;
    }

    private static async Task<Exception?> ObserveAsync(Task<RemovalResult> task, string path)
    {
        try
        {
            var result = await task;
            Check.Require(result.Outcome == RemovalOutcome.Published && result.Final?.Path == path,
                "A successful sibling remains published at its original target.");
            return null;
        }
        catch (Exception error) { return error; }
    }

    private static async Task MixedMembersAsync()
    {
        using (var fixture = new Fixture())
        {
            var errors = await CohortAsync(fixture, [Failed(DropboxProtocol.Conflict), Failed(DropboxProtocol.Conflict)], true);
            Check.Require(errors[0] is PathOccupiedException { Path: "Child" } &&
                errors[1] is ChangedUnderneathException { Path: "Other" }, "Create and replace failures retain distinct target attribution.");
            Check.Require(fixture.Handler.Requests == PairCount, "The blocker and mixed cohort each send once.");
            fixture.Done();
        }
        foreach (var kind in new[] { "malformed", "unknown", "throttle" })
        foreach (var failureFirst in new[] { false, true })
        {
            using var fixture = new Fixture();
            var failedIndex = failureFirst ? 0 : OneByte;
            var successIndex = failureFirst ? OneByte : 0;
            var failedPath = failureFirst ? "Child" : "Other";
            var replies = new[] { Finished(), Finished("/Parent/Other") };
            replies[failedIndex] = kind == "malformed" ? new { } : Failed(kind == "throttle" ? DropboxProtocol.TooManyWriteOperations : "unknown");
            var errors = await CohortAsync(fixture, replies);
            Check.Require(errors[successIndex] is null && errors[failedIndex] is ItemFailedException failed &&
                failed.Path == failedPath && failed.Cause == TextKey.DropboxRefused &&
                (kind == "malformed" ? failed.InnerException is KeyNotFoundException :
                    failed.InnerException is ProviderFailureException { HttpStatus: 200 } evidence &&
                    evidence.Route == DropboxProtocol.UploadSessionFinishBatch &&
                    evidence.Tag == (kind == "throttle" ? DropboxProtocol.TooManyWriteOperations : "unknown")),
                "A valid sibling survives a malformed, unknown or throttled sibling without positional shift.");
            Check.Require(fixture.Waits.Count == (kind == "throttle" ? OneByte : 0), "A cohort reports write contention exactly once.");
            Check.Require(fixture.Handler.Requests == PairCount, "No mixed member is replayed.");
            fixture.Done();
        }
        foreach (var replies in new object[][] { [Finished()], [Finished(), Finished("/Parent/Other"), Finished("/Parent/Extra")] })
        {
            using var fixture = new Fixture();
            var errors = await CohortAsync(fixture, replies);
            Check.Require(errors[0] is ItemFailedException { Path: "Child", InnerException: JsonException } &&
                errors[1] is ItemFailedException { Path: "Other", InnerException: JsonException } && fixture.Waits.Count == 0,
                "Wrong member counts make every unsettled member uncertain without shifting attribution or resetting feedback.");
            Check.Require(fixture.Handler.Requests == PairCount, "Wrong cardinality never replays a member.");
            fixture.Done();
        }
    }

    private static async Task ThreeOutcomeCohortAsync()
    {
        using var fixture = new Fixture();
        fixture.Notify = waited => fixture.Time.Advance(waited.Wait);
        var errors = await CohortAsync(fixture,
            [Finished(), Failed(DropboxProtocol.TooManyWriteOperations),
                new Dictionary<string, object> { [DropboxProtocol.Tag] = UnknownMemberTag }], includeThird: true);
        Check.Require(errors.Length == MixedOutcomeCount && errors[0] is null &&
            errors[1] is ItemFailedException { Cause: TextKey.DropboxRefused,
                InnerException: ProviderFailureException { HttpStatus: 200 } } terminal &&
            terminal.Path == SecondPath &&
            errors[MixedOutcomeCount - 1] is ItemFailedException
                { Cause: TextKey.DropboxRefused, InnerException: JsonException } unknown && unknown.Path == ThirdPath,
            MixedOutcomeCheck);
        Check.Require(fixture.Waits.Count == 1 && fixture.Waits[0].Reason == TextKey.WaitTooManyWrites &&
            fixture.Handler.Requests == PairCount + 1, FollowingDrainCheck);
        fixture.Done();
    }

    private static async Task OccupiedParentAsync()
    {
        foreach (var direct in new[] { false, true })
        foreach (var replace in new[] { false, true })
        {
            using var fixture = new Fixture();
            using var content = new SizedStream(OneByte);
            var conflict = new Dictionary<string, object>
            {
                [DropboxProtocol.Tag] = DropboxProtocol.Conflict,
                [DropboxProtocol.Conflict] = new Dictionary<string, object> { [DropboxProtocol.Tag] = "file_ancestor" }
            };
            var failure = new Dictionary<string, object>
            {
                [DropboxProtocol.Tag] = DropboxProtocol.Path,
                [direct ? DropboxProtocol.Reason : DropboxProtocol.Path] = conflict
            };
            fixture.Handler.Steps.Enqueue(new Step(direct ? DropboxProtocol.Upload : DropboxProtocol.UploadSessionFinishBatch,
                _ => { }, direct ? Refusal("unrelated/", failure) : Batch(new Dictionary<string, object>
                { [DropboxProtocol.Tag] = DropboxProtocol.TagFailure, [DropboxProtocol.TagFailure] = failure }),
                direct ? HttpStatusCode.Conflict : HttpStatusCode.OK, BodyLength: direct ? OneByte : null));
            await RequiresMappedAsync(replace ? typeof(ChangedUnderneathException) : typeof(PathOccupiedException),
                () => fixture.Writer.PublishAsync(direct ? DropboxStaged.Single("Child", content, Source()) :
                    DropboxStaged.Session("Child", "session", OneByte, Source(), OneByteHash),
                    replace ? Source() : null, null, default), TextKey.DropboxRefused);
            Check.Require(fixture.Handler.Requests == 1, "An occupied parent is not replayed.");
            fixture.Done();
        }
    }

    private static Task DirectRateThrottleDoesNotResetCommitStreak() => DirectThrottleAsync(true);
    private static Task DirectSummaryThrottleDoesNotResetCommitStreak() => DirectThrottleAsync(false);

    private static async Task DirectThrottleAsync(bool rate)
    {
        using var fixture = new Fixture();
        await CommitThrottleAsync(fixture);
        var first = fixture.Waits[^1].Wait;
        fixture.Time.Advance(first);
        using var content = new SizedStream(OneByte);
        if (rate)
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.Upload, _ => { },
                Refusal("unrelated/", new { retry_after = 0 }), HttpStatusCode.TooManyRequests, BodyLength: OneByte));
        fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.Upload, _ => { },
            Refusal("too_many_write_operations/", DirectFailure(DropboxProtocol.Conflict)),
            HttpStatusCode.Conflict, BodyLength: OneByte));
        await RequiresMappedAsync(typeof(PathOccupiedException),
            () => fixture.Writer.PublishAsync(DropboxStaged.Single("Child", content, Source()), null, null, default), TextKey.DropboxRefused);
        fixture.Time.Advance(fixture.Waits[^1].Wait);
        await CommitThrottleAsync(fixture);
        Check.Require(fixture.Waits[^1].Wait == first * BackoffFactor,
            "An HTTP-owned throttle does not reset the semantic commit streak.");
        Check.Require(fixture.Handler.Requests == fixture.Waits.Count, "Each refusal produces one notification, including a replayed 429.");
        fixture.Done();
    }

    private static async Task CommitThrottleAsync(Fixture fixture)
    {
        fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionFinishBatch, _ => { },
            Batch(Failed(DropboxProtocol.TooManyWriteOperations))));
        await RequiresMappedAsync(typeof(ItemFailedException), () => fixture.Writer.PublishAsync(
            DropboxStaged.Session("Child", "session", OneByte, Source(), OneByteHash), null, null, default), TextKey.DropboxRefused);
    }

    private static async Task AllFailedThrottledCohortPreservesNotificationFailure()
    {
        foreach (var control in new[] { "failed", "mixed", "success" })
        {
            using var fixture = new Fixture();
            var notification = new InvalidOperationException("notification");
            fixture.Notify = _ => throw notification;
            var errors = await CohortAsync(fixture,
                [control == "success" ? Finished() : Failed(DropboxProtocol.TooManyWriteOperations),
                 control == "failed" ? Failed(DropboxProtocol.NoWritePermission) : Finished("/Parent/Other")]);
            if (control == "success")
                Check.Require(errors.All(error => error is null) && fixture.Waits.Count == 0,
                    "All-success control produces no failure notification.");
            else
            {
                Check.Require(fixture.Waits.Count == 1, "A throwing cohort notification is called once.");
                Check.Require(errors[0] is AggregateException first && first.InnerExceptions.Any(error => ReferenceEquals(error, notification)) &&
                    first.InnerExceptions.Any(error => error is ItemFailedException { Path: "Child" }),
                    "The failed member preserves its own error and the notification error.");
                Check.Require(control == "failed"
                    ? errors[1] is AggregateException second && second.InnerExceptions.Any(error => ReferenceEquals(error, notification)) &&
                        second.InnerExceptions.Any(error => error is ItemFailedException { Path: "Other", Cause: TextKey.FailDropboxNoPermission })
                    : ReferenceEquals(errors[1], notification), "Every member preserves notification failure without erasing its own refusal.");
                fixture.Time.Advance(fixture.Waits[^1].Wait);
            }
            fixture.Notify = null;
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionFinishBatch, _ => { }, Batch(Finished())));
            var next = await fixture.Writer.PublishAsync(DropboxStaged.Session("Child", "later", OneByte, Source(), OneByteHash), null, null, default);
            Check.Require(next.Outcome == RemovalOutcome.Published && fixture.Handler.Requests == PairCount + 1,
                "The following cohort completes without replaying prior members.");
            fixture.Done();
        }
    }

    private static FolderSubmission Armed() => new("submission", "pair", 0, FolderSubmission.SubmissionPhase.Armed, null, null);

    private static async Task FolderSubmissionRejectsKindMismatchBeforeIo()
    {
        foreach (var folder in new[] { false, true })
        {
            using var fixture = new Fixture();
            var effect = new FolderRemovalEffect("operation", 0, 0,
                folder ? FolderEffectKind.ConditionalFile : FolderEffectKind.RecursiveFolder, Source() with { IsFolder = folder });
            await ThrowsAsync(typeof(ArgumentException), () => fixture.Writer.SubmitFolderRemovalAsync(Armed(), [effect],
                new RequestAttempt(new object(), AttemptOwner.Recovery), default));
            Check.Require(fixture.Handler.Requests == 0, "A kind mismatch is refused before provider I/O.");
        }
    }

    private static async Task ConditionalFileRequiresFrozenRevision()
    {
        using var fixture = new Fixture();
        var effect = new FolderRemovalEffect("operation", 0, 0, FolderEffectKind.ConditionalFile, Source() with { Revision = "" });
        await ThrowsAsync(typeof(ArgumentException), () => fixture.Writer.SubmitFolderRemovalAsync(Armed(), [effect],
            new RequestAttempt(new object(), AttemptOwner.Recovery), default));
        Check.Require(fixture.Handler.Requests == 0, "An empty frozen file revision is refused before I/O.");
        fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.DeleteBatch, arg =>
        {
            var entry = arg.GetProperty(DropboxProtocol.Entries)[0];
            Path(entry, "/Parent/Child");
            Check.Require(entry.GetProperty("parent_rev").GetString() == "old-rev", "The exact frozen revision reaches the wire.");
        }, new Dictionary<string, object> { [DropboxProtocol.Tag] = DropboxProtocol.AsyncJobId, [DropboxProtocol.AsyncJobId] = "job" }));
        var result = await fixture.Writer.SubmitFolderRemovalAsync(Armed(), [effect with { Expected = Source() }],
            new RequestAttempt(new object(), AttemptOwner.Recovery), default);
        Check.Require(result is FolderSubmitResult.Accepted, "A valid conditional member receives the job receipt.");
        fixture.Done();
    }

    private static async Task UnknownFolderSubmissionKeepsCustodyAsync()
    {
        using var fixture = new Fixture();
        var effect = new FolderRemovalEffect("operation", 0, 0, FolderEffectKind.RecursiveFolder,
            Source() with { IsFolder = true });
        var attempt = new RequestAttempt(new object(), AttemptOwner.Request);
        fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.DeleteBatch, _ => throw new HttpRequestException(),
            new { }));
        var result = await fixture.Writer.SubmitFolderRemovalAsync(Armed(), [effect], attempt, default);
        Check.Require(result is FolderSubmitResult.Unknown { Cause: TextKey.DropboxRefused },
            "A dispatched folder submission without a receipt keeps custody as Unknown.");
        Check.Require(attempt.Completion?.Result == RequestResult.UnknownWrite &&
            attempt.Completion?.NoEffectProven == false,
            "A lost receipt is an unknown write, never a proved no-effect refusal.");
        Check.Require(fixture.Handler.Requests == 1, "An unknown folder submission is not replayed.");
        fixture.Done();
    }

    private static async Task FolderJobsAsync()
    {
        using var fixture = new Fixture();
        fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.DeleteBatchCheck, _ => { }, new Dictionary<string, object>
        {
            [DropboxProtocol.Tag] = DropboxProtocol.TagComplete,
            [DropboxProtocol.Entries] = new object[]
            {
                new Dictionary<string, object> { [DropboxProtocol.Tag] = DropboxProtocol.TagSuccess, [DropboxProtocol.Metadata] = Metadata() },
                new { }
            }
        }));
        var result = await fixture.Writer.ReadFolderRemovalAsync("job", PairCount,
            new RequestAttempt(new object(), AttemptOwner.Recovery), default);
        Check.Require(result is FolderJobResult.Terminal terminal &&
            terminal.Results[0] is FolderMemberResult.Receipt { Ordinal: 0 } &&
            terminal.Results[1] is FolderMemberResult.Unknown unknown && unknown.Ordinal == 1,
            "Mixed folder receipts retain immutable wire ordinals.");
        fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.DeleteBatchCheck, _ => { },
            Refusal("unrelated/", new Dictionary<string, object> { [DropboxProtocol.Tag] = DropboxProtocol.InvalidAsyncJobId }),
            HttpStatusCode.Conflict));
        await ThrowsAsync(typeof(ItemFailedException), () => fixture.Writer.ReadFolderRemovalAsync("job", PairCount,
            new RequestAttempt(new object(), AttemptOwner.Recovery), default), cause: TextKey.FailDropboxDeleteJobInvalid);
        Check.Require(fixture.Handler.Requests == PairCount, "An invalid job read does not resend the mutation.");
        fixture.Done();
        foreach (var failed in new[] { false, true })
        {
            using var jobFixture = new Fixture();
            jobFixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.DeleteBatchCheck, _ => { }, new Dictionary<string, object>
            {
                [DropboxProtocol.Tag] = failed ? DropboxProtocol.TagFailed : DropboxProtocol.TagComplete,
                [DropboxProtocol.Entries] = Array.Empty<object>()
            }));
            var attempt = new RequestAttempt(new object(), AttemptOwner.Recovery);
            var observation = await jobFixture.Writer.ReadFolderRemovalAsync("job", PairCount, attempt, default);
            Check.Require(observation is FolderJobResult.Terminal finished && finished.Results.Count == PairCount &&
                finished.Results[0] is FolderMemberResult.Unknown { Ordinal: 0 } &&
                finished.Results[1] is FolderMemberResult.Unknown member && member.Ordinal == 1 &&
                attempt.Completion?.Result == RequestResult.Success,
                "Explicit failure or invalid member count keeps the durable wire count and unknown ordinals.");
            Check.Require(jobFixture.Handler.Requests == 1, "A terminal observation makes no mutation or replay call.");
            jobFixture.Done();
        }
    }

    private static async Task MalformedRepliesAsync()
    {
        using (var fixture = new Fixture())
        {
            using var content = new SizedStream(DropboxApiLimits.SingleUploadMaxBytes + 1);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionStart, _ => { }, new { },
                BodyLength: DropboxApiLimits.SingleUploadMaxBytes));
            await MalformedAsync("Child", typeof(KeyNotFoundException),
                () => fixture.Writer.StageAsync("Child", content, Source(content.Length), default));
            Check.Require(fixture.Handler.Requests == 1, "A malformed session start is not replayed.");
            Check.Require(content.CanRead, "A malformed session reply leaves the caller's content open.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            using var content = new SizedStream(DropboxApiLimits.SingleUploadMaxBytes + 1);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionStart, _ => { }, new { },
                BodyLength: DropboxApiLimits.SingleUploadMaxBytes, Raw: "{not-json"));
            await MalformedAsync("Child", typeof(JsonException),
                () => fixture.Writer.StageAsync("Child", content, Source(content.Length), default), raw: "{not-json");
            Check.Require(fixture.Handler.Requests == 1, "An undecodable session start is not replayed.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            using var content = new SizedStream(OneByte);
            await using var work = await fixture.Limiter.EnterWorkAsync(true, default);
            await using var staged = await fixture.Writer.StageAsync("Child", content, Source(), default);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.Upload, _ => { }, new { }, BodyLength: OneByte));
            await MalformedAsync("Child", typeof(KeyNotFoundException),
                () => fixture.Writer.PublishAsync(staged, null, null, default));
            Check.Require(fixture.Handler.Requests == 1, "A malformed upload is not replayed.");
            using var later = new SizedStream(OneByte);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.Upload, _ => { },
                Metadata("/Parent/Sub/Later", tagged: false), BodyLength: OneByte));
            var next = await fixture.Writer.PublishAsync(DropboxStaged.Single("Sub/Later", later, Source()), null, null, default);
            Check.Require(next.Final!.Path == "Sub/Later", "A later item still publishes after a malformed reply.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            await using var staged = DropboxStaged.Session("Child", "session", OneByte, Source(), OneByteHash);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionFinishBatch, _ => { }, new { }));
            await MalformedAsync("Child", typeof(KeyNotFoundException),
                () => fixture.Writer.PublishAsync(staged, null, null, default));
            Check.Require(fixture.Handler.Requests == 1, "A malformed batch reply is not replayed.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            await using var staged = DropboxStaged.Session("Child", "session", OneByte, Source(), OneByteHash);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionFinishBatch, _ => { }, Batch()));
            await MalformedBatchAsync(() => fixture.Writer.PublishAsync(staged, null, null, default));
            Check.Require(fixture.Handler.Requests == 1, "A batch reply with the wrong member count is not replayed.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.GetMetadata, _ => { }, Error("path/not_found/"),
                HttpStatusCode.Conflict));
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.CreateFolder, _ => { }, new { }));
            await MalformedAsync("Child", typeof(KeyNotFoundException),
                () => fixture.Writer.EnsureFolderAsync("Child", default));
            Check.Require(fixture.Handler.Requests == PairCount, "A malformed create reply is not replayed.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.GetMetadata, _ => { }, Error("path/not_found/"),
                HttpStatusCode.Conflict));
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.CreateFolder, _ => { },
                Metadata(folder: true)));
            await MalformedAsync("Child", typeof(KeyNotFoundException),
                () => fixture.Writer.EnsureFolderAsync("Child", default));
            Check.Require(fixture.Handler.Requests == PairCount,
                "Valid-looking create metadata without the required envelope is rejected and not replayed.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.GetMetadata, _ => { }, Error("path/not_found/"),
                HttpStatusCode.Conflict));
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.CreateFolder, _ => { }, new { }, Raw: "{not-json"));
            await MalformedAsync("Child", typeof(JsonException),
                () => fixture.Writer.EnsureFolderAsync("Child", default), raw: "{not-json");
            Check.Require(fixture.Handler.Requests == PairCount, "An undecodable create reply is not replayed.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.Move, _ => { }, new { }));
            await MalformedAsync("Other", typeof(KeyNotFoundException),
                () => fixture.Writer.MoveAsync(Source(), "Other", default));
            Check.Require(fixture.Handler.Requests == 1, "A malformed move reply is not replayed.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.Move, _ => { }, Metadata()));
            await MalformedAsync("Other", typeof(KeyNotFoundException),
                () => fixture.Writer.MoveAsync(Source(), "Other", default));
            Check.Require(fixture.Handler.Requests == 1,
                "Valid-looking move metadata without the required envelope is rejected.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            using var content = new SizedStream(DropboxApiLimits.SingleUploadMaxBytes + 1);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionStart, _ => { },
                new { session_id = WrongKindNumber }, BodyLength: DropboxApiLimits.SingleUploadMaxBytes));
            await MalformedAsync("Child", typeof(InvalidOperationException),
                () => fixture.Writer.StageAsync("Child", content, Source(content.Length), default));
            Check.Require(fixture.Handler.Requests == 1, "A wrong-kind session id is not replayed.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            using var content = new SizedStream(DropboxApiLimits.SingleUploadMaxBytes + 1);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionStart, _ => { }, new { },
                BodyLength: DropboxApiLimits.SingleUploadMaxBytes, Raw: "[1]"));
            await MalformedAsync("Child", typeof(InvalidOperationException),
                () => fixture.Writer.StageAsync("Child", content, Source(content.Length), default));
            Check.Require(fixture.Handler.Requests == 1, "A root-kind session reply is not replayed.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            using var content = new SizedStream(DropboxApiLimits.SingleUploadMaxBytes + 1);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionStart, _ => { },
                new { session_id = (string?)null }, BodyLength: DropboxApiLimits.SingleUploadMaxBytes));
            await MalformedAsync("Child", typeof(JsonException),
                () => fixture.Writer.StageAsync("Child", content, Source(content.Length), default));
            Check.Require(fixture.Handler.Requests == 1, "A null session id neither appends nor finishes.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            using var content = new SizedStream(DropboxApiLimits.SingleUploadMaxBytes + 1);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionStart, _ => { },
                new { session_id = "" }, BodyLength: DropboxApiLimits.SingleUploadMaxBytes));
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionAppend, arg =>
                Check.Require(arg.GetProperty("cursor").GetProperty("session_id").GetString() == "",
                    "An empty session id is used verbatim."),
                new { }, BodyLength: OneByte, BodyOffset: DropboxApiLimits.SingleUploadMaxBytes));
            await using var staged = await fixture.Writer.StageAsync("Child", content, Source(content.Length), default);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.UploadSessionFinishBatch, arg =>
            {
                var entry = arg.GetProperty(DropboxProtocol.Entries)[0];
                Check.Require(entry.GetProperty("cursor").GetProperty("session_id").GetString() == "",
                    "An empty session id reaches the batch cursor.");
                Commit(entry.GetProperty("commit"), false);
            }, Batch(Finished(size: content.Length, hash: fixture.Handler.ReceivedHash))));
            var result = await fixture.Writer.PublishAsync(staged, null, null, default);
            Check.Require(result.Final!.Id == "id:file", "An empty session id is accepted at the writer boundary.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            using var content = new SizedStream(OneByte);
            await using var work = await fixture.Limiter.EnterWorkAsync(true, default);
            await using var staged = await fixture.Writer.StageAsync("Child", content, Source(), default);
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.Upload, _ => { }, new Dictionary<string, object>
            {
                [DropboxProtocol.Tag] = DropboxProtocol.TagDeleted,
                [DropboxProtocol.PathDisplay] = "/Parent/Child"
            }, BodyLength: OneByte));
            await MalformedAsync("Child", typeof(JsonException),
                () => fixture.Writer.PublishAsync(staged, null, null, default));
            Check.Require(fixture.Handler.Requests == 1, "Deleted upload metadata is not replayed.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.Move, _ => { }, new { metadata = WrongKindNumber }));
            await MalformedAsync("Other", typeof(InvalidOperationException),
                () => fixture.Writer.MoveAsync(Source(), "Other", default));
            Check.Require(fixture.Handler.Requests == 1, "A wrong-kind move metadata value is not replayed.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.Delete, _ => { }, new { }, Raw: "{not-json"));
            await MalformedAsync("Child", typeof(JsonException),
                () => fixture.Writer.DeleteAsync(Source(), null, default), raw: "{not-json");
            Check.Require(fixture.Handler.Requests == 1, "A malformed delete success is not replayed.");
            fixture.Done();
        }

        using (var fixture = new Fixture())
        {
            var transport = new InvalidOperationException("transport");
            fixture.Handler.Steps.Enqueue(new Step(DropboxProtocol.Upload, _ => throw transport, new { }, BodyLength: OneByte));
            using var content = new SizedStream(OneByte);
            try
            {
                await fixture.Writer.PublishAsync(DropboxStaged.Single("Child", content, Source()), null, null, default);
                Check.Require(false, "A transport-side programming exception must not become an item failure.");
            }
            catch (Exception error)
            {
                Check.Require(ReferenceEquals(error, transport),
                    "A transport-side programming exception passes through unchanged.");
            }
            Check.Require(fixture.Handler.Requests == 1, "The failed upload is not replayed.");
            fixture.Done();
        }
    }

    private static async Task MalformedAsync(string path, Type inner, Func<Task> action, string? raw = null)
    {
        try
        {
            await action();
        }
        catch (ItemFailedException failure)
        {
            Check.Require(failure.Path == path && failure.Cause == TextKey.DropboxRefused,
                "A malformed successful reply becomes the item failure for that path.");
            var cause = failure.InnerException;
            if (cause?.GetType() == typeof(JsonException) && cause.InnerException is { } extraction)
            {
                Check.Require(cause.Message == new JsonException().Message,
                    "The Writer's decode wrapper carries the decoder's default JSON message and the extraction cause.");
                Check.Require(extraction.GetType() == inner && extraction.InnerException is null,
                    "The decode wrapper holds exactly the expected extraction exception, one level deep.");
            }
            else if (raw is not null)
            {
                Check.Require(cause is JsonException && cause.InnerException is null,
                    "An unparseable body keeps a JSON decoding failure; lexer wording is not a contract.");
            }
            else
            {
                Check.Require(cause is not null && cause.GetType() == inner &&
                    cause.Message == new JsonException().Message && cause.InnerException is null,
                    "An engine JsonException is the item failure cause directly, at the default message.");
            }
            return;
        }
        Check.Require(false, "A malformed successful reply must fail the item.");
    }

    private static async Task ThrowsAsync(Type expected, Func<Task> action, Exception? transport = null,
        TextKey cause = TextKey.DropboxRefused)
    {
        try { await action(); }
        catch (Exception error)
        {
            Check.Require(error.GetType() == expected, "Expected " + expected.Name + ", got " + error.GetType().Name + ".");
            if (error is ItemFailedException failure)
                Check.Require(failure.Cause == cause &&
                    (transport is null ? failure.InnerException is DropboxApiException : ReferenceEquals(failure.InnerException, transport)),
                    "Provider refusal preserves the provider or transport error and cause.");
            return;
        }
        Check.Require(false, "Expected " + expected.Name + ".");
    }

    private static Step MoveStep(string from, string to, bool folder) => new(DropboxProtocol.Move, arg =>
    {
        Check.Require(arg.GetProperty("from_path").GetString() == from && arg.GetProperty("to_path").GetString() == to,
            "Move uses the expected source and destination, including the case-rename intermediate.");
        Check.Require(!arg.GetProperty("autorename").GetBoolean() && !arg.GetProperty("allow_ownership_transfer").GetBoolean(),
            "Move disables autorename and ownership transfer.");
    }, new { metadata = Metadata(to, folder) });

    private static void Path(JsonElement arg, string expected) =>
        Check.Require(arg.GetProperty("path").GetString() == expected, "Request path maps the pair root.");

    private static void Cursor(JsonElement arg, long offset) =>
        Check.Require(arg.GetProperty("session_id").GetString() == "session" && arg.GetProperty("offset").GetInt64() == offset,
            "Session cursor uses the session id and exact byte offset.");

    private static void Commit(JsonElement arg, bool replace)
    {
        Path(arg, "/Parent/Child");
        var mode = arg.GetProperty("mode");
        Check.Require(replace
            ? mode.GetRawText() == "{\".tag\":\"update\",\"update\":\"old-rev\"}"
            : mode.GetRawText() == "\"add\"",
            "Commit mode serialises the tagged update union with .tag first, or the add string.");
        Check.Require(replace ? mode.GetProperty(".tag").GetString() == "update" && mode.GetProperty("update").GetString() == "old-rev"
            : mode.GetString() == "add", "Commit mode is add or revision-conditional update.");
        Check.Require(!arg.GetProperty("autorename").GetBoolean() && arg.GetProperty("strict_conflict").GetBoolean(),
            "Commit prevents silent rename and enables strict conflicts.");
        Check.Require(arg.GetProperty("client_modified").GetString() == "2024-01-02T01:04:05Z",
            "Commit time uses UTC seconds in the provider format.");
    }

    // finish_batch_v2 success: the file's metadata fields sit beside .tag=success (P5 F06), not under a wrapper.
    private static object Finished(string path = "/Parent/Child", long size = OneByte, string? hash = null)
    {
        var fields = (Dictionary<string, object>)Metadata(path, tagged: false, size: size, hash: hash);
        fields[DropboxProtocol.Tag] = DropboxProtocol.TagSuccess;
        return fields;
    }

    private static object Failed(string writeTag) => new Dictionary<string, object>
    {
        [DropboxProtocol.Tag] = DropboxProtocol.TagFailure,
        [DropboxProtocol.TagFailure] = SessionFailure(writeTag)
    };

    private static object Batch(params object[] entries) => new { entries };

    // A direct upload's UploadError carries UploadWriteFailed's WriteError inline under reason (P5 C14/U01).
    private static object DirectFailure(string writeTag) => new Dictionary<string, object>
    {
        [DropboxProtocol.Tag] = DropboxProtocol.Path,
        [DropboxProtocol.Reason] = new Dictionary<string, object> { [DropboxProtocol.Tag] = writeTag }
    };

    // A session finish member's UploadSessionFinishError nests the WriteError under path (P5 C10).
    private static object SessionFailure(string writeTag) => new Dictionary<string, object>
    {
        [DropboxProtocol.Tag] = DropboxProtocol.Path,
        [DropboxProtocol.Path] = new Dictionary<string, object> { [DropboxProtocol.Tag] = writeTag }
    };

    private static object Refusal(string summary, object failure) => new { error_summary = summary, error = failure };

    private static void BatchEntry(JsonElement arg, long offset, bool replace)
    {
        var entries = arg.GetProperty(DropboxProtocol.Entries);
        Check.Require(entries.GetArrayLength() == 1, "One staged session is one batch member.");
        var entry = entries[0];
        Cursor(entry.GetProperty("cursor"), offset);
        Commit(entry.GetProperty("commit"), replace);
    }

    private static async Task RequiresMappedAsync(Type expected, Func<Task> action, TextKey cause,
        bool providerEvidence = true)
    {
        try
        {
            await action();
        }
        catch (Exception error)
        {
            Check.Require(error.GetType() == expected, "Expected " + expected.Name + ", got " + error.GetType().Name + ".");
            switch (error)
            {
                case PathOccupiedException occupied:
                    Check.Require(occupied.Path == "Child", "A structured refusal names the exact item.");
                    break;
                case ChangedUnderneathException changed:
                    Check.Require(changed.Path == "Child", "A structured refusal names the exact item.");
                    break;
                case ItemFailedException failed:
                    Check.Require(failed.Cause == cause && (providerEvidence
                            ? failed.InnerException is ProviderFailureException : failed.InnerException is null),
                        "A refusal retains its mapped cause and only structured provider refusals carry provider evidence.");
                    break;
            }
            return;
        }
        Check.Require(false, "Expected " + expected.Name + ".");
    }

    private static async Task MalformedBatchAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception error)
        {
            Check.Require(error is ItemFailedException { Path: "Child", Cause: TextKey.DropboxRefused, InnerException: JsonException },
                "A batch reply with the wrong member count is a decode failure and is never replayed.");
            return;
        }
        Check.Require(false, "A batch reply with the wrong member count must fail the item.");
    }

    private static Entry Source(long length = OneByte) => new("Child", false, length,
        SourceTime, "id:source", "old-rev", OneByteHash, null);

    // Concrete upload/create results omit .tag; get_metadata and move_v2 return tagged Metadata.
    private static object Metadata(string path = "/Parent/Child", bool folder = false, bool tagged = true, long size = OneByte, string? hash = null)
    {
        var fields = new Dictionary<string, object>
        {
            ["name"] = path.Split('/').Last(), ["path_lower"] = path.ToLowerInvariant(),
            ["path_display"] = path, ["id"] = folder ? "id:folder" : "id:file"
        };
        if (tagged) fields[".tag"] = folder ? "folder" : "file";
        if (!folder)
        {
            fields["rev"] = "new-rev";
            fields["size"] = size;
            fields["client_modified"] = "2024-01-02T01:04:05Z";
            fields["server_modified"] = "2024-01-02T01:04:06Z";
            fields["is_downloadable"] = true;
            fields["content_hash"] = hash ?? OneByteHash;
        }
        return fields;
    }

    private static object Error(string summary) => new { error_summary = summary };

    private sealed record Step(string Route, Action<JsonElement> Inspect, object Reply,
        HttpStatusCode Status = HttpStatusCode.OK, long? BodyLength = null, long BodyOffset = 0, string? Raw = null,
        Task? Release = null);

    private sealed class Fixture : IDisposable
    {
        public readonly Handler Handler = new();
        public readonly List<WaitEvent> Waits = new();
        public readonly ManualTime Time = new();
        public Action<WaitEvent>? Notify;
        public readonly AdaptiveLimiter Limiter;
        public readonly EngineSettings Settings = new("{name}", "yyyyMMdd", "local", "dropbox", ".case-rename",
            RecycleFallback.Fail, LogRetentionDays, FirstRunPolicy.NewestWins, InitialConcurrency, "", LogBufferCharacters, ScanTestSettings.BatchRows)
        {
        JournalPageBufferBytes = ScanTestSettings.JournalPageBufferBytes,
        LogReadBufferCharacters = ScanTestSettings.LogReadBufferCharacters,
            Backoff = new FailureRetryOwner.BackoffSettings(BackoffInitialSeconds, BackoffFactor, 0, BackoffMaximumSeconds),
        };
        public readonly DropboxWriter Writer;
        private readonly HttpClient _http;

        public Fixture(string root = "/Parent")
        {
            _http = new HttpClient(Handler);
            var pair = new PairConfig("pair", "Pair A", "C:\\Fixture", root, "connection", [], [], FirstRunPolicy.NewestWins, true, null);
            var network = new FakeNetworkSignal();
            network.SetAvailable(true);
            var time = Time;
            Limiter = new AdaptiveLimiter(InitialConcurrency, InitialConcurrency, time, network, waited =>
            {
                Waits.Add(waited);
                Notify?.Invoke(waited);
            }, Settings.Backoff);
            var api = new DropboxApi(_http, new Tokens(), "namespace", Limiter,
                _ => ValueTask.FromResult<Stream>(new MemoryStream()), Settings.Backoff, time, Guid.NewGuid().ToString(), "source");
            Writer = new DropboxWriter(api, pair, Settings, new DropboxListing(api, pair, new PathScope(pair, [pair])));
        }

        public void Done() => Check.Require(Handler.Steps.Count == 0, "All expected HTTP requests occurred, with no extra request.");
        public void Dispose() => _http.Dispose();
    }

    private sealed class Tokens : IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("fixture-token");
    }

    private sealed class Handler : HttpMessageHandler
    {
        public readonly Queue<Step> Steps = new();
        public int Requests;
        public string? ReceivedHash;
        private ContentBlockHasher? _received;

        protected override void Dispose(bool disposing)
        {
            if (disposing) _received?.Dispose();
            base.Dispose(disposing);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Check.Require(Steps.Count != 0, "No unexpected HTTP request.");
            var step = Steps.Dequeue();
            var contentRoute = step.Route == DropboxProtocol.Download || step.Route == DropboxProtocol.Upload ||
                step.Route == DropboxProtocol.UploadSessionStart || step.Route == DropboxProtocol.UploadSessionAppend;
            var uri = request.RequestUri!;
            Check.Require(request.Method == HttpMethod.Post && uri.AbsolutePath == "/2/" + step.Route &&
                uri.Host == (contentRoute ? "content.dropboxapi.com" : "api.dropboxapi.com"),
                "Writer uses the route's POST endpoint and host: expected /2/" + step.Route + " but sent " + uri.AbsolutePath);
            Check.Require(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization?.Parameter == "fixture-token",
                "Writer requests use the token source.");
            using var root = JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Path-Root").Single());
            Check.Require(root.RootElement.GetProperty(".tag").GetString() == "root" &&
                root.RootElement.GetProperty("root").GetString() == "namespace", "Writer requests use the configured namespace.");
            var json = contentRoute ? request.Headers.GetValues("Dropbox-API-Arg").Single()
                : await request.Content!.ReadAsStringAsync(cancellationToken);
            using var arg = JsonDocument.Parse(json);
            if (contentRoute && step.Route != DropboxProtocol.Download)
            {
                Check.Require(request.Content!.Headers.ContentLength is not null, "Upload length is declared.");
            }
            if ((step.BodyLength ?? (contentRoute && step.Route != DropboxProtocol.Download
                ? request.Content!.Headers.ContentLength : null)) is { } length)
            {
                if (step.Route is DropboxProtocol.Upload or DropboxProtocol.UploadSessionStart)
                {
                    _received?.Dispose();
                    _received = new ContentBlockHasher();
                }
                using var sink = new ReceivingStream(_received!, step.BodyOffset);
                await request.Content!.CopyToAsync(sink, cancellationToken);
                Check.Require(sink.Length == length, "The serializer delivers the exact piece length.");
                if (step.Route == DropboxProtocol.Upload || arg.RootElement.GetProperty("close").GetBoolean())
                    ReceivedHash = _received!.Finish();
            }
            step.Inspect(arg.RootElement);
            if (step.Release is not null) await step.Release.WaitAsync(cancellationToken);
            var reply = step.Reply is Func<object> create ? create() : step.Reply;
            return new HttpResponseMessage(step.Status)
            {
                Content = new StringContent(step.Raw ?? (step.Route == DropboxProtocol.Download && reply is string text
                    ? text : JsonSerializer.Serialize(reply)), Encoding.UTF8, "application/json")
            };
        }
    }

    private static string Digest(int length)
    {
        using var hash = new ContentBlockHasher();
        hash.Append(new byte[length]);
        return hash.Finish();
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }

    // The receiver hashes serialized bytes without buffering the large logical inputs.
    private sealed class ReceivingStream(ContentBlockHasher hash, long offset) : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int start, int count) => Write(buffer.AsSpan(start, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check.Require(buffer.IndexOfAnyExcept((byte)(offset / DropboxApiLimits.SingleUploadMaxBytes)) < 0,
                "Every serialized byte in this bounded piece comes from the expected absolute source offset.");
            hash.Append(buffer);
            _length += buffer.Length;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // Large logical inputs exercise maximum-size slicing without retaining the transmitted bytes.
    private sealed class SizedStream(long length) : Stream
    {
        private bool _disposed;
        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public long BytesRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, length - Position);
            for (var index = 0; index < read; index++)
                buffer[offset + index] = (byte)((Position + index) / DropboxApiLimits.SingleUploadMaxBytes);
            Position += read;
            BytesRead += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, length - Position);
            for (var index = 0; index < read; index++)
                buffer.Span[index] = (byte)((Position + index) / DropboxApiLimits.SingleUploadMaxBytes);
            Position += read;
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
    }
}
