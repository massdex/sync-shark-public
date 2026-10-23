using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark;

// Controlled HTTP is input only: each case enters SyncCli.ExecuteAsync and uses the real
// SyncCliOperationsBase, connection store, token refresh/account verification and sharing parsers.
// No Windows credential store, live provider, product installation or arbitrary network is used.
internal static class CliSharingChecks
{
    private const string Account = "dbid:cli-sharing-fixture";
    private const string Home = "cli-sharing-home";
    private const string Root = "cli-sharing-root";
    private const string Access = "fixture-access-private-value";
    private const string Refresh = "fixture-refresh-private-value";
    private const string Password = "fixture-password-private-value";
    private const string ProviderPrivate = "fixture-provider-private-value";
    private const string LinkCursor = "fixture-links-continuation-private";
    private const string RequestCursor = "fixture-requests-continuation-private";
    private static readonly string[] PrivateValues = [Access, Refresh, Password, ProviderPrivate, LinkCursor, RequestCursor];
    private static readonly DateTimeOffset Future = DateTimeOffset.UtcNow.AddDays(30);
    private static string Deadline => Future.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    private static int _assertions;
    private static readonly List<string> Failures = [];
    private static string LinkUrl(int item) => $"https://www.dropbox.com/s/fixture{item}/item{item}.txt";

