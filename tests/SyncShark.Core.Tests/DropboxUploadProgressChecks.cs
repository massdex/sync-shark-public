using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Massdex.SyncShark;

// SS-D07 proof for the Dropbox upload-session progress budget. The fixture below supplies only the
// provider's HTTP acknowledgement stream and the caller's checkpoint journal; every accept, rewind,
// replacement, refusal and commit decision is the real production decision in UploadSessionAsync,
// reached through the real UploadResumableAsync entry point. No decision is re-implemented here and
// the shared SyncUploadProgressBudget is consumed from its own Core file, never duplicated.
internal static class DropboxUploadProgressChecks
{
    private const string Leaf = "wire.bin";
    private const int ChunkBytes = 4;

    internal static async Task RunAsync(string root)
    {
        var isolated = Path.GetFullPath(root);
        Directory.CreateDirectory(isolated);
        var journal = new List<string>();

        // SS-D07 requirement 3: many valid increasing short offsets complete exact bytes/hash.
        await ManyValidIncreasingShortOffsetsCompleteAsync(journal);
        // SS-D07 requirement 3: a valid correction of a tentative retained checkpoint still progresses.
        await RetainedTentativeCheckpointRewindsOnceAsync(journal);
        // SS-D07 requirement 1: repeated and alternating nonadvancing offsets stop at eight.
        await RepeatedCorrectedOffsetIsBoundedAsync(journal);
        await AlternatingLowerOffsetsAreBoundedAsync(journal);
        // SS-D07 requirement 2: two replacements restart safely; the third refuses recoverably.
        await TwoUnavailableSessionsRestartSafelyAsync(journal);
        await ThirdUnavailableSessionRefusesRecoverablyAsync(journal);
        // SS-D07 requirement 4: a lost finish response or session never restarts the logical effect.
        foreach (var mode in new[] { "finish-transport-loss", "finish-torn-body", "finish-session-gone" })
            await LostFinishNeverRestartsAsync(mode, journal);

        var journalPath = Path.Combine(isolated, "dropbox-upload-progress-journal.json");
        await File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(new
        {
            production_entry = "DropboxPilotClient.UploadResumableAsync -> UploadSessionAsync",
            consumed_shared_contract = "SyncUploadProgressBudget (google-protocol lane); no duplicate implementation",
            fixture_limit = "fakes supply the provider acknowledgement stream and the checkpoint journal only",
            cases = journal
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS dropbox upload-session progress budget: " + string.Join(" | ", journal));
        Console.WriteLine("PASS dropbox upload-session progress journal: " + journalPath);
    }

    private static async Task ManyValidIncreasingShortOffsetsCompleteAsync(List<string> journal)
    {
        var source = Payload(64);
        var hash = DropboxContentHash.Compute(source);
        var fixture = new ProgressFixture("progress");
        using var harness = Open(fixture);
        var checkpoints = new List<SyncUploadSessionCheckpoint>();
        var result = await harness.Client.UploadResumableAsync(Leaf, new MemoryStream(source, writable: false),
            hash, null, null, Persist(checkpoints), CancellationToken.None);
        Require(fixture.StartCount == 1 && fixture.AppendCount == 14 && fixture.FinishCount == 1,
            "SS-D07-3 many increasing short offsets: the production loop sent exactly one session, fourteen increasing appends and one finish");
        Require(fixture.CorrectionCount == 0 && fixture.NonProgressReplies == 0,
            "SS-D07-3 many increasing short offsets: fourteen increasing acknowledgements are never treated as nonprogress");
        Require(result.Size == source.Length && string.Equals(result.ContentHash, hash, StringComparison.OrdinalIgnoreCase),
            "SS-D07-3 many increasing short offsets: the accepted result carries the exact length and content hash");
        Require(fixture.Committed.SequenceEqual(source),
            "SS-D07-3 many increasing short offsets: the provider received the exact bytes");
        Require(checkpoints.Count == fixture.AppendCount + 1 &&
                checkpoints[^1].ConfirmedOffset == source.Length - ChunkBytes,
            "SS-D07-3 many increasing short offsets: every authoritative append offset was persisted before the next data send");
        journal.Add("SS-D07-3 many increasing short offsets: 14 appends, 1 finish, exact bytes/hash, 0 nonprogress observations");
    }

    private static async Task RetainedTentativeCheckpointRewindsOnceAsync(List<string> journal)
    {
        var source = Payload(16);
        var hash = DropboxContentHash.Compute(source);
        var fixture = new ProgressFixture("progress");
        // The retained session only ever received the first four bytes, so the retained offset eight is
        // tentative and the provider's own acknowledgement is the first authoritative offset.
        fixture.Seed("session-1", source[..4]);
        using var harness = Open(fixture);
        var checkpoints = new List<SyncUploadSessionCheckpoint>();
        var result = await harness.Client.UploadResumableAsync(Leaf, new MemoryStream(source, writable: false),
            hash, null, new SyncUploadSessionCheckpoint("session-1", 8), Persist(checkpoints),
            CancellationToken.None);
        Require(fixture.StartCount == 0 && fixture.CorrectionCount == 1 && fixture.Corrections[0] == 4,
            "SS-D07-3 retained tentative checkpoint: one valid correction rewinds the tentative high-water to the authoritative offset without a new session");
        Require(fixture.FinishCount == 1 && fixture.Committed.SequenceEqual(source) && result.Size == source.Length &&
                string.Equals(result.ContentHash, hash, StringComparison.OrdinalIgnoreCase),
            "SS-D07-3 retained tentative checkpoint: the rewound transfer continues to the exact bytes/hash");
        Require(checkpoints.Count >= 3 && checkpoints[0].ConfirmedOffset == 4 &&
                checkpoints[^1].ConfirmedOffset == source.Length - ChunkBytes,
            "SS-D07-3 retained tentative checkpoint: the rewound offset is persisted before the next data send");
        journal.Add("SS-D07-3 retained tentative checkpoint: the first authoritative offset (4) rewound the tentative 8, then completed exactly with no new session");
    }

    private static async Task RepeatedCorrectedOffsetIsBoundedAsync(List<string> journal)
    {
        var source = Payload(16);
        var hash = DropboxContentHash.Compute(source);
        var fixture = new ProgressFixture("repeat");
        using var harness = Open(fixture);
        var checkpoints = new List<SyncUploadSessionCheckpoint>();
        var failure = await CaptureAsync(() => harness.Client.UploadResumableAsync(Leaf,
            new MemoryStream(source, writable: false), hash, null, null, Persist(checkpoints),
            CancellationToken.None));
        Require(failure is DropboxEffectUncertainException,
            "SS-D07-1 repeated corrected offset: the bounded exit is the exact uncertain provider error, not a success or a retry loop");
        Require(fixture.NonProgressReplies == 8,
            "SS-D07-1 repeated corrected offset: exactly eight nonprogress acknowledgements were tolerated before the bounded exit");
        Require(fixture.Corrections.Count == 8 && fixture.Corrections.Distinct().Count() == 1 &&
                fixture.Corrections[0] == fixture.BoundOffset,
            "SS-D07-1 repeated corrected offset: every reply repeated the same acknowledged offset without a greater high-water");
        Require(fixture.StartCount == 1 && fixture.FinishCount == 0,
            "SS-D07-1 repeated corrected offset: no replacement session and no fabricated finish");
        Require(checkpoints.Count > 0 && checkpoints[^1].ConfirmedOffset == fixture.BoundOffset &&
                checkpoints[^1].ConfirmedOffset <= source.Length,
            "SS-D07-1 repeated corrected offset: the retained checkpoint keeps the last acknowledged offset inside the source");
        Require(ExposesNoCredential(failure!),
            "SS-D07-1 repeated corrected offset: the retained reason carries no credential material");
        journal.Add("SS-D07-1 repeated corrected offset: 8 identical nonadvancing acknowledgements exit uncertain with the checkpoint retained");
    }

    private static async Task AlternatingLowerOffsetsAreBoundedAsync(List<string> journal)
    {
        var source = Payload(16);
        var hash = DropboxContentHash.Compute(source);
        var fixture = new ProgressFixture("alternating");
        using var harness = Open(fixture);
        var checkpoints = new List<SyncUploadSessionCheckpoint>();
        var failure = await CaptureAsync(() => harness.Client.UploadResumableAsync(Leaf,
            new MemoryStream(source, writable: false), hash, null, null, Persist(checkpoints),
            CancellationToken.None));
        Require(failure is DropboxEffectUncertainException,
            "SS-D07-1 alternating lower offsets: the bounded exit is the exact uncertain provider error");
        Require(fixture.NonProgressReplies == 8 && fixture.Corrections.Count == 8,
            "SS-D07-1 alternating lower offsets: exactly eight nonadvancing acknowledgements were tolerated");
        Require(fixture.Corrections.Distinct().Order().SequenceEqual(new[] { fixture.BoundOffset - 4, fixture.BoundOffset }) &&
                fixture.Corrections.Where((value, index) => index % 2 == 0).All(value => value == fixture.BoundOffset) &&
                fixture.Corrections.Where((value, index) => index % 2 == 1).All(value => value == fixture.BoundOffset - 4),
            "SS-D07-1 alternating lower offsets: the alternating replies never exceeded the bound high-water");
        Require(fixture.StartCount == 1 && fixture.FinishCount == 0,
            "SS-D07-1 alternating lower offsets: no replacement session and no fabricated finish");
        Require(checkpoints.Count > 0 && checkpoints[^1].ConfirmedOffset <= source.Length,
            "SS-D07-1 alternating lower offsets: the retained checkpoint stays inside the source");
        Require(ExposesNoCredential(failure!),
            "SS-D07-1 alternating lower offsets: the retained reason carries no credential material");
        journal.Add("SS-D07-1 alternating lower offsets: 8 alternating nonadvancing acknowledgements exit uncertain with the checkpoint retained");
    }

    private static async Task TwoUnavailableSessionsRestartSafelyAsync(List<string> journal)
    {
        var source = Payload(16);
        var hash = DropboxContentHash.Compute(source);
        var fixture = new ProgressFixture("two-unavailable");
        using var harness = Open(fixture);
        var checkpoints = new List<SyncUploadSessionCheckpoint>();
        var result = await harness.Client.UploadResumableAsync(Leaf, new MemoryStream(source, writable: false),
            hash, null, null, Persist(checkpoints), CancellationToken.None);
        Require(fixture.StartCount == 3 && fixture.UnavailableReplies == 2,
            "SS-D07-2 two unavailable sessions: exactly two bounded replacements were made and both restarted safely");
        Require(checkpoints.Any(item => item.SessionId == "session-2" && item.ConfirmedOffset == 4) &&
                checkpoints.Any(item => item.SessionId == "session-3" && item.ConfirmedOffset == 4),
            "SS-D07-2 two unavailable sessions: each exact replacement session was persisted before continuation");
        Require(fixture.FinishCount == 1 && fixture.Committed.SequenceEqual(source) && result.Size == source.Length &&
                string.Equals(result.ContentHash, hash, StringComparison.OrdinalIgnoreCase),
            "SS-D07-2 two unavailable sessions: the restarted transfer commits the exact bytes/hash");
        journal.Add("SS-D07-2 two unavailable sessions: 2 replacements persisted then completed exactly (3 sessions, 1 finish)");
    }

    private static async Task ThirdUnavailableSessionRefusesRecoverablyAsync(List<string> journal)
    {
        var source = Payload(16);
        var hash = DropboxContentHash.Compute(source);
        var fixture = new ProgressFixture("third-refuses");
        using var harness = Open(fixture);
        var checkpoints = new List<SyncUploadSessionCheckpoint>();
        var failure = await CaptureAsync(() => harness.Client.UploadResumableAsync(Leaf,
            new MemoryStream(source, writable: false), hash, null, null, Persist(checkpoints),
            CancellationToken.None));
        Require(failure is DropboxEffectUncertainException,
            "SS-D07-2 third unavailable session: the exact refusal is the uncertain provider error, never a fabricated success");
        Require(fixture.StartCount == 4 && fixture.FinishCount == 0,
            "SS-D07-2 third unavailable session: the refusal happens at the third replacement with no finish attempted");
        Require(checkpoints.Count > 0 && checkpoints[^1].SessionId == "session-4" &&
                checkpoints[^1].ConfirmedOffset == 4 && checkpoints[^1].ConfirmedOffset <= source.Length,
            "SS-D07-2 third unavailable session: the retained checkpoint names the newest live session with an offset inside the source");
        Require(ExposesNoSessionIdentity(failure!),
            "SS-D07-2 third unavailable session: the secret-free reason exposes no session identity or credential");
        journal.Add("SS-D07-2 third unavailable session: replacement 3 refused as uncertain with the live session checkpoint retained and no finish");
    }

    private static async Task LostFinishNeverRestartsAsync(string mode, List<string> journal)
    {
        var source = Payload(16);
        var hash = DropboxContentHash.Compute(source);
        var fixture = new ProgressFixture(mode);
        using var harness = Open(fixture);
        var checkpoints = new List<SyncUploadSessionCheckpoint>();
        var failure = await CaptureAsync(() => harness.Client.UploadResumableAsync(Leaf,
            new MemoryStream(source, writable: false), hash, null, null, Persist(checkpoints),
            CancellationToken.None));
        Require(failure is DropboxEffectUncertainException,
            "SS-D07-4 " + mode + ": the lost finish outcome stays the exact uncertain provider error");
        Require(fixture.FinishCount == 1 && fixture.RequestCount("/upload_session/finish") == 1,
            "SS-D07-4 " + mode + ": exactly one finish was transmitted and none was duplicated");
        Require(fixture.StartCount == 1 && fixture.AppendCount == 2,
            "SS-D07-4 " + mode + ": the logical effect was never restarted and no chunk was replayed");
        if (mode != "finish-session-gone")
            Require(fixture.Committed.SequenceEqual(source),
                "SS-D07-4 " + mode + ": the fixture really did accept the commit, so the refusal is honest uncertainty rather than a no-effect guess");
        journal.Add("SS-D07-4 " + mode + ": 1 finish, no restart, no duplicate mutation, uncertain outcome retained");
    }

    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++) bytes[index] = (byte)(index * 7 + 3);
        return bytes;
    }

