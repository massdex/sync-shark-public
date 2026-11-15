using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark;

// Real CLI parser/dispatcher plus the real Dropbox HTTP adapter over scripted responses. Only HTTP bytes
// and fixture filesystem state are synthetic: every account, namespace, parent, absence, effect and
// receipt decision under test stays in production code. No live account, credential, installed app or
// provider effect is touched.
internal static class ExactFileUploadChecks
{
    private const string Account = "dbid:exact-file-fixture";
    private const string Namespace = "exact-file-root";
    private const string ParentPath = "/Documents/Exact";
    private const string ParentId = "id:exact-parent";
    private const string Leaf = "report.csv";
    private const string Destination = "ns:" + Namespace + "//Documents/Exact/report.csv";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("exact-file-upload-payload");
    private static readonly List<string> Failures = [];
    private static int _checks;
    private static string _root = string.Empty;

    private static string SourceSha => Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();
    private static string PayloadBytes => Payload.Length.ToString(CultureInfo.InvariantCulture);

    internal static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The exact-file upload checks need Windows file sharing semantics.");
        if (Directory.Exists(root)) throw new InvalidOperationException("The exact-file upload fixture root must be new.");
        Directory.CreateDirectory(root);
        _root = root; _checks = 0; Failures.Clear();
        try
        {
            await Case("successful exact create-only upload", SuccessAsync);
            await Case("occupied destination is refused with no effect", OccupiedAsync);
            await Case("wrong namespace is refused before any provider request", WrongNamespaceAsync);
            await Case("non-Dropbox and unknown accounts are refused", AccountRefusalAsync);
            await Case("changed source size and hash are refused", SourceIdentityAsync);
            await Case("directory and reparse-point sources are refused", SourceShapeAsync);
            await Case("missing parent is refused with no effect", MissingParentAsync);
            await Case("provider permission failure fails closed", PermissionAsync);
            await Case("malformed parent metadata fails closed", MalformedMetadataAsync);
            await Case("source race after measurement is refused", SourceRaceAsync);
            await Case("timeout before transmission leaves no effect", TimeoutAsync);
            await Case("caller cancellation before transmission leaves no effect", CallerCancellationAsync);
            await Case("I/O failure after transmission is UNKNOWN", PostEffectIoFailureAsync);
            await Case("lost and malformed success after transmission is UNKNOWN", UnknownOutcomeAsync);
            await Case("structured upload rejections keep a safe reason and correlation", UploadRejectionAsync);
            await Case("malformed arguments are usage refusals", ArgumentRejectionsAsync);
            Console.WriteLine(JsonSerializer.Serialize(new { result = Failures.Count == 0 ? "PASS" : "FAIL",
                assertions = _checks, failures = Failures,
                boundary = "Controlled HTTP and fixture NTFS only; no live account, credential, installed app or provider effect." }));
            if (Failures.Count != 0)
                throw new InvalidOperationException("Exact-file upload checks failed: " + string.Join(" | ", Failures));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
    }

    private static async Task Case(string name, Func<Fixture, Task> action)
    {
        try
        {
            await using var fixture = await Fixture.Create(Path.Combine(_root,
                (_checks + Failures.Count + 1).ToString(CultureInfo.InvariantCulture)));
            await action(fixture);
        }
        catch (Exception error)
        {
            Failures.Add(name + ": " + error.GetType().Name + ": " + error.Message);
            Console.WriteLine("FAIL " + Failures[^1]);
        }
    }

    private static void Check(bool condition, string name)
    {
        _checks++;
        if (!condition) { Failures.Add(name); Console.WriteLine("FAIL " + name); }
    }

    private static Task<(int Exit, string Output, string Error)> UploadAsync(Fixture fixture, string source,
        string destination, long bytes, string sha, string? account = null) =>
        fixture.Run("files", "upload", "--account", account ?? fixture.Connection, "--source", source,
            "--destination", destination, "--bytes", bytes.ToString(CultureInfo.InvariantCulture), "--sha256", sha,
            "--yes");

    private static JsonObject Success((int Exit, string Output, string Error) result, string name)
    {
        if (result.Exit != 0)
            throw new InvalidOperationException(name + " exited " + result.Exit + ": " + result.Output + result.Error);
        var envelope = JsonNode.Parse(result.Output)!.AsObject();
        if (!envelope["ok"]!.GetValue<bool>()) throw new InvalidOperationException(name + " did not report ok.");
        return envelope["data"]!.AsObject();
    }

    private static JsonObject Refusal((int Exit, string Output, string Error) result, string name)
    {
        if (result.Exit == 0) throw new InvalidOperationException(name + " was expected to be refused.");
        var envelope = JsonNode.Parse(result.Output)!.AsObject();
        if (envelope["ok"]!.GetValue<bool>()) throw new InvalidOperationException(name + " reported ok.");
        return envelope["data"]?.AsObject() ?? new JsonObject();
    }