    internal static async Task<int> RunAsync(string root)
    {
        if (Directory.Exists(root)) throw new InvalidOperationException("Sharing fixture root must be new.");
        Directory.CreateDirectory(root);
        _assertions = 0;
        Failures.Clear();
        try
        {
            await Case("later pages and exact non-home namespace", async f =>
            {
                foreach (var family in new[] { "sharing", "file-requests" })
                {
                    var result = await f.Run(family, "list", "--settings", Ns(Root), "--limit", "1", "--offset", "2");
                    var data = Success(result, family + " later page");
                    Check(data?["namespaceId"]?.GetValue<string>() == Root && data["count"]?.GetValue<int>() == 1 &&
                          data["hasMore"]?.GetValue<bool>() == true, family + " bounded later window and remaining rows");
                    var rows = data?[family == "sharing" ? "links" : "requests"]?.AsArray();
                    Check(rows?.Count == 1 && (family == "sharing" ? rows[0]?["url"]?.GetValue<string>() == LinkUrl(2) :
                          rows[0]?["id"]?.GetValue<string>() == "request2"), family + " offset reaches exact second-page item");
                    var calls = f.Handler.Calls.Where(c => c.Route.StartsWith(family == "sharing" ? "sharing/" : "file_requests/", StringComparison.Ordinal)).ToArray();
                    Check(calls.Length == 2 && calls.All(c => c.Namespace == Root), family + " exactly two pages in selected root namespace");
                    Check(calls.Length == 2 && calls[1].Body["cursor"]?.GetValue<string>() ==
                          (family == "sharing" ? LinkCursor : RequestCursor), family + " real parser supplies continuation");
                    Check(calls.Length == 2 && calls[1].Route == (family == "sharing" ? "sharing/list_shared_links" :
                          "file_requests/list/continue"), family + " continuation uses provider endpoint");
                }
                Check(f.Handler.OAuthCalls == 2 && f.Handler.AccountCalls == 2 && f.Handler.Effects.Count == 0,
                    "list commands verify real account without mutations");
            });

            await Case("pagination is bounded", async f =>
            {
                // G4: the twenty-page bound is deliberate production fail-closed behaviour, not a defect.
                // The listing must refuse with the bounded-page incomplete error instead of reporting a
                // silent successful empty window, and it must leave provider state untouched.
                foreach (var family in new[] { "sharing", "file-requests" })
                {
                    var before = f.Handler.Calls.Count;
                    var result = await f.Run(family, "list", "--limit", "1", "--offset", "999");
                    var calls = f.Handler.Calls.Count - before;
                    var envelope = JsonNode.Parse(result.Output)?.AsObject();
                    var error = envelope?["error"]?.AsObject();
                    Check(result.Exit == 6, family + " page cap fails closed (actual " + result.Exit + ")");
                    Check(envelope?["ok"]?.GetValue<bool>() == false && envelope["data"] is null && error is not null,
                        family + " page cap returns a failed envelope carrying no data");
                    Check(error?["code"]?.GetValue<string>() == "operation_failed",
                        family + " page cap uses the operation failure code");
                    var message = error?["message"]?.GetValue<string>() ?? string.Empty;
                    Check(message.Contains("bounded page limit", StringComparison.OrdinalIgnoreCase) &&
                          message.Contains("incomplete", StringComparison.OrdinalIgnoreCase) &&
                          message.Contains("continuing list", StringComparison.OrdinalIgnoreCase),
                        family + " page cap names the bounded page limit, the incomplete result and the continuing list");
                    Check((result.Output + result.Error).Contains("operation_failed", StringComparison.Ordinal),
                        family + " page cap reports the same failure on the human channel");
                    Check(calls == 20, family + " stops after twenty provider pages (actual " + calls + ")");
                    Check(f.Handler.Effects.Count == 0, family + " page cap performs no provider effect");
                    Check(PrivateValues.All(value => !(result.Output + result.Error).Contains(value, StringComparison.Ordinal)),
                        family + " page cap leaks no private value or continuation");
                }
            }, h => h.UnendingPages = true);

            await Case("missing account and namespace", async f =>
            {
                foreach (var family in new[] { "sharing", "file-requests" })
                {
                    var wrongAccount = await f.RunWithAccount("unknown-exact-account", family, "list");
                    Check(wrongAccount.Exit == 4 && f.Handler.OAuthCalls == 0 && f.Handler.Calls.Count == 0,
                        family + " unknown exact account rejects before HTTP");
                    var wrongNamespace = await f.Run(family, "list", "--settings", Ns("foreign-namespace"));
                    Check(wrongNamespace.Exit == 4 && f.Handler.OAuthCalls == 0 && f.Handler.Calls.Count == 0,
                        family + " foreign namespace rejects before HTTP");
                }
                var change = await f.Run("sharing", "change", "--key", LinkUrl(0), "--settings",
                    "{\"namespace\":\"foreign-namespace\",\"audience\":\"team\"}", "--yes");
                var edit = await f.Run("file-requests", "edit", "--key", "request0", "--settings",
                    "{\"namespace\":\"foreign-namespace\",\"title\":\"Changed\"}", "--yes");
                Check(change.Exit == 4 && edit.Exit == 4 && f.Handler.Effects.Count == 0 && f.Handler.Calls.Count == 0,
                    "foreign-namespace edits issue no provider requests or effects");
            });

            foreach (var mismatch in new[] { "token-account", "current-account", "current-namespace" })
                await Case("verified identity refusal " + mismatch, async f =>
                {
                    var result = await f.Run("sharing", "change", "--key", LinkUrl(0), "--settings", "{\"audience\":\"team\"}", "--yes");
                    Check(result.Exit == 6 && f.Handler.OAuthCalls == 1 && f.Handler.Effects.Count == 0 && f.Handler.Calls.Count == 0,
                        mismatch + " rejected by real authorization verification before sharing HTTP");
                    Check(f.Handler.AccountCalls == (mismatch == "token-account" ? 0 : 1), mismatch + " reaches expected verification boundary");
                }, h => h.IdentityMismatch = mismatch);

            await Case("change later exact link", async f =>
            {
                var data = Success(await f.Run("sharing", "change", "--key", LinkUrl(2), "--settings",
                    JsonSerializer.Serialize(new { @namespace = Root, audience = "team", allowDownload = false, requirePassword = false, expires = Deadline }), "--yes"), "change exact later link");
                Check(data?["url"]?.GetValue<string>() == LinkUrl(2) && data["audience"]?.GetValue<string>() == "team" &&
                      data["allowDownload"]?.GetValue<bool>() == false && data["expires"] is not null,
                    "genuine link parser projects modified capability fields");
                Check(f.Handler.Effects.Count == 1 && f.Handler.Effects[0].Route == "sharing/modify_shared_link_settings" &&
                      f.Handler.Effects[0].Namespace == Root && f.Handler.Effects[0].Body["url"]?.GetValue<string>() == LinkUrl(2),
                    "change sends one exact second-page URL in selected namespace");
                Check(f.Handler.Calls.Count(c => c.Route == "sharing/list_shared_links") == 2 &&
                      f.Handler.Calls.Count(c => c.Route == "sharing/get_shared_link_metadata") == 1,
                    "change locates later link then refreshes exact identity");
            });

            await Case("bounded selected revocation returns every exact outcome", async f =>
            {
                f.Handler.EffectFailureByUrl[LinkUrl(1)] = "http500";
                f.Handler.EffectFailureByUrl[LinkUrl(2)] = "rejected";
                var selection = JsonSerializer.Serialize(new { links = Enumerable.Range(0, 3).Select(i =>
                    new { accountId = f.ConnectionId, namespaceId = i == 2 ? Root : Home, url = LinkUrl(i) }) });
                var result = await f.RunExact("sharing", "revoke-selected", "--settings", selection, "--yes");
                var data = JsonNode.Parse(result.Output)?["data"]?.AsArray();
                Check(result.Exit == 0 && data?.Count == 3, "selected revoke returns its complete per-link report");
                Check(data?.Select(row => row?["status"]?.GetValue<string>()).SequenceEqual(["revoked", "uncertain", "rejected"]) == true,
                    "confirmed, uncertain and known rejection are distinct");
                Check(f.Handler.Effects.Count == 3 && f.Handler.Effects.Select(c => c.Body["url"]!.GetValue<string>()).SequenceEqual(Enumerable.Range(0, 3).Select(LinkUrl)),
                    "each selected URL is attempted once without automatic retries");
                Check(f.Handler.Effects[2].Namespace == Root && f.Handler.Effects.Take(2).All(c => c.Namespace == Home),
                    "each selected link retains its own namespace");
                Check(data?[1]?["retryable"]?.GetValue<bool>() == false && data?[2]?["retryable"]?.GetValue<bool>() == true,
                    "uncertain effect is never advertised as retryable");
            });
            await Case("selected revoke rejects malformed selection before effects", async f =>
            {
                var exact = new { accountId = f.ConnectionId, namespaceId = Home, url = LinkUrl(0) };
                foreach (var links in new[] { Array.Empty<object>(), new object[] { exact, exact }, Enumerable.Repeat<object>(exact, 101).ToArray() })
                {
                    var result = await f.RunExact("sharing", "revoke-selected", "--settings", JsonSerializer.Serialize(new { links }), "--yes");
                    Check(result.Exit != 0 && f.Handler.Effects.Count == 0, "empty, duplicate or oversized batch has zero effects");
                }
                var missingConsent = await f.RunExact("sharing", "revoke-selected", "--settings", JsonSerializer.Serialize(new { links = new[] { exact } }));
                Check(missingConsent.Exit == 2 && f.Handler.Effects.Count == 0, "selected revoke requires --yes before dispatch");
            });

            await Case("native complete bulk receipt and pre-effect oversize refusal", async f =>
            {
                var targets = Enumerable.Range(0, 100).Select(index => new SharedLinkRevokeTarget(
                    f.ConnectionId, Home, LinkUrl(index))).ToArray();
                var wireOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };
                var largest = targets.Select(SharedLinkRevokeBatch.MaximumReceipt).ToArray();
                Check(SyncCli.ResponseFits(largest, "sharing revoke-selected"),
                    "all 100 ordinary exact targets fit with every worst-case production outcome");
                foreach (var target in targets)
                    foreach (var possible in SharedLinkRevokeBatch.PossibleReceipts(target))
                        Check(JsonSerializer.Serialize(possible).Length <= JsonSerializer.Serialize(
                                  SharedLinkRevokeBatch.MaximumReceipt(target)).Length,
                            "receipt admission covers every actual production status and description");
                f.Handler.EffectFailureByUrl[LinkUrl(1)] = "http500";
                f.Handler.EffectFailureByUrl[LinkUrl(2)] = "rejected";
                var selectionFile = Path.Combine(SyncSharkRuntimePaths.ProductRoot, "cli-revoke-selection.json");
                if (File.Exists(selectionFile)) throw new InvalidOperationException("Native fixture selection already exists.");
                Console.WriteLine("STEP native revocation runtime construction");
                await using var runtime = new Runtime(f.StatePath, f.Handler, borrowed: true);
                try
                {
                    Console.WriteLine("STEP native revocation receiver start");
                    using var server = new SyncCliBridgeServer(runtime);
                    server.Start();
                    await File.WriteAllTextAsync(selectionFile, JsonSerializer.Serialize(new { links = targets }, wireOptions));
                    Console.WriteLine("STEP native revocation client invocation");
                    using var receipt = JsonDocument.Parse(await CliChecks.NativeProbeAsync("revocations"));
                    var outcomes = receipt.RootElement.GetProperty("data");
                    Check(receipt.RootElement.GetProperty("ok").GetBoolean() && outcomes.GetArrayLength() == 100,
                        "actual authenticated native bridge returns a complete successful 100-link report");
                    Check(outcomes.EnumerateArray().Select(row => row.GetProperty("target").GetProperty("url").GetString())
                              .SequenceEqual(targets.Select(target => target.Url)) &&
                          outcomes[1].GetProperty("status").GetString() == "uncertain" &&
                          !outcomes[1].GetProperty("retryable").GetBoolean() &&
                          outcomes[2].GetProperty("status").GetString() == "rejected",
                        "native receipt keeps every exact target and distinguishes uncertainty without replay");
                    Check(f.Handler.Effects.Count == 100 && f.Handler.Effects.Select(call =>
                              call.Body["url"]!.GetValue<string>()).SequenceEqual(targets.Select(target => target.Url)),
                        "real CLI/host/provider path attempts each selected fixture effect exactly once");
                    var longTargets = targets.Select(target => target with
                        { Url = target.Url + "?padding=" + new string('a', 20) + new string('&', 40) }).ToArray();
                    var longSelection = JsonSerializer.Serialize(new { links = longTargets }, wireOptions);
                    var nonce = new string('a', 32);
                    var request = SyncCliBridgeProtocol.EncodeRequest(new("invoke", nonce, DateTimeOffset.UtcNow,
                        ["--cli", "sharing", "revoke-selected", "--settings", longSelection, "--yes", "--json"], "1000"));
                    var parsedRequest = SyncCliBridgeProtocol.ParseRequest(request, DateTimeOffset.UtcNow);
                    Check(SyncCliParser.Parse(parsedRequest.Arguments.ToArray()).Command == "sharing revoke-selected",
                        "oversize-receipt request passes the actual native and CLI input parsers");
                    Check(request.Length < SyncCliBridgeProtocol.MaximumPayloadBytes &&
                          !SyncCli.ResponseFits(longTargets.Select(SharedLinkRevokeBatch.MaximumReceipt).ToArray(),
                              "sharing revoke-selected"),
                        "oversize-receipt fixture is a valid bounded native request, not an input-frame rejection");
                    var calls = f.Handler.Calls.Count;
                    var oauth = f.Handler.OAuthCalls;
                    await File.WriteAllTextAsync(selectionFile, longSelection);
                    using var refused = JsonDocument.Parse(await CliChecks.NativeProbeAsync("revocations-too-large"));
                    Check(!refused.RootElement.GetProperty("ok").GetBoolean() &&
                          refused.RootElement.GetProperty("error").GetProperty("message").GetString()!
                              .Contains("No provider action was taken", StringComparison.Ordinal) &&
                          f.Handler.Effects.Count == 100 && f.Handler.Calls.Count == calls && f.Handler.OAuthCalls == oauth,
                        "actual native receipt-size refusal occurs before any provider request or mutation");
                    var human = SyncCli.RenderHuman(largest);
                    Check(human.Contains(targets[99].Url, StringComparison.Ordinal) &&
                          human.Contains("rejected", StringComparison.Ordinal) &&
                          human.Contains("retryable=True", StringComparison.Ordinal),
                        "human bulk report includes the final target, exact outcome and retryability");
                }
                finally
                {
                    await runtime.DisposeOwnedAsync();
                    if (File.Exists(selectionFile)) File.Delete(selectionFile);
                }
            }, handler => handler.ListedLinkCount = 100);

            await Case("revoke exact link", async f =>
            {
                Success(await f.Run("sharing", "revoke", "--key", LinkUrl(0), "--yes"), "revoke exact link");
                Check(f.Handler.Effects.Count == 1 && f.Handler.Effects[0].Route == "sharing/revoke_shared_link" &&
                      f.Handler.Effects[0].Body["url"]?.GetValue<string>() == LinkUrl(0) && f.Handler.Effects[0].Namespace == Home,
                    "revoke sends one exact home-namespace URL");
            });

            // Later-page targeting must work. An unsupported namespace selector may instead be
            // refused by the parser, but may never be silently ignored before sending an effect.
            await Case("revoke later link", async f =>
            {
                Success(await f.Run("sharing", "revoke", "--key", LinkUrl(2), "--yes"), "revoke later exact link");
                Check(f.Handler.Effects.Count == 1 && f.Handler.Effects[0].Body["url"]?.GetValue<string>() == LinkUrl(2),
                    "revoke reaches later-page exact target");
            });
            await Case("revoke namespace refusal", async f =>
            {
                var result = await f.Run("sharing", "revoke", "--key", LinkUrl(0), "--settings", Ns("foreign-namespace"), "--yes");
                Check((result.Exit == 2 && JsonNode.Parse(result.Output)?["error"]?["code"]?.GetValue<string>() == "usage" ||
                       result.Exit == 4 && JsonNode.Parse(result.Output)?["error"]?["code"]?.GetValue<string>() == "missing_target") &&
                      f.Handler.Effects.Count == 0 && f.Handler.Calls.Count == 0,
                    "revoke rejects an explicitly foreign namespace without effects");
            });

            await Case("missing exact targets", async f =>
            {
                var result = await f.Run("sharing", "change", "--key", LinkUrl(99), "--settings", "{\"audience\":\"team\"}", "--yes");
                Check(result.Exit == 4 && f.Handler.Effects.Count == 0 && f.Handler.Calls.Count == 2,
                    "missing link exhausts bounded list and cannot change neighboring target");
                var request = await f.Run("file-requests", "edit", "--key", "missing", "--settings", "{\"title\":\"Changed\"}", "--yes");
                Check(request.Exit == 6 && f.Handler.Effects.Count == 0,
                    "provider missing request rejects before mutation");
            });

            await Case("refresh identity and capabilities", async f =>
            {
                var result = await f.Run("sharing", "change", "--key", LinkUrl(0), "--settings", "{\"audience\":\"team\"}", "--yes");
                Check(result.Exit == 6 && f.Handler.Effects.Count == 0, "changed link item identity rejected before mutation");
            }, h => h.RefreshWrongItem = true);
            await Case("provider policy refusal", async f =>
            {
                var result = await f.Run("sharing", "change", "--key", LinkUrl(0), "--settings", "{\"audience\":\"team\"}", "--yes");
                Check(result.Exit == 6 && f.Handler.Effects.Count == 0, "real parsed audience restriction refuses mutation");
            }, h => h.DenyTeam = true);

            await Case("file request edit reopen and deadline", async f =>
            {
                var result = await f.Run("file-requests", "edit", "--key", "request2", "--settings",
                    JsonSerializer.Serialize(new { @namespace = Root, title = "Reopened request", destination = "/Inbox", description = "Bounded fixture", open = true, deadline = Deadline }), "--yes");
                var data = Success(result, "request edit and reopen");
                Check(data?["id"]?.GetValue<string>() == "request2" && data["title"]?.GetValue<string>() == "Reopened request" &&
                      data["isOpen"]?.GetValue<bool>() == true && data["deadline"] is not null && data["fileCount"]?.GetValue<long>() == 7,
                    "real request parser returns exact reopened request, deadline and uploaded count");
                Check(f.Handler.Effects.Count == 1 && f.Handler.Effects[0].Namespace == Root &&
                      f.Handler.Effects[0].Body["id"]?.GetValue<string>() == "request2" &&
                      f.Handler.Effects[0].Body["open"]?.GetValue<bool>() == true &&
                      f.Handler.Effects[0].Body["deadline"]?[".tag"]?.GetValue<string>() == "update" &&
                      f.Handler.Effects[0].Body["deadline"]?["update"]?["deadline"]?.GetValue<string>() == Deadline,
                    "request mutation carries exact ID, reopen state and provider deadline update union");
                Check(f.Handler.Calls.Count(c => c.Route == "file_requests/get") == 2 &&
                      f.Handler.Calls.Single(c => c.Route == "files/get_metadata").Body["path"]?.GetValue<string>() == "/Inbox",
                    "request edit revalidates snapshot and destination through real operations");
            });
            await Case("clear request deadline", async f =>
            {
                var data = Success(await f.Run("file-requests", "edit", "--key", "request2", "--settings", "{\"deadline\":null}", "--yes"), "clear deadline");
                Check(data is not null && data["deadline"] is null && f.Handler.Effects.Count == 1 &&
                      f.Handler.Effects[0].Body["deadline"]?[".tag"]?.GetValue<string>() == "update" &&
                      f.Handler.Effects[0].Body["deadline"]?["update"] is null,
                    "request deadline removal sends explicit update-null and parses removal");
            });
            await Case("changed request snapshot", async f =>
            {
                var result = await f.Run("file-requests", "edit", "--key", "request2", "--settings", "{\"title\":\"Changed\"}", "--yes");
                Check(result.Exit == 6 && f.Handler.Effects.Count == 0, "concurrently changed request rejected without overwrite");
            }, h => h.ChangeRequestOnRefresh = true);
            await Case("wrong request identity", async f =>
            {
                var result = await f.Run("file-requests", "edit", "--key", "request2", "--settings", "{\"title\":\"Changed\"}", "--yes");
                Check(result.Exit == 6 && f.Handler.Effects.Count == 0, "different provider request ID rejected before mutation");
            }, h => h.WrongRequestId = true);

            await Case("password argument and confirmation rejection", async f =>
            {
                var result = await f.Run("sharing", "change", "--key", LinkUrl(0), "--settings",
                    JsonSerializer.Serialize(new { password = Password }), "--yes");
                Check(result.Exit == 5 && f.Handler.Effects.Count == 0 && f.Handler.Calls.Count == 0,
                    "password value refused without sharing HTTP or output leakage");
                var noConfirmation = await f.Run("sharing", "revoke", "--key", LinkUrl(0));
                Check(noConfirmation.Exit == 2 && f.Handler.Calls.Count == 0 && f.Handler.Effects.Count == 0,
                    "missing confirmation rejects mutation before provider access");
            });

            foreach (var mode in new[] { "http500", "malformed", "transport", "wrong-identity" })
                await Case("uncertain mutation " + mode, async f =>
                {
                    foreach (var family in new[] { "sharing", "file-requests" })
                    {
                        var before = f.Handler.Effects.Count;
                        var result = family == "sharing"
                            ? await f.Run("sharing", "change", "--key", LinkUrl(0), "--settings", "{\"audience\":\"team\"}", "--yes")
                            : await f.Run("file-requests", "edit", "--key", "request2", "--settings", "{\"title\":\"Changed\"}", "--yes");
                        Check(result.Exit == 6 && f.Handler.Effects.Count == before + 1,
                            family + " " + mode + " reports failure after exactly one mutation attempt");
                        Check((result.Output + result.Error).Contains("Refresh", StringComparison.OrdinalIgnoreCase),
                            family + " " + mode + " directs refresh instead of automatic retry");
                    }
                }, h => h.MutationFailure = mode);

            Console.WriteLine(JsonSerializer.Serialize(new { result = Failures.Count == 0 ? "PASS" : "FAIL", assertions = _assertions,
                failed = Failures.Count, failures = Failures, note = "Real CLI and sharing semantics with controlled HTTP only; no live provider, signed installation or UI acceptance." }));
            return Failures.Count == 0 ? 0 : 1;
        }
        finally { Directory.Delete(root, recursive: true); }

