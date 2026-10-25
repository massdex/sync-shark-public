using Massdex.SyncShark;
using System.Globalization;
using System.IO;

// Focused source tests for the shared pause contract. The helper checks cover the option set, the
// deadline grammar, the identity rules and the D2 wall-clock arithmetic; the consumer checks drive
// the real CLI registry, dispatcher and operations over a real host and preferences store, which is
// where the scope, refusal and fail-closed behaviour lives. Written under a source-only hold and
// never executed, so every result is UNVERIFIED.
internal static class PauseContractChecks
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        CheckOptionSet();
        CheckDeadlineGrammar();
        CheckDeadlineArithmetic();
        CheckIdentityAndScopeRules();
        await CheckStoreScopesAsync(Path.Combine(root, "store"));
        await CheckCliConsumerAsync(Path.Combine(root, "cli"));
        Console.WriteLine("PASS pause contract source checks: options, grammar, identity, scope rules, " +
                          "preserved scopes, expiry and the real CLI pause and resume operations");
    }

    private static void CheckOptionSet()
    {
        var labels = PauseOption.All.Select(option => option.Label).ToArray();
        Equal("5 Minutes", labels[0]);
        Equal("15 Minutes", labels[1]);
        Equal("30 Minutes", labels[2]);
        Equal("1 Hour", labels[3]);
        Equal("Until Tomorrow", labels[4]);
        Equal("Indefinitely", labels[5]);
        Equal(6, labels.Distinct(StringComparer.Ordinal).Count());
        True(PauseOption.All[5].StopSquare);
        False(PauseOption.All.Take(5).Any(option => option.StopSquare));
        True(PauseOption.All[4].Detail.Contains("09:00", StringComparison.Ordinal));
        True(PauseOption.FromMinutes(5) is not null && PauseOption.FromMinutes(15) is not null &&
             PauseOption.FromMinutes(30) is not null && PauseOption.FromMinutes(60) is not null);
        True(PauseOption.FromMinutes(10) is null);
    }

    private static void CheckDeadlineGrammar()
    {
        var nowUtc = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        True(PauseSchedule.TryParseDeadline("2026-09-18T09:00:00+01:00", nowUtc, out var offsetDeadline, out _));
        Equal(new DateTimeOffset(2026, 9, 18, 8, 0, 0, TimeSpan.Zero), offsetDeadline);
        True(PauseSchedule.TryParseDeadline("2026-09-18T09:00:00Z", nowUtc, out var utcDeadline, out _));
        Equal(new DateTimeOffset(2026, 9, 18, 9, 0, 0, TimeSpan.Zero), utcDeadline);
        True(PauseSchedule.TryParseDeadline("2026-09-18T09:00Z", nowUtc, out _, out _));
        // Documented grammar only: no date-only, no space separator, no bare local time.
        False(PauseSchedule.TryParseDeadline("2026-09-18", nowUtc, out _, out var dateReason));
        True(dateReason.Contains("offset", StringComparison.OrdinalIgnoreCase));
        False(PauseSchedule.TryParseDeadline("2026-09-18 09:00:00+01:00", nowUtc, out _, out _));
        False(PauseSchedule.TryParseDeadline("2026-09-18T09:00:00", nowUtc, out _, out _));
        False(PauseSchedule.TryParseDeadline("18/09/2026 09:00:00+01:00", nowUtc, out _, out _));
        False(PauseSchedule.TryParseDeadline("2026-09-17T09:00:00+01:00", nowUtc, out _, out _));
        False(PauseSchedule.TryParseDeadline("2026-11-30T09:00:00Z", nowUtc, out _, out _));
        False(PauseSchedule.TryParseDeadline(null, nowUtc, out _, out _));
        // The same validator is applied at the authoritative mutation.
        True(PauseSchedule.ValidateDeadline(nowUtc.AddHours(1), nowUtc, out _));
        False(PauseSchedule.ValidateDeadline(nowUtc.AddMinutes(-1), nowUtc, out var staleReason));
        True(staleReason.Contains("future", StringComparison.OrdinalIgnoreCase));
    }

    private static void CheckDeadlineArithmetic()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var nowUtc = new DateTimeOffset(2026, 9, 17, 20, 0, 0, TimeSpan.Zero);
        var tomorrow = PauseSchedule.Resolve(PauseOption.UntilTomorrow, nowUtc, zone);
        Equal("2026-09-18 09:00",
            TimeZoneInfo.ConvertTime(tomorrow.UntilUtc!.Value, zone).ToString("yyyy-MM-dd HH:mm"));
        Equal(zone.Id, tomorrow.TimeZoneId);
        var springNow = new DateTimeOffset(2026, 3, 7, 20, 0, 0, TimeSpan.Zero);
        var spring = PauseSchedule.Resolve(PauseOption.UntilTomorrow, springNow, zone);
        Equal("2026-03-08 09:00",
            TimeZoneInfo.ConvertTime(spring.UntilUtc!.Value, zone).ToString("yyyy-MM-dd HH:mm"));
        True(spring.UntilUtc!.Value - springNow < TimeSpan.FromHours(24));
        var invalid = new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified);
        True(zone.IsInvalidTime(invalid));
        Equal("2026-03-08 03:00",
            TimeZoneInfo.ConvertTime(PauseSchedule.ResolveLocalWallClock(invalid, zone), zone)
                .ToString("yyyy-MM-dd HH:mm"));
        var ambiguous = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified);
        True(zone.IsAmbiguousTime(ambiguous));
        Equal(new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero),
            PauseSchedule.ResolveLocalWallClock(ambiguous, zone));
    }

    private static void CheckIdentityAndScopeRules()
    {
        var dropbox = "connection-" + new string('a', 24);
        var google = "gdrive-connection-" + new string('b', 24);
        True(PauseScopeRules.IsExactConnectionId(dropbox));
        True(PauseScopeRules.IsExactConnectionId(google));
        False(PauseScopeRules.IsExactConnectionId("connection-" + new string('A', 24)));
        False(PauseScopeRules.IsExactConnectionId("connection-" + new string('a', 23)));
        False(PauseScopeRules.IsExactConnectionId("connection-" + new string('a', 25)));
        False(PauseScopeRules.IsExactConnectionId("gdrive-connection-" + new string('z', 24)));
        False(PauseScopeRules.IsExactConnectionId("gdrive-connection-"));
        False(PauseScopeRules.IsExactConnectionId("onedrive-connection-" + new string('a', 24)));
        False(PauseScopeRules.IsExactConnectionId(null));
        False(PauseScopeRules.IsExactConnectionId(string.Empty));
        var refusal = PauseScopeRules.ActiveRunRefusal("account");
        True(refusal.Contains("refused", StringComparison.OrdinalIgnoreCase));
        True(refusal.Contains("no account was paused globally", StringComparison.Ordinal));
        True(PauseScopes.IsKnown(PauseScopes.Global) && PauseScopes.IsKnown(PauseScopes.Account) &&
             PauseScopes.IsKnown(PauseScopes.Pair));
        False(PauseScopes.IsKnown("all"));
        True(Throws(() => PauseScopes.Require("all")));
    }

    private static async Task CheckStoreScopesAsync(string root)
    {
        Directory.CreateDirectory(root);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var authority = appLock.StateAuthority;
        var store = new UiPreferencesStore(authority);
        var now = DateTimeOffset.UtcNow;
        var dropbox = "connection-" + new string('c', 24);
        var google = "gdrive-connection-" + new string('d', 24);

        // Both provider identities save, load and stay bound to their exact account.
        foreach (var id in new[] { dropbox, google })
        {
            var resolution = PauseSchedule.Resolve(PauseOption.Minutes30, now, TimeZoneInfo.Local);
            var current = store.Load();
            // Exercise each isolated scope from an unpaused baseline. The separate overlap
            // case below proves that an intentionally retained other-account pause survives.
            var pauses = new List<SyncAccountPause>();
            pauses.Add(new SyncAccountPause(id, resolution.Indefinitely, resolution.UntilUtc, resolution.TimeZoneId));
            await store.SaveAsync(current with
            {
                PauseUntilResumed = false, PauseUntilUtc = null, PauseTimeZoneId = null, AccountPauses = pauses
            });
            var reloaded = new UiPreferencesStore(authority).Load();
            var entry = PauseStateProjection.AccountPause(reloaded, id, now);
            True(entry is not null);
            True(entry!.UntilUtc == resolution.UntilUtc);
            Equal(TimeZoneInfo.Local.Id, entry.TimeZoneId);
            False(PauseStateProjection.GlobalPaused(reloaded, now));
            var other = string.Equals(id, dropbox, StringComparison.Ordinal) ? google : dropbox;
            False(PauseStateProjection.AccountPaused(reloaded, other, now));
        }

        // Malformed and unknown identities are refused by the store, not stored.
        foreach (var invalid in new[]
                 {
                     "connection-" + new string('a', 23), "gdrive-connection-" + new string('z', 24),
                     "connection-" + new string('A', 24), "unknown-connection-" + new string('a', 24)
                 })
        {
            True(await SaveIsRejectedAsync(store, invalid));
        }

        // Overlapping scopes: resuming one account leaves the global pause and the other account paused.
        var kept = store.Load();
        await store.SaveAsync(kept with
        {
            PauseUntilResumed = true, PauseUntilUtc = null, PauseTimeZoneId = TimeZoneInfo.Local.Id,
            AccountPauses = new List<SyncAccountPause>
            {
                new(dropbox, true, null, TimeZoneInfo.Local.Id), new(google, true, null, TimeZoneInfo.Local.Id)
            }
        });
        var allPaused = store.Load();
        True(PauseStateProjection.GlobalPaused(allPaused, now));
        True(PauseStateProjection.AccountPaused(allPaused, dropbox, now));
        True(PauseStateProjection.AccountPaused(allPaused, google, now));
        await store.SaveAsync(store.Load() with
        {
            AccountPauses = (store.Load().AccountPauses ?? [])
                .Where(item => !string.Equals(item.ConnectionId, dropbox, StringComparison.Ordinal)).ToArray()
        });
        var afterResume = new UiPreferencesStore(authority).Load();
        False(PauseStateProjection.AccountPaused(afterResume, dropbox, now));
        True(PauseStateProjection.AccountPaused(afterResume, google, now));
        True(PauseStateProjection.GlobalPaused(afterResume, now));

        // Expiry across a restart and across a sleep gap.
        var lapsed = now.AddMinutes(-1);
        await store.SaveAsync(store.Load() with
        {
            PauseUntilResumed = false, PauseUntilUtc = lapsed, PauseTimeZoneId = TimeZoneInfo.Local.Id
        });
        var restarted = new UiPreferencesStore(authority).Load();
        True(restarted.PauseUntilUtc == lapsed);
        True(PauseSchedule.IsExpired(restarted.PauseUntilResumed, restarted.PauseUntilUtc, now));
        False(PauseStateProjection.GlobalPaused(restarted, now));
        False(PauseStateProjection.GlobalPaused(restarted, now.AddHours(9)));
    }

    private static async Task CheckCliConsumerAsync(string root)
    {
        Directory.CreateDirectory(root);
        await using var runtime = new PauseRuntime(root);
        var authority = runtime.State;
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, "dbid:ppause",
            "ppause-namespace", "ppause-home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        await new DropboxConnectionStore(authority).SaveNewAsync(binding);
        var accountId = binding.ConnectionId;
        var now = DateTimeOffset.UtcNow;

        // Success: global indefinite pause through the real dispatcher.
        var global = await DispatchAsync(runtime, ["--cli", "pause", "--until-resumed", "--yes", "--json"]);
        True(global is SyncCliPauseData { Paused: true, UntilResumed: true, UntilUtc: null } globalData &&
             globalData.Scope == PauseScopes.Global);
        var afterGlobal = new UiPreferencesStore(authority).Load();
        True(afterGlobal.PauseUntilResumed && afterGlobal.PauseUntilUtc is null);
        Equal(TimeZoneInfo.Local.Id, afterGlobal.PauseTimeZoneId);

        // Success: account scope with the shared 30 minute option, exact account only.
        var account = await DispatchAsync(runtime,
            ["--cli", "pause", "--account", accountId, "--minutes", "30", "--yes", "--json"]);
        True(account is SyncCliPauseData { Paused: true, UntilUtc: not null } accountData &&
             accountData.Scope == PauseScopes.Account);
        var afterAccount = new UiPreferencesStore(authority).Load();
        var entry = PauseStateProjection.AccountPause(afterAccount, accountId, now);
        True(entry is not null && entry!.UntilUtc is not null && entry.TimeZoneId == TimeZoneInfo.Local.Id);
        True(afterAccount.PauseUntilResumed);
        True(afterAccount.PauseUntilUtc is null);
        True(runtime.Engine.IsAccountPaused(accountId));

        // Success: Until Tomorrow resolves to the shared resume hour with the captured timezone.
        var tomorrow = await DispatchAsync(runtime,
            ["--cli", "pause", "--account", accountId, "--until-tomorrow", "--yes", "--json"]);
        True(tomorrow is SyncCliPauseData { UntilUtc: not null });
        var tomorrowData = (SyncCliPauseData)tomorrow;
        True(TimeZoneInfo.ConvertTime(tomorrowData.UntilUtc!.Value, TimeZoneInfo.Local).Hour ==
             PauseSchedule.TomorrowResumeHour);
        Equal(TimeZoneInfo.Local.Id, tomorrowData.TimeZoneId);

        // Fail closed: an expired or unusable deadline never becomes an indefinite pause.
        var beforeFailure = new UiPreferencesStore(authority).Load();
        var expired = now.AddMinutes(-5).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        True(await DispatchIsRejectedAsync(runtime,
            ["--cli", "pause", "--account", accountId, "--until", expired, "--yes", "--json"]));
        True(await DispatchIsRejectedAsync(runtime,
            ["--cli", "pause", "--until", "2026-09-18", "--yes", "--json"]));
        var afterFailure = new UiPreferencesStore(authority).Load();
        True(afterFailure.PauseUntilResumed == beforeFailure.PauseUntilResumed &&
             afterFailure.PauseUntilUtc == beforeFailure.PauseUntilUtc);
        Equal(beforeFailure.AccountPauses?.Count ?? 0, afterFailure.AccountPauses?.Count ?? 0);

        // An unknown account identity is refused and never widened to a global pause.
        True(await DispatchIsRejectedAsync(runtime,
            ["--cli", "pause", "--account", "gdrive-connection-" + new string('e', 24), "--yes", "--json"]));
        var afterUnknown = new UiPreferencesStore(authority).Load();
        True(afterUnknown.PauseUntilResumed == beforeFailure.PauseUntilResumed);
        Equal(beforeFailure.AccountPauses?.Count ?? 0, afterUnknown.AccountPauses?.Count ?? 0);

        // Resume keeps the overlapping scopes: the global pause survives an account resume.
        var resumed = await DispatchAsync(runtime,
            ["--cli", "resume", "--account", accountId, "--yes", "--json"]);
        True(resumed is SyncCliPauseData { Paused: false });
        var afterResume = new UiPreferencesStore(authority).Load();
        False(PauseStateProjection.AccountPaused(afterResume, accountId, now));
        True(afterResume.PauseUntilResumed);
        True(afterResume.PauseUntilUtc is null);
    }

    private static async Task<bool> SaveIsRejectedAsync(UiPreferencesStore store, string connectionId)
    {
        try
        {
            await store.SaveAsync(store.Load() with
            {
                AccountPauses = new List<SyncAccountPause>
                {
                    new(connectionId, true, null, TimeZoneInfo.Local.Id)
                }
            });
            return false;
        }
        catch (InvalidDataException) { return true; }
    }

    private static async Task<bool> DispatchIsRejectedAsync(SyncCliOperationsBase runtime, string[] args)
    {
        try
        {
            await DispatchAsync(runtime, args);
            return false;
        }
        catch (Exception error) when (error is SyncCliUsageException or SyncCliOperationException or
                                          SyncCliTargetException or InvalidOperationException)
        {
            return true;
        }
    }

    private static async Task<object> DispatchAsync(SyncCliOperationsBase runtime, string[] args) =>
        await SyncCliRegistry.DispatchAsync(runtime, SyncCliRegistry.Parse(args), TextWriter.Null,
            CancellationToken.None);

    private sealed class PauseRuntime : SyncCliOperationsBase
    {
        private readonly PilotInstanceLock _instance;
        private readonly HttpClient _http;
        internal PauseRuntime(string path)
        {
            _instance = PilotInstanceLock.Acquire(Path.Combine(path, "state", "instance.lock"));
            _http = new HttpClient(new NoNetworkHandler(), disposeHandler: false);
            var authority = _instance.StateAuthority;
            Engine = new SyncAuthorityRuntimeHost(authority, _http);
        }
        internal SyncAuthorityRuntimeHost Engine { get; }
        internal PilotStateAuthority State => _instance.StateAuthority;
        protected override SyncAuthorityRuntimeHost Host => Engine;
        protected override PilotStateAuthority StateAuthority => State;
        public override string Mode => "pause-contract-checks";
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This pause check performs no provider call.");
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (Exception) { return true; }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException("Expected '" + expected + "' but observed '" + actual + "'.");
    }

    private static void True(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Expected a true condition.");
    }

    private static void False(bool condition)
    {
        if (condition) throw new InvalidOperationException("Expected a false condition.");
    }
}