    private static async Task SuccessAsync(Fixture fixture)
    {
        var source = fixture.WriteSource(Payload);
        var data = Success(await UploadAsync(fixture, source, Destination, Payload.Length, SourceSha), "files upload");
        Check(data["outcome"]!.GetValue<string>() == "success" && data["effectAttempted"]!.GetValue<bool>() &&
              data["committedId"]!.GetValue<string>() == "id:committed" &&
              data["committedRevision"]!.GetValue<string>() == "rev:committed" &&
              data["committedSize"]!.GetValue<long>() == Payload.Length &&
              data["committedContentHash"]!.GetValue<string>() == DropboxContentHash.Compute(Payload) &&
              data["sourceSha256"]!.GetValue<string>() == SourceSha &&
              data["sourceBytes"]!.GetValue<long>() == Payload.Length &&
              data["namespaceId"]!.GetValue<string>() == Namespace &&
              data["destination"]!.GetValue<string>() == Destination,
            "successful upload returns the exact commitment, source identity, destination and namespace");
        Check(fixture.Wire.UploadCalls == 1, "exactly one create-only effect call was made");
        Check(fixture.Wire.UploadedPath == ParentId + "/" + Leaf && fixture.Wire.UploadedMode == "add" &&
              fixture.Wire.UploadedAutorename == false && fixture.Wire.UploadedStrictConflict == true &&
              fixture.Wire.UploadedSize == Payload.Length &&
              fixture.Wire.UploadedHash == DropboxContentHash.Compute(Payload),
            "the wire is an exact add with strict_conflict and no autorename under the verified parent identity");
        Check(fixture.Wire.PathRootNamespaces.Count > 0 && fixture.Wire.PathRootNamespaces.All(value => value == Namespace),
            "every provider request carried the retained Dropbox root namespace");
        Check(fixture.Wire.ParentLookups >= 1 && fixture.Wire.AccountChecks >= 1,
            "the account, the namespace and the exact existing destination parent were re-verified");
    }

    private static async Task OccupiedAsync(Fixture fixture)
    {
        fixture.Wire.DestinationOccupied = true;
        var source = fixture.WriteSource(Payload);
        var data = Refusal(await UploadAsync(fixture, source, Destination, Payload.Length, SourceSha),
            "occupied destination");
        Check(data["outcome"]!.GetValue<string>() == "known-no-effect" && !data["effectAttempted"]!.GetValue<bool>() &&
              data["failureReason"]!.GetValue<string>().Contains("already exists", StringComparison.Ordinal),
            "an occupied destination is a known no-effect that never claims to have created anything");
        Check(fixture.Wire.UploadCalls == 0, "an occupied destination sends no upload request and changes nothing");
    }

    private static async Task WrongNamespaceAsync(Fixture fixture)
    {
        var source = fixture.WriteSource(Payload);
        var result = await UploadAsync(fixture, source, "ns:some-other-root//Documents/Exact/report.csv",
            Payload.Length, SourceSha);
        var data = Refusal(result, "wrong namespace");
        Check(result.Exit == (int)SyncCliExitCode.MissingTarget && data["outcome"]!.GetValue<string>() == "refused" &&
              !data["effectAttempted"]!.GetValue<bool>() &&
              data["namespaceId"]!.GetValue<string>() == "some-other-root",
            "a destination in another namespace is refused as a missing target");
        Check(fixture.Wire.Calls.Count == 0 && fixture.Wire.UploadCalls == 0,
            "a wrong namespace is refused before any provider request or effect");
    }