    private static Func<SyncUploadSessionCheckpoint, CancellationToken, Task> Persist(
        List<SyncUploadSessionCheckpoint> checkpoints) =>
        (checkpoint, _) =>
        {
            checkpoints.Add(checkpoint);
            return Task.CompletedTask;
        };

    private static async Task<Exception?> CaptureAsync(Func<Task<PilotUploadResult>> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static Harness Open(ProgressFixture fixture) => new(fixture);

    private static bool ExposesNoCredential(Exception error) =>
        !error.ToString().Contains("test-token", StringComparison.Ordinal);

    private static bool ExposesNoSessionIdentity(Exception error) =>
        ExposesNoCredential(error) && !Regex.IsMatch(error.ToString(), @"session-\d");

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class Harness : IDisposable
    {
        internal Harness(ProgressFixture fixture)
        {
            Fixture = fixture;
            Http = new HttpClient(fixture, disposeHandler: false);
            var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes);
            Client = new DropboxPilotClient(Http, new FixedTokenSource(), "/Pilot", binding, "id:pilot-folder",
                uploadSessionThresholdBytes: 1, uploadSessionChunkSizeBytes: ChunkBytes);
        }

        internal ProgressFixture Fixture { get; }
        internal HttpClient Http { get; }
        internal DropboxPilotClient Client { get; }

        public void Dispose() => Http.Dispose();
    }