        async Task Case(string name, Func<Fixture, Task> check, Action<Handler>? configure = null)
        {
            Console.WriteLine("CASE " + name);
            try
            {
                var fixture = await Fixture.Create(Path.Combine(root, Guid.NewGuid().ToString("N")));
                configure?.Invoke(fixture.Handler);
                await check(fixture);
            }
            catch (Exception exception)
            {
                // Exception messages could contain supplied private markers: report only the type.
                Check(false, name + " unexpectedly threw " + exception.GetType().Name);
                var methods = new System.Diagnostics.StackTrace(exception, false).GetFrames()?
                    .Select(frame => frame.GetMethod())
                    .Where(method => method?.DeclaringType?.Assembly == typeof(CliSharingChecks).Assembly)
                    .Select(method => method!.DeclaringType!.FullName + "." + method.Name)
                    .Distinct(StringComparer.Ordinal).Take(3).ToArray() ?? [];
                Console.WriteLine("DIAGNOSTIC method identities only: " + string.Join(" <- ", methods));
            }
        }
    }

    private static string Ns(string value) => JsonSerializer.Serialize(new { @namespace = value });
    private static void Check(bool condition, string label)
    {
        _assertions++;
        if (!condition) Failures.Add(label);
        Console.WriteLine((condition ? "PASS " : "FAIL ") + label);
    }
    private static JsonObject? Success(Result result, string label)
    {
        Check(result.Exit == 0, label + " exits successfully (actual " + result.Exit + ")");
        var envelope = JsonNode.Parse(result.Output)?.AsObject();
        Check(envelope?["ok"]?.GetValue<bool>() == true && envelope["error"] is null, label + " successful JSON envelope");
        return envelope?["data"]?.AsObject();
    }
    private sealed record Result(int Exit, string Output, string Error);
    private sealed record Call(string Route, string? Namespace, JsonObject Body);

    private sealed class Fixture(string path, DropboxConnectionBinding binding)
    {
        internal string StatePath => path;
        internal string ConnectionId => binding.ConnectionId;
        internal Handler Handler { get; } = new();
        internal static async Task<Fixture> Create(string path)
        {
            Directory.CreateDirectory(path);
            var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, Account, Root, Home,
                DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequestedScopes(true)).Normalize();
            using var instance = PilotInstanceLock.Acquire(Path.Combine(path, "state", "instance.lock"));
            await new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(binding);
            return new Fixture(path, binding);
        }
        internal Task<Result> Run(params string[] args) => RunWithAccount(binding.ConnectionId, args);
        internal Task<Result> RunWithAccount(string account, params string[] args) => RunExact([..args, "--account", account]);
        internal async Task<Result> RunExact(params string[] args)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exit = await SyncCli.ExecuteAsync(["--cli", ..args, "--json"], output, error,
                () => new Runtime(path, Handler));
            var result = new Result(exit, output.ToString(), error.ToString());
            Check(PrivateValues.All(value => !(result.Output + result.Error).Contains(value, StringComparison.Ordinal)) &&
                  !(result.Output + result.Error).Contains("Bearer ", StringComparison.Ordinal), "stdout/stderr omit fixture secrets and continuations");
            return result;
        }
    }

    private sealed class Runtime : SyncCliOperationsBase
    {
        private readonly PilotInstanceLock _instance;
        private readonly HttpClient _http;
        private readonly SyncAuthorityRuntimeHost _host;
        private readonly bool _borrowed;
        internal Runtime(string path, Handler handler, bool borrowed = false)
        {
            _borrowed = borrowed;
            _instance = PilotInstanceLock.Acquire(Path.Combine(path, "state", "instance.lock"));
            _http = new HttpClient(handler, disposeHandler: false);
            var authority = _instance.StateAuthority;
            _host = new SyncAuthorityRuntimeHost(authority, _http, new SyncPairStore(authority), new SyncRuntimeStateStore(authority),
                new DropboxConnectionStore(authority), new MemoryDropboxRefreshTokenStore(Refresh),
                new SyncAuthorityDatabase(Path.Combine(path, "authority.db")));
        }
        protected override SyncAuthorityRuntimeHost Host => _host;
        protected override PilotStateAuthority StateAuthority => _instance.StateAuthority;
        public override string Mode => "controlled-http";
        public override async ValueTask DisposeAsync()
        {
            if (!_borrowed) await DisposeOwnedAsync();
        }
        internal async ValueTask DisposeOwnedAsync()
        {
            try { await base.DisposeAsync(); }
            finally { _http.Dispose(); _instance.Dispose(); }
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal List<Call> Calls { get; } = [];
        internal List<Call> Effects { get; } = [];
        internal int OAuthCalls, AccountCalls;
        internal int ListedLinkCount = 4;
        internal string? IdentityMismatch, MutationFailure;
        internal Dictionary<string, string> EffectFailureByUrl { get; } = [];
        internal bool UnendingPages, RefreshWrongItem, DenyTeam, ChangeRequestOnRefresh, WrongRequestId;
        private int _requestGets;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri?.Host != "api.dropboxapi.com" || request.Method != HttpMethod.Post)
                throw new InvalidOperationException("Unexpected fixture HTTP host or method.");
            var route = request.RequestUri.AbsolutePath;
            if (route == "/oauth2/token")
            {
                OAuthCalls++;
                var form = await request.Content!.ReadAsStringAsync(token);
                Check(form.Contains("grant_type=refresh_token", StringComparison.Ordinal) && form.Contains(Refresh, StringComparison.Ordinal),
                    "real refresh boundary uses bounded in-memory credential");
                return Json(new { access_token = Access, expires_in = 3600, account_id = IdentityMismatch == "token-account" ? "dbid:other" : Account });
            }
            Check(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == Access,
                "HTTP uses fixture access token");
            if (route == "/2/users/get_current_account")
            {
                AccountCalls++;
                return Json(new { account_id = IdentityMismatch == "current-account" ? "dbid:other" : Account,
                    root_info = new { root_namespace_id = IdentityMismatch == "current-namespace" ? "foreign-root" : Root, home_namespace_id = Home } });
            }
            route = route[3..];
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            var ns = JsonNode.Parse(request.Headers.GetValues("Dropbox-API-Path-Root").Single())!["namespace_id"]!.GetValue<string>();
            var call = new Call(route, ns, body);
            Calls.Add(call);
            if (route == "sharing/list_shared_links")
            {
                var later = body["cursor"] is not null;
                return Json(new { links = Enumerable.Range(later ? 2 : 0, later ? ListedLinkCount - 2 : 2)
                    .Select(Link).ToArray(), has_more = !later || UnendingPages, cursor = LinkCursor });
            }
            if (route is "file_requests/list_v2" or "file_requests/list/continue")
            {
                var later = route.EndsWith("/continue", StringComparison.Ordinal);
                return Json(new { file_requests = new[] { Request(later ? "request2" : "request0"), Request(later ? "request3" : "request1") },
                    has_more = !later || UnendingPages, cursor = RequestCursor });
            }
            if (route == "sharing/get_shared_link_metadata")
            {
                var link = Link(Index(body["url"]!.GetValue<string>()));
                if (RefreshWrongItem) link["id"] = "id:different-item";
                return Json(link);
            }
            if (route == "file_requests/get")
            {
                _requestGets++;
                var id = body["id"]!.GetValue<string>();
                if (id == "missing") return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent(ProviderPrivate) };
                var result = Request(WrongRequestId ? "other-request" : id);
                if (ChangeRequestOnRefresh && _requestGets > 1) result["title"] = "Changed elsewhere";
                return Json(result);
            }
            if (route == "files/get_metadata")
                return Json(new Dictionary<string, object> { [".tag"] = "folder", ["id"] = "id:inbox", ["name"] = "Inbox", ["path_lower"] = "/inbox", ["path_display"] = "/Inbox" });
            if (route is not ("sharing/modify_shared_link_settings" or "sharing/revoke_shared_link" or "file_requests/update"))
                throw new InvalidOperationException("Unexpected fixture HTTP route.");
            Effects.Add(call);
            if (body["url"]?.GetValue<string>() is { } effectUrl && EffectFailureByUrl.TryGetValue(effectUrl, out var failure))
                return new HttpResponseMessage(failure == "http500" ? HttpStatusCode.InternalServerError : HttpStatusCode.Conflict)
                { Content = new StringContent(ProviderPrivate + Access) };
            if (MutationFailure == "http500") return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(ProviderPrivate + Access) };
            if (MutationFailure == "transport") throw new HttpRequestException(ProviderPrivate + Access);
            if (MutationFailure == "malformed") return Json(new { incomplete = true, private_value = ProviderPrivate });
            if (route == "sharing/revoke_shared_link") return Json<object?>(null);
            if (route == "sharing/modify_shared_link_settings")
            {
                var result = Link(Index(body["url"]!.GetValue<string>()));
                var settings = body["settings"]!.AsObject();
                var permissions = result["link_permissions"]!.AsObject();
                if (settings["audience"] is { } audience) permissions["effective_audience"] = new JsonObject { [".tag"] = audience.DeepClone() };
                if (settings["allow_download"] is { } allow) permissions["allow_download"] = allow.DeepClone();
                if (settings["require_password"] is { } require) permissions["require_password"] = require.DeepClone();
                if (settings["expires"] is { } expires) result["expires"] = expires.DeepClone();
                if (MutationFailure == "wrong-identity") result["id"] = "id:wrong-effect";
                return Json(result);
            }
            var updated = Request(body["id"]!.GetValue<string>());
            foreach (var field in new[] { "title", "destination", "description" })
                if (body[field] is { } value) updated[field] = value.DeepClone();
            if (body["open"] is { } open) updated["is_open"] = open.DeepClone();
            if (body["deadline"] is { } deadline) updated["deadline"] = deadline["update"]?.DeepClone();
            if (MutationFailure == "wrong-identity") updated["id"] = "wrong-effect";
            return Json(updated);
        }

        private int Index(string url) => Enumerable.Range(0, ListedLinkCount).Single(index => LinkUrl(index) == url);
        private JsonObject Link(int index) => new()
        {
            [".tag"] = "file", ["url"] = LinkUrl(index), ["id"] = "id:link" + index, ["name"] = "item" + index + ".txt",
            ["path_lower"] = "/item" + index + ".txt", ["private_value"] = ProviderPrivate,
            ["link_permissions"] = new JsonObject
            {
                ["effective_audience"] = new JsonObject { [".tag"] = "public" }, ["link_access_level"] = new JsonObject { [".tag"] = "viewer" },
                ["can_revoke"] = true, ["require_password"] = false, ["can_set_password"] = true, ["can_remove_password"] = true,
                ["can_set_expiry"] = true, ["can_remove_expiry"] = true, ["allow_download"] = true, ["can_allow_download"] = true, ["can_disallow_download"] = true,
                ["audience_options"] = new JsonArray(new JsonObject { ["audience"] = new JsonObject { [".tag"] = "public" }, ["allowed"] = true },
                    new JsonObject { ["audience"] = new JsonObject { [".tag"] = "team" }, ["allowed"] = !DenyTeam })
            }
        };
        private static JsonObject Request(string id) => new()
        {
            ["id"] = id, ["url"] = "https://www.dropbox.com/request/" + id, ["title"] = "Fixture " + id,
            ["destination"] = "/Inbox", ["description"] = "Original description", ["created"] = "2026-01-01T00:00:00Z",
            ["is_open"] = false, ["file_count"] = 7, ["private_value"] = ProviderPrivate,
            ["deadline"] = new JsonObject { ["deadline"] = Deadline, ["allow_late_uploads"] = new JsonObject { [".tag"] = "seven_days" } }
        };
        private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