    private static async Task AccountRefusalAsync(Fixture fixture)
    {
        var google = new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId,
            GoogleDriveConnectionBinding.CreateAccountKey("exact-file-google"), "exact-file-google",
            "fixture@example.invalid", "Fixture", GoogleDriveProviderConfiguration.RequiredScopes).Normalize();
        await new GoogleDriveConnectionStore(fixture.State).SaveAsync(google);
        var source = fixture.WriteSource(Payload);
        var wrongProvider = Refusal(await UploadAsync(fixture, source, Destination, Payload.Length, SourceSha,
            account: google.ConnectionId), "non-Dropbox account");
        Check(wrongProvider["outcome"]!.GetValue<string>() == "refused" &&
              wrongProvider["failureReason"]!.GetValue<string>().Contains("Dropbox provider", StringComparison.Ordinal),
            "a non-Dropbox account is refused as unsupported");
        var unknown = await UploadAsync(fixture, source, Destination, Payload.Length, SourceSha,
            account: "dbid:not-present");
        var unknownReceipt = Refusal(unknown, "unknown account");
        Check(unknown.Exit == (int)SyncCliExitCode.MissingTarget &&
              unknownReceipt["outcome"]!.GetValue<string>() == "refused" &&
              !unknownReceipt["effectAttempted"]!.GetValue<bool>(),
            "an unknown retained account retains its precise no-effect receipt as a missing target");
        Check(fixture.Wire.Calls.Count == 0 && fixture.Wire.UploadCalls == 0,
            "no account refusal opens a provider connection or attempts an effect");
    }

    private static async Task SourceIdentityAsync(Fixture fixture)
    {
        var source = fixture.WriteSource(Payload);
        var hashData = Refusal(await UploadAsync(fixture, source, Destination, Payload.Length, new string('a', 64)),
            "hash mismatch");
        Check(hashData["outcome"]!.GetValue<string>() == "refused" && !hashData["effectAttempted"]!.GetValue<bool>() &&
              hashData["failureReason"]!.GetValue<string>().Contains("SHA-256", StringComparison.Ordinal),
            "a source SHA-256 mismatch is refused as a changed source");
        var sizeData = Refusal(await UploadAsync(fixture, source, Destination, Payload.Length + 1, SourceSha),
            "size mismatch");
        Check(sizeData["outcome"]!.GetValue<string>() == "refused" && !sizeData["effectAttempted"]!.GetValue<bool>(),
            "a source length mismatch is refused as a changed source");
        Check(fixture.Wire.UploadCalls == 0, "a changed source never reaches the provider effect");
        Check((await File.ReadAllBytesAsync(source)).SequenceEqual(Payload),
            "the refused source file is left byte-identical");
    }

    private static async Task SourceShapeAsync(Fixture fixture)
    {
        var emptySha = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        var directory = Path.Combine(fixture.Root, "a-directory");
        Directory.CreateDirectory(directory);
        var directoryData = Refusal(await UploadAsync(fixture, directory, Destination, 0, emptySha), "directory source");
        Check(directoryData["outcome"]!.GetValue<string>() == "refused" &&
              directoryData["failureReason"]!.GetValue<string>().Contains("directory", StringComparison.Ordinal),
            "a directory source is refused before any provider request");
        var target = fixture.WriteSource(Payload);
        var link = Path.Combine(fixture.Root, "linked.csv");
        try
        {
            File.CreateSymbolicLink(link, target);
            var linkData = Refusal(await UploadAsync(fixture, link, Destination, Payload.Length, SourceSha),
                "reparse-point source");
            Check(linkData["outcome"]!.GetValue<string>() == "refused" &&
                  linkData["failureReason"]!.GetValue<string>().Contains("reparse", StringComparison.Ordinal),
                "a reparse-point source is refused before any provider request");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            Console.WriteLine("SKIP reparse-point source fixture on this host: " + error.GetType().Name);
        }
        Check(fixture.Wire.UploadCalls == 0, "a rejected source shape sends no upload request");
    }

    private static async Task MissingParentAsync(Fixture fixture)
    {
        fixture.Wire.ParentMissing = true;
        var source = fixture.WriteSource(Payload);
        var result = await UploadAsync(fixture, source, Destination, Payload.Length, SourceSha);
        var data = Refusal(result, "missing parent");
        Check(result.Exit == (int)SyncCliExitCode.MissingTarget && data["outcome"]!.GetValue<string>() == "refused" &&
              !data["effectAttempted"]!.GetValue<bool>() &&
              data["failureReason"]!.GetValue<string>().Contains("never creates a parent", StringComparison.Ordinal),
            "an absent destination parent is refused and no parent folder is created");
        Check(fixture.Wire.UploadCalls == 0, "the missing-parent refusal sends no upload request");
    }

    private static async Task PermissionAsync(Fixture fixture)
    {
        fixture.Wire.PermissionDenied = true;
        var source = fixture.WriteSource(Payload);
        var result = await UploadAsync(fixture, source, Destination, Payload.Length, SourceSha);
        var data = Refusal(result, "permission failure");
        Check(result.Exit == (int)SyncCliExitCode.Failed && data["outcome"]!.GetValue<string>() == "refused" &&
              !data["effectAttempted"]!.GetValue<bool>(),
            "a provider permission failure fails closed instead of assuming absence");
        Check(fixture.Wire.UploadCalls == 0, "the permission failure sends no upload request");
    }

    private static async Task MalformedMetadataAsync(Fixture fixture)
    {
        fixture.Wire.MalformedMetadata = true;
        var source = fixture.WriteSource(Payload);
        var result = await UploadAsync(fixture, source, Destination, Payload.Length, SourceSha);
        var data = Refusal(result, "malformed metadata");
        Check(result.Exit == (int)SyncCliExitCode.Failed && data["outcome"]!.GetValue<string>() == "refused" &&
              !data["effectAttempted"]!.GetValue<bool>(),
            "malformed parent metadata fails closed instead of implying presence or absence");
        Check(fixture.Wire.UploadCalls == 0, "the malformed-metadata refusal sends no upload request");
    }

    private static async Task SourceRaceAsync(Fixture fixture)
    {
        var source = fixture.WriteSource(Payload);
        await File.WriteAllBytesAsync(source, Encoding.UTF8.GetBytes("changed-after-measurement"));
        var data = Refusal(await UploadAsync(fixture, source, Destination, Payload.Length, SourceSha),
            "source race");
        Check(data["outcome"]!.GetValue<string>() == "refused" && !data["effectAttempted"]!.GetValue<bool>() &&
              data["failureReason"]!.GetValue<string>().Contains("changed", StringComparison.Ordinal),
            "a source that changed after measurement is refused before transmission");
        Check(fixture.Wire.UploadCalls == 0, "a raced source never reaches the provider effect");
        Check((await File.ReadAllBytesAsync(source)).SequenceEqual(Encoding.UTF8.GetBytes("changed-after-measurement")),
            "the raced source file itself is never rewritten by this command");
    }

    private static async Task TimeoutAsync(Fixture fixture)
    {
        Check(SyncCliOperationsBase.ExactFileUploadBudget == TimeSpan.FromSeconds(300),
            "the production exact-file operation has a 300-second total budget");
        fixture.Wire.ParentDelay = TimeSpan.FromSeconds(30);
        var source = fixture.WriteSource(Payload);
        var previous = SyncCliOperationsBase.ExactFileUploadBudget;
        SyncCliOperationsBase.ExactFileUploadBudget = TimeSpan.FromMilliseconds(250);
        try
        {
            var result = await UploadAsync(fixture, source, Destination, Payload.Length, SourceSha);
            var data = Refusal(result, "timeout before transmission");
            Check(result.Exit == (int)SyncCliExitCode.Canceled && data["outcome"]!.GetValue<string>() == "refused" &&
                  !data["effectAttempted"]!.GetValue<bool>() &&
                  data["failureReason"]!.GetValue<string>().Contains("before any upload was transmitted",
                      StringComparison.Ordinal),
                "a bound expiry before transmission is a refusal that attempted no effect");
            Check(fixture.Wire.UploadCalls == 0, "the bounded expiry sent no upload request");
        }
        finally { SyncCliOperationsBase.ExactFileUploadBudget = previous; }
    }

    private static async Task CallerCancellationAsync(Fixture fixture)
    {
        fixture.Wire.ParentDelay = TimeSpan.FromSeconds(30);
        var source = fixture.WriteSource(Payload);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var result = await fixture.RunWithCancellation(cancellation.Token, "files", "upload",
            "--account", fixture.Connection, "--source", source, "--destination", Destination,
            "--bytes", PayloadBytes, "--sha256", SourceSha, "--yes");
        var data = Refusal(result, "caller cancellation before transmission");
        Check(result.Exit == (int)SyncCliExitCode.Canceled &&
              data["outcome"]!.GetValue<string>() == "refused" &&
              !data["effectAttempted"]!.GetValue<bool>() &&
              data["failureReason"]!.GetValue<string>().Contains("cancel", StringComparison.OrdinalIgnoreCase),
            "a caller cancellation reports a precise refusal, not an expiry or a successful upload");
        Check(fixture.Wire.UploadCalls == 0, "caller cancellation before transmission makes no effect call");
    }

    private static async Task PostEffectIoFailureAsync(Fixture fixture)
    {
        fixture.Wire.UploadIoFailure = true;
        var source = fixture.WriteSource(Payload);
        var data = Refusal(await UploadAsync(fixture, source, Destination, Payload.Length, SourceSha),
            "I/O failure after transmission");
        Check(data["outcome"]!.GetValue<string>() == "unknown" &&
              data["effectAttempted"]!.GetValue<bool>() &&
              data["failureReason"]!.GetValue<string>().Contains("not proven", StringComparison.Ordinal),
            "an I/O failure after transmission retains an UNKNOWN outcome and does not assert no effect");
        Check(fixture.Wire.UploadCalls == 1, "a post-transmission I/O failure is never automatically replayed");
    }

    private static async Task UnknownOutcomeAsync(Fixture fixture)
    {
        fixture.Wire.MalformedUploadSuccess = true;
        var source = fixture.WriteSource(Payload);
        var malformed = Refusal(await UploadAsync(fixture, source, Destination, Payload.Length, SourceSha),
            "malformed success");
        Check(malformed["outcome"]!.GetValue<string>() == "unknown" && malformed["effectAttempted"]!.GetValue<bool>() &&
              malformed["failureReason"]!.GetValue<string>().Contains("not proven", StringComparison.Ordinal),
            "a malformed success body after transmission is UNKNOWN, never success");
        Check(fixture.Wire.UploadCalls == 1, "the malformed success was transmitted exactly once and never retried");
        fixture.Wire.MalformedUploadSuccess = false;
        fixture.Wire.LostUploadSuccess = true;
        var lost = Refusal(await UploadAsync(fixture, source, Destination, Payload.Length, SourceSha), "lost success");
        Check(lost["outcome"]!.GetValue<string>() == "unknown" && lost["effectAttempted"]!.GetValue<bool>(),
            "a lost success response after transmission is UNKNOWN, never success");
        Check(fixture.Wire.UploadCalls == 2, "the lost success was transmitted exactly once more and never retried");
    }

    private static async Task UploadRejectionAsync(Fixture fixture)
    {
        // The single-request upload error is UploadError.path carrying UploadWriteFailed as an ordinary
        // struct, so Stone serializes its fields inline: the WriteError discriminator is error.reason..tag.
        // The structured reason is authoritative and the human-readable summary never proves an outcome.
        const string ConflictBody =
            "{\"error_summary\":\"path/disallowed_name/....\",\"error\":{\".tag\":\"path\"," +
            "\"reason\":{\".tag\":\"conflict\",\"conflict\":{\".tag\":\"file\"}}," +
            "\"upload_session_id\":\"synthetic-fixture-only\"}}";
        const string MisleadingNameBody =
            "{\"error_summary\":\"path/conflict/file/....\",\"error\":{\".tag\":\"path\"," +
            "\"reason\":{\".tag\":\"disallowed_name\"},\"upload_session_id\":\"synthetic-fixture-only\"}}";
        const string PropertiesBody =
            "{\"error_summary\":\"properties_error/does_not_fit_template/....\",\"error\":{\".tag\":\"properties_error\"," +
            "\"properties_error\":{\".tag\":\"does_not_fit_template\"}},\"upload_session_id\":\"synthetic-fixture-only\"}";

        const string RequestId = "req0fixture0123456789abcdef";
        const string ConflictNoSubtypeBody =
            "{\"error\":{\".tag\":\"path\",\"reason\":{\".tag\":\"conflict\"}," +
            "\"upload_session_id\":\"synthetic-fixture-only\"}}";
        const string ConflictUnknownSubtypeBody =
            "{\"error\":{\".tag\":\"path\",\"reason\":{\".tag\":\"conflict\",\"conflict\":{\".tag\":\"link\"}}," +
            "\"upload_session_id\":\"synthetic-fixture-only\"}}";
        const string ConflictNoSessionBody =
            "{\"error\":{\".tag\":\"path\",\"reason\":{\".tag\":\"conflict\",\"conflict\":{\".tag\":\"file\"}}}}";
        const string ConflictWrongSessionTypeBody =
            "{\"error\":{\".tag\":\"path\",\"reason\":{\".tag\":\"conflict\",\"conflict\":{\".tag\":\"file\"}}," +
            "\"upload_session_id\":7}}";
        const string UnrecognisedReasonBody =
            "{\"error\":{\".tag\":\"path\",\"reason\":{\".tag\":\"reason-sentinel-never-emit\"}," +
            "\"upload_session_id\":\"synthetic-fixture-only\"}}";
        const string UnrecognisedErrorTagBody = "{\"error\":{\".tag\":\"error-tag-sentinel-never-emit\"}}";

        var source = fixture.WriteSource(Payload);
        var outputs = new List<string>();

        async Task<(JsonObject Data, string Reason, string Output, string Error)> Attempt(string name,
            Func<HttpResponseMessage> rejection)
        {
            fixture.Wire.RejectionResponseFactory = rejection;
            var before = fixture.Wire.UploadCalls;
            var result = await UploadAsync(fixture, source, Destination, Payload.Length, SourceSha);
            Check(fixture.Wire.UploadCalls == before + 1, name + ": transmitted exactly once and never retried");
            var data = Refusal(result, name);
            outputs.Add(result.Output);
            outputs.Add(result.Error);
            return (data, data["failureReason"]!.GetValue<string>(), result.Output, result.Error);
        }

        var conflict = await Attempt("structured create-only conflict",
            () => Reject(HttpStatusCode.Conflict, ConflictBody, RequestId));
        Check(conflict.Data["outcome"]!.GetValue<string>() == "known-no-effect" &&
              conflict.Data["effectAttempted"]!.GetValue<bool>() &&
              conflict.Reason.Contains("path/conflict/file", StringComparison.Ordinal) &&
              conflict.Reason.Contains(RequestId, StringComparison.Ordinal),
            "the official flattened conflict proves known-no-effect with a safe reason and request id");
        Check(conflict.Output.Contains("path/conflict/file", StringComparison.Ordinal) &&
              conflict.Output.Contains(RequestId, StringComparison.Ordinal) &&
              conflict.Error.Contains("path/conflict/file", StringComparison.Ordinal) &&
              conflict.Error.Contains(RequestId, StringComparison.Ordinal),
            "the safe reason and the allowed request correlation are present in the JSON receipt and stderr");

        var named = await Attempt("structured non-conflict with a misleading summary",
            () => Reject(HttpStatusCode.Conflict, MisleadingNameBody, RequestId));
        Check(named.Data["outcome"]!.GetValue<string>() == "unknown" && named.Data["effectAttempted"]!.GetValue<bool>() &&
              named.Reason.Contains("path/disallowed_name", StringComparison.Ordinal) &&
              !named.Reason.Contains("path/conflict", StringComparison.Ordinal),
            "a structured non-conflict stays UNKNOWN and the misleading summary never proves no effect");
        Check(named.Error.Contains("path/disallowed_name", StringComparison.Ordinal) &&
              named.Error.Contains(RequestId, StringComparison.Ordinal),
            "the structured non-conflict reason and correlation also reach stderr");

        var conflict500 = await Attempt("valid conflict under a contradictory status",
            () => Reject(HttpStatusCode.InternalServerError, ConflictBody, RequestId));
        Check(conflict500.Data["outcome"]!.GetValue<string>() == "unknown" &&
              conflict500.Data["effectAttempted"]!.GetValue<bool>(),
            "an otherwise valid conflict under a contradictory status stays UNKNOWN, never known-no-effect");

        var missingSubtype = await Attempt("conflict without the subtype member",
            () => Reject(HttpStatusCode.Conflict, ConflictNoSubtypeBody, RequestId));
        Check(missingSubtype.Data["outcome"]!.GetValue<string>() == "unknown",
            "a conflict without the recognised subtype stays UNKNOWN");

        var unknownSubtype = await Attempt("conflict with an unrecognised subtype",
            () => Reject(HttpStatusCode.Conflict, ConflictUnknownSubtypeBody, RequestId));
        Check(unknownSubtype.Data["outcome"]!.GetValue<string>() == "unknown",
            "an unrecognised conflict subtype stays UNKNOWN and never proves no effect");

        var missingSession = await Attempt("conflict without the required session field",
            () => Reject(HttpStatusCode.Conflict, ConflictNoSessionBody, RequestId));
        Check(missingSession.Data["outcome"]!.GetValue<string>() == "unknown",
            "a conflict missing the required UploadWriteFailed field stays UNKNOWN");

        var wrongSessionType = await Attempt("conflict with a wrong-type session field",
            () => Reject(HttpStatusCode.Conflict, ConflictWrongSessionTypeBody, RequestId));
        Check(wrongSessionType.Data["outcome"]!.GetValue<string>() == "unknown",
            "a wrong-type required UploadWriteFailed field stays UNKNOWN");

        var properties = await Attempt("properties_error rejection",
            () => Reject(HttpStatusCode.Conflict, PropertiesBody));
        Check(properties.Data["outcome"]!.GetValue<string>() == "unknown" &&
              properties.Reason.Contains("properties_error/does_not_fit_template", StringComparison.Ordinal),
            "a properties_error rejection keeps its allowlisted code and stays UNKNOWN");

        var malformed = await Attempt("malformed rejection body",
            () => Reject(HttpStatusCode.Conflict, "{\"error\": not json"));
        Check(malformed.Data["outcome"]!.GetValue<string>() == "unknown" &&
              malformed.Reason.Contains("'unreadable'", StringComparison.Ordinal),
            "a malformed rejection body stays UNKNOWN and never proves no effect");

        var absent = await Attempt("absent rejection body", () => Reject(HttpStatusCode.Conflict, ""));
        Check(absent.Data["outcome"]!.GetValue<string>() == "unknown",
            "an absent rejection body stays UNKNOWN and never proves no effect");

        var oversize = await Attempt("oversize rejection body",
            () => Reject(HttpStatusCode.Conflict, "{\"error\":{\".tag\":\"path\"}}",
                declaredLength: 16L * 1024 * 1024 + 1));
        Check(oversize.Data["outcome"]!.GetValue<string>() == "unknown" &&
              oversize.Reason.Contains("'unreadable'", StringComparison.Ordinal),
            "an oversize rejection body is refused without being read and stays UNKNOWN");

        var interrupted = await Attempt("interrupted rejection body", () => Interrupted(HttpStatusCode.Conflict));
        Check(interrupted.Data["outcome"]!.GetValue<string>() == "unknown",
            "an interrupted rejection body stays UNKNOWN and never proves no effect");

        var thrown = await Attempt("throwing rejection body",
            () => Throwing(HttpStatusCode.Conflict, "fixture-http-sentinel", RequestId));
        Check(thrown.Data["outcome"]!.GetValue<string>() == "unknown" &&
              thrown.Reason.Contains("'unreadable'", StringComparison.Ordinal) &&
              thrown.Reason.Contains(RequestId, StringComparison.Ordinal) &&
              !thrown.Output.Contains("fixture-http-sentinel", StringComparison.Ordinal) &&
              !thrown.Error.Contains("fixture-http-sentinel", StringComparison.Ordinal),
            "an HttpRequestException from the rejection body is sanitized to the fixed unknown taxonomy with " +
            "the validated correlation and no reflected exception text");

        var invalidId = await Attempt("invalid request id",
            () => Reject(HttpStatusCode.Conflict, ConflictBody, "req injected value"));
        Check(invalidId.Data["outcome"]!.GetValue<string>() == "known-no-effect" &&
              !invalidId.Reason.Contains("injected", StringComparison.Ordinal) &&
              !invalidId.Output.Contains("injected", StringComparison.Ordinal) &&
              !invalidId.Error.Contains("injected", StringComparison.Ordinal),
            "an invalid X-Dropbox-Request-Id is dropped and never reflected");

        var oversizeId = await Attempt("over-length request id",
            () => Reject(HttpStatusCode.Conflict, ConflictBody, "reqOVER64" + new string('z', 70)));
        Check(oversizeId.Data["outcome"]!.GetValue<string>() == "known-no-effect" &&
              !oversizeId.Reason.Contains("reqOVER64", StringComparison.Ordinal) &&
              !oversizeId.Output.Contains("reqOVER64", StringComparison.Ordinal) &&
              !oversizeId.Error.Contains("reqOVER64", StringComparison.Ordinal),
            "an X-Dropbox-Request-Id longer than the 64-character bound is dropped and never reflected");

        var unrecognisedReason = await Attempt("unrecognised reason tag",
            () => Reject(HttpStatusCode.Conflict, UnrecognisedReasonBody, RequestId));
        Check(unrecognisedReason.Data["outcome"]!.GetValue<string>() == "unknown" &&
              unrecognisedReason.Reason.Contains("'path/other'", StringComparison.Ordinal) &&
              !unrecognisedReason.Output.Contains("reason-sentinel-never-emit", StringComparison.Ordinal) &&
              !unrecognisedReason.Error.Contains("reason-sentinel-never-emit", StringComparison.Ordinal),
            "an unrecognised reason tag collapses to the fixed path/other code and is never reflected");

        var unrecognisedErrorTag = await Attempt("unrecognised upload error tag",
            () => Reject(HttpStatusCode.Conflict, UnrecognisedErrorTagBody, RequestId));
        Check(unrecognisedErrorTag.Data["outcome"]!.GetValue<string>() == "unknown" &&
              unrecognisedErrorTag.Reason.Contains("'other'", StringComparison.Ordinal) &&
              !unrecognisedErrorTag.Output.Contains("error-tag-sentinel-never-emit", StringComparison.Ordinal) &&
              !unrecognisedErrorTag.Error.Contains("error-tag-sentinel-never-emit", StringComparison.Ordinal),
            "an unrecognised upload-error tag collapses to the fixed other code and is never reflected");

        Check(outputs.All(text => !text.Contains("synthetic-fixture-only", StringComparison.Ordinal)),
            "no raw upload_session_id reaches the JSON receipt or stderr");
        Check(outputs.All(text => !text.Contains("not json", StringComparison.Ordinal)) &&
              outputs.All(text => !text.Contains("injected", StringComparison.Ordinal)),
            "no raw provider body or rejected correlation value is reflected into receipt or stderr");
    }

    private static HttpResponseMessage Reject(HttpStatusCode status, string body, string? requestId = null,
        long? declaredLength = null)
    {
        HttpContent content = declaredLength is { } length
            ? new DeclaredLengthContent(body, length)
            : new StringContent(body, Encoding.UTF8, "application/json");
        var response = new HttpResponseMessage(status)
        {
            Content = content
        };
        if (requestId is not null) response.Headers.TryAddWithoutValidation("X-Dropbox-Request-Id", requestId);
        return response;
    }

    private static HttpResponseMessage Interrupted(HttpStatusCode status) =>
        new(status) { Content = new InterruptedContent() };

    private static HttpResponseMessage Throwing(HttpStatusCode status, string sentinel, string? requestId = null)
    {
        var response = new HttpResponseMessage(status) { Content = new ThrowingContent(sentinel) };
        if (requestId is not null) response.Headers.TryAddWithoutValidation("X-Dropbox-Request-Id", requestId);
        return response;
    }

    // Reports a declared length above the reader's bound without materialising that many bytes; the
    // production reader must refuse the response before it reads the body.
    private sealed class DeclaredLengthContent(string body, long declaredLength) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = declaredLength; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(body)).AsTask();
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(body), cancellationToken).AsTask();
    }

    // The response headers arrive and the connection then fails while the error body is read.
    private sealed class InterruptedContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new IOException("Fixture connection lost while reading the rejection body.");
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken) =>
            throw new IOException("Fixture connection lost while reading the rejection body.");
    }

    // The response headers arrive and the body transport then fails with a transport exception that must
    // be sanitized rather than surfacing the fixture's own message text.
    private sealed class ThrowingContent(string sentinel) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new HttpRequestException(sentinel);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException(sentinel);
    }

    private static async Task ArgumentRejectionsAsync(Fixture fixture)
    {
        var source = fixture.WriteSource(Payload);
        foreach (var args in new[]
        {
            new[] { "files", "upload", "--source", source, "--destination", Destination, "--bytes", PayloadBytes,
                "--sha256", SourceSha, "--yes" },
            new[] { "files", "upload", "--account", fixture.Connection, "--destination", Destination,
                "--bytes", PayloadBytes, "--sha256", SourceSha, "--yes" },
            new[] { "files", "upload", "--account", fixture.Connection, "--source", "relative-report.csv",
                "--destination", Destination, "--bytes", PayloadBytes, "--sha256", SourceSha, "--yes" },
            new[] { "files", "upload", "--account", fixture.Connection, "--source", source,
                "--destination", "/Documents/Exact/report.csv", "--bytes", PayloadBytes, "--sha256", SourceSha,
                "--yes" },
            new[] { "files", "upload", "--account", fixture.Connection, "--source", source,
                "--destination", Destination, "--bytes", "-1", "--sha256", SourceSha, "--yes" },
            new[] { "files", "upload", "--account", fixture.Connection, "--source", source,
                "--destination", Destination, "--bytes", PayloadBytes, "--sha256", new string('A', 64), "--yes" },
            new[] { "files", "upload", "--account", fixture.Connection, "--source", source,
                "--destination", Destination, "--bytes", PayloadBytes, "--sha256", SourceSha },
            new[] { "files", "upload", "--account", fixture.Connection, "--source", source,
                "--destination", Destination, "--bytes", PayloadBytes, "--sha256", SourceSha, "--yes", "--other", "x" },
            new[] { "files", "upload", "--account", fixture.Connection, "--source", source, "--source", source,
                "--destination", Destination, "--bytes", PayloadBytes, "--sha256", SourceSha, "--yes" }
        })
        {
            var refused = await fixture.Run([.. args]);
            Check(refused.Exit == (int)SyncCliExitCode.Usage,
                "a malformed files upload argument is a usage refusal: " + string.Join(' ', args));
        }
        Check(fixture.Wire.Calls.Count == 0, "no argument refusal opens product state or a provider connection");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly PilotInstanceLock _instance;
        private readonly HttpClient _http;
        private Fixture(string root, PilotInstanceLock instance, HttpClient http, Handler wire,
            SyncAuthorityRuntimeHost host, string connection)
        { Root = root; _instance = instance; _http = http; Wire = wire; Host = host; Connection = connection; }

        internal string Root { get; }
        internal Handler Wire { get; }
        internal SyncAuthorityRuntimeHost Host { get; }
        internal PilotStateAuthority State => _instance.StateAuthority;
        internal string Connection { get; }

        internal static async Task<Fixture> Create(string path)
        {
            Directory.CreateDirectory(path);
            var instance = PilotInstanceLock.Acquire(Path.Combine(path, "state", "instance.lock"));
            var wire = new Handler();
            var http = new HttpClient(wire, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, Account,
                Namespace, "exact-file-home", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes).Normalize();
            await new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(binding);
            var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http,
                new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
                new DropboxConnectionStore(instance.StateAuthority),
                new MemoryDropboxRefreshTokenStore("exact-file-refresh"),
                new SyncAuthorityDatabase(Path.Combine(path, "authority.db")));
            return new Fixture(path, instance, http, wire, host, binding.ConnectionId);
        }

        internal string WriteSource(byte[] bytes)
        {
            var source = Path.Combine(Root, "source-" + Guid.NewGuid().ToString("N") + ".csv");
            File.WriteAllBytes(source, bytes);
            return source;
        }

        internal Task<(int Exit, string Output, string Error)> Run(params string[] args) =>
            RunWithCancellation(CancellationToken.None, args);

        internal async Task<(int Exit, string Output, string Error)> RunWithCancellation(
            CancellationToken cancellationToken, params string[] args)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exit = await SyncCli.ExecuteAsync(["--cli", .. args, "--json"], output, error,
                () => new Runtime(this), cancellationToken);
            return (exit, output.ToString(), error.ToString());
        }

        public ValueTask DisposeAsync()
        {
            try { Host.Dispose(); } finally { _http.Dispose(); _instance.Dispose(); }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Runtime(Fixture fixture) : SyncCliOperationsBase
    {
        protected override SyncAuthorityRuntimeHost Host => fixture.Host;
        protected override PilotStateAuthority StateAuthority => fixture.State;
        protected override bool ApplicationOwnsRuntime => true;
        public override string Mode => "controlled-http";
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal readonly List<string> Calls = [];
        internal readonly List<string> PathRootNamespaces = [];
        internal int UploadCalls;
        internal int ParentLookups;
        internal int AccountChecks;
        internal bool ParentMissing;
        internal bool DestinationOccupied;
        internal bool PermissionDenied;
        internal bool MalformedMetadata;
        internal bool MalformedUploadSuccess;
        internal bool LostUploadSuccess;
        internal bool UploadIoFailure;
        internal Func<HttpResponseMessage>? RejectionResponseFactory;
        internal TimeSpan ParentDelay = TimeSpan.Zero;
        internal string? UploadedPath;
        internal string? UploadedMode;
        internal bool? UploadedAutorename;
        internal bool? UploadedStrictConflict;
        internal long UploadedSize;
        internal string? UploadedHash;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            if (host is not ("api.dropboxapi.com" or "content.dropboxapi.com"))
                throw new InvalidOperationException("Non-fixture HTTP rejected: " + host);
            if (request.Headers.TryGetValues("Dropbox-API-Path-Root", out var roots))
            {
                using var pathRoot = JsonDocument.Parse(roots.Single());
                PathRootNamespaces.Add(pathRoot.RootElement.GetProperty("root").GetString()!);
            }
            var route = request.RequestUri.AbsolutePath.TrimStart('/');
            Calls.Add(route);
            if (route == "oauth2/token")
                return Json(new Dictionary<string, object?>
                {
                    ["access_token"] = "exact-file-access", ["expires_in"] = 3600, ["account_id"] = Account
                });
            if (route == "2/users/get_current_account")
            {
                AccountChecks++;
                return Json(new Dictionary<string, object?>
                {
                    ["account_id"] = Account,
                    ["root_info"] = new Dictionary<string, object?>
                    {
                        ["root_namespace_id"] = Namespace, ["home_namespace_id"] = "exact-file-home"
                    }
                });
            }
            if (route == "2/files/get_metadata")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var path = body.RootElement.GetProperty("path").GetString();
                if (PermissionDenied)
                    return new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent(
                            "{\"error_summary\":\"insufficient_scope/\",\"error\":{\".tag\":\"other\"}}",
                            Encoding.UTF8, "application/json")
                    };
                if (MalformedMetadata && path == ParentPath)
                    return Json(new Dictionary<string, object?> { ["path_display"] = ParentPath });
                if (path == ParentPath)
                {
                    ParentLookups++;
                    if (ParentMissing) return NotFound();
                    if (ParentDelay > TimeSpan.Zero) await Task.Delay(ParentDelay, cancellationToken);
                    return Json(Folder(ParentId, ParentPath, "Exact"));
                }
                if (path == ParentId) return Json(Folder(ParentId, ParentPath, "Exact"));
                if (path == ParentId + "/" + Leaf)
                    return DestinationOccupied
                        ? Json(File("id:existing", "rev:existing", 3, new string('b', 64), ParentPath + "/" + Leaf))
                        : NotFound();
                return NotFound();
            }
            if (route != "2/files/upload") throw new InvalidOperationException("Unexpected provider route: " + route);
            UploadCalls++;
            var argument = JsonArgument(request);
            UploadedPath = argument.GetProperty("path").GetString();
            var mode = argument.GetProperty("mode");
            UploadedMode = mode.ValueKind == JsonValueKind.String ? mode.GetString() : mode.GetProperty(".tag").GetString();
            UploadedAutorename = argument.GetProperty("autorename").GetBoolean();
            UploadedStrictConflict = argument.GetProperty("strict_conflict").GetBoolean();
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            UploadedSize = bytes.LongLength;
            UploadedHash = DropboxContentHash.Compute(bytes);
            if (RejectionResponseFactory is { } rejection) return rejection();
            if (UploadIoFailure) throw new IOException("Fixture response connection lost after payload transmission.");
            if (LostUploadSuccess)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("the create-only result is unknown", Encoding.UTF8, "text/plain")
                };
            if (MalformedUploadSuccess) return Json(new Dictionary<string, object?> { ["unexpected"] = true });
            return Json(File("id:committed", "rev:committed", bytes.LongLength, UploadedHash,
                ParentPath + "/" + Leaf));
        }

        private static JsonElement JsonArgument(HttpRequestMessage request)
        {
            var raw = request.Headers.GetValues("Dropbox-API-Arg").Single();
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }

        private static Dictionary<string, object?> Folder(string id, string path, string name) => new()
        {
            [".tag"] = "folder", ["name"] = name, ["id"] = id,
            ["path_display"] = path, ["path_lower"] = path.ToLowerInvariant()
        };

        private static Dictionary<string, object?> File(string id, string revision, long size, string hash,
            string path) => new()
        {
            [".tag"] = "file", ["name"] = Leaf, ["id"] = id, ["rev"] = revision, ["size"] = size,
            ["content_hash"] = hash, ["path_display"] = path, ["path_lower"] = path.ToLowerInvariant()
        };

        private static HttpResponseMessage NotFound() => new(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                "{\"error_summary\":\"path/not_found/\",\"error\":{\".tag\":\"path\",\"path\":{\".tag\":\"not_found\"}}}",
                Encoding.UTF8, "application/json")
        };

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }
}