    private sealed class TornBody : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("simulated torn finish response body");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("simulated torn finish response body"));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ProgressFixture(string mode) : HttpMessageHandler
    {
        private readonly Dictionary<string, MemoryStream> _sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _appendsBySession = new(StringComparer.Ordinal);
        private readonly HashSet<string> _unavailable = new(StringComparer.Ordinal);
        private int _nextSession;
        private bool _baselineBound;

        internal int StartCount { get; private set; }
        internal int AppendCount { get; private set; }
        internal int FinishCount { get; private set; }
        internal int CorrectionCount { get; private set; }
        internal int NonProgressReplies { get; private set; }
        internal int UnavailableReplies { get; private set; }
        internal long BoundOffset { get; private set; }
        internal byte[] Committed { get; private set; } = [];
        internal List<long> Corrections { get; } = [];
        internal List<string> Requests { get; } = [];

        internal void Seed(string sessionId, byte[] bytes)
        {
            var session = new MemoryStream();
            session.Write(bytes);
            session.Position = 0;
            _sessions[sessionId] = session;
        }

        internal int RequestCount(string suffix) =>
            Requests.Count(route => route.EndsWith(suffix, StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var route = request.RequestUri!.AbsolutePath;
            Requests.Add(route);
            if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
                return Json("""{"account_id":"account-id","root_info":{"root_namespace_id":"root-namespace","home_namespace_id":"home-namespace"}}""");
            if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
                return Json("""{".tag":"folder","name":"Pilot","id":"id:pilot-folder","path_lower":"/pilot","path_display":"/Pilot"}""");
            if (route.EndsWith("/files/upload_session/start", StringComparison.Ordinal))
            {
                StartCount++;
                var sessionId = "session-" + Interlocked.Increment(ref _nextSession);
                var session = new MemoryStream();
                await session.WriteAsync(await request.Content!.ReadAsByteArrayAsync(cancellationToken),
                    cancellationToken);
                _sessions[sessionId] = session;
                return Json($"{{\"session_id\":\"{sessionId}\"}}");
            }
            if (route.EndsWith("/files/upload_session/append_v2", StringComparison.Ordinal))
                return await AppendAsync(request, cancellationToken);
            if (route.EndsWith("/files/upload_session/finish", StringComparison.Ordinal))
                return await FinishAsync(request, cancellationToken);
            throw new InvalidOperationException("Unexpected Dropbox upload-session fixture route: " + route);
        }

        private async Task<HttpResponseMessage> AppendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AppendCount++;
            var (sessionId, offset) = Cursor(request);
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            if (!_sessions.TryGetValue(sessionId, out var session)) return MissingSession();
            _appendsBySession.TryGetValue(sessionId, out var sessionAppends);
            sessionAppends++;
            _appendsBySession[sessionId] = sessionAppends;
            if (_unavailable.Contains(sessionId) || sessionAppends > 1 && GoesUnavailable(sessionId))
            {
                _unavailable.Add(sessionId);
                UnavailableReplies++;
                return MissingSession();
            }
            if (mode == "repeat" && _baselineBound) return NonAdvancing(BoundOffset);
            if (mode == "alternating" && _baselineBound)
                return NonAdvancing(NonProgressReplies % 2 == 0 ? BoundOffset : BoundOffset - ChunkBytes);
            if (offset != session.Length) return Correction(session.Length);
            session.Position = session.Length;
            await session.WriteAsync(body, cancellationToken);
            if (!_baselineBound)
            {
                _baselineBound = true;
                BoundOffset = session.Length;
            }
            return Accepted();
        }

        private async Task<HttpResponseMessage> FinishAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            FinishCount++;
            var (sessionId, offset) = Cursor(request);
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            if (mode == "finish-session-gone")
            {
                UnavailableReplies++;
                return MissingSession();
            }
            if (!_sessions.TryGetValue(sessionId, out var session)) return MissingSession();
            if (offset != session.Length) return Correction(session.Length);
            session.Position = session.Length;
            await session.WriteAsync(body, cancellationToken);
            Committed = session.ToArray();
            _sessions.Remove(sessionId);
            session.Dispose();
            if (mode == "finish-transport-loss")
                throw new HttpRequestException("simulated finish acknowledgement loss after acceptance");
            if (mode == "finish-torn-body")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new TornBody())
                };
            return Json($"{{\".tag\":\"file\",\"name\":\"large.bin\",\"id\":\"id:large\",\"rev\":\"rev:large\",\"size\":{Committed.Length},\"content_hash\":\"{DropboxContentHash.Compute(Committed)}\"}}");
        }

        private bool GoesUnavailable(string sessionId)
        {
            if (mode == "third-refuses") return true;
            if (mode != "two-unavailable") return false;
            return int.TryParse(sessionId.AsSpan("session-".Length), out var number) && number <= 2;
        }

        private HttpResponseMessage Correction(long correctOffset)
        {
            CorrectionCount++;
            Corrections.Add(correctOffset);
            return IncorrectOffset(correctOffset);
        }

        private HttpResponseMessage NonAdvancing(long acknowledgedOffset)
        {
            NonProgressReplies++;
            Corrections.Add(acknowledgedOffset);
            return IncorrectOffset(acknowledgedOffset);
        }

        private static (string SessionId, long Offset) Cursor(HttpRequestMessage request)
        {
            using var argument = JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
            var cursor = argument.RootElement.GetProperty("cursor");
            return (cursor.GetProperty("session_id").GetString()!, cursor.GetProperty("offset").GetInt64());
        }

        private static HttpResponseMessage Accepted() =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };

        private static HttpResponseMessage IncorrectOffset(long correctOffset) =>
            new(HttpStatusCode.Conflict)
            {
                Content = new StringContent(
                    $"{{\"error_summary\":\"incorrect_offset/..\",\"error\":{{\".tag\":\"incorrect_offset\",\"correct_offset\":{correctOffset}}}}}",
                    Encoding.UTF8, "application/json")
            };

        private static HttpResponseMessage MissingSession() => new(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                """{"error_summary":"lookup_failed/not_found/..","error":{".tag":"lookup_failed","lookup_failed":{".tag":"not_found"}}}""",
                Encoding.UTF8, "application/json")
        };

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };
    }
}

