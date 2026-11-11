using System.Globalization;

namespace Massdex.SyncShark;

// The single shared definition of the pause option set, the scope names and the deadline
// arithmetic. Tray flyout, native tray menu, account controls, Home and the CLI all read
// these definitions instead of repeating their own lists.
public enum PauseKind
{
    Minutes,
    UntilTomorrow,
    Indefinitely
}

public sealed record PauseOption(PauseKind Kind, int? Minutes, string Label, string Detail)
{
    public bool StopSquare => Kind == PauseKind.Indefinitely;

    public static PauseOption Minutes5 { get; } = Timed(5);
    public static PauseOption Minutes15 { get; } = Timed(15);
    public static PauseOption Minutes30 { get; } = Timed(30);
    public static PauseOption Minutes60 { get; } = Timed(60);

    // Menu labels are exactly the option names; the scope and the deadline live in the menu
    // heading or tooltip instead of being repeated on every row.
    public static PauseOption UntilTomorrow { get; } = new(PauseKind.UntilTomorrow, null, "Until Tomorrow",
        "Pauses until " + PauseSchedule.TomorrowResumeHour.ToString("00", CultureInfo.InvariantCulture) +
        ":00 on the next local calendar day, using the timezone captured now.");

    public static PauseOption Indefinitely { get; } = new(PauseKind.Indefinitely, null, "Indefinitely",
        "Pauses until you explicitly resume. No automatic expiry is stored and no active work is cancelled.");

    public static IReadOnlyList<PauseOption> All { get; } =
        [Minutes5, Minutes15, Minutes30, Minutes60, UntilTomorrow, Indefinitely];

    private static PauseOption Timed(int minutes) => new(PauseKind.Minutes, minutes, DescribeMinutes(minutes),
        "Pauses automatic work for " + minutes.ToString(CultureInfo.InvariantCulture) + " minute(s) from now.");

    public static string DescribeMinutes(int minutes) => minutes switch
    {
        60 => "1 Hour",
        _ => minutes.ToString(CultureInfo.InvariantCulture) + " Minutes"
    };

    public static PauseOption? FromMinutes(int minutes) =>
        All.FirstOrDefault(option => option.Kind == PauseKind.Minutes && option.Minutes == minutes);
}

// The resolved deadline plus the timezone captured at selection time. The UTC instant is
// persisted and never recalculated on restart or timezone change.
public sealed record PauseResolution(bool Indefinitely, DateTimeOffset? UntilUtc, string TimeZoneId,
    DateTimeOffset? LocalResume)
{
    public string LocalResumeLabel => LocalResume is { } local
        ? local.ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture)
        : "until resumed";
}

public static class PauseScopes
{
    public const string Global = "global";
    public const string Account = "account";
    public const string Pair = "pair";

    public static bool IsKnown(string? scope) => scope is Global or Account or Pair;

    public static string Require(string? scope) => IsKnown(scope)
        ? scope!
        : throw new SyncCliUsageException(
            "A pause or resume scope of 'global', 'account' or 'pair' is required; no implicit scope is ever chosen.");
}

public static class PauseSchedule
{
    public const int TomorrowResumeHour = 9;
    private static readonly TimeSpan DeadlineCeiling = TimeSpan.FromDays(30);

    // D2: Until Tomorrow means the resume hour on the next local calendar day at the moment of
    // selection, with that moment's timezone captured and the deadline resolved to UTC.
    public static PauseResolution Resolve(PauseOption option, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(zone);
        switch (option.Kind)
        {
            case PauseKind.Indefinitely:
                return new PauseResolution(true, null, zone.Id, null);
            case PauseKind.Minutes:
                var until = nowUtc.AddMinutes(option.Minutes!.Value);
                return new PauseResolution(false, until, zone.Id, TimeZoneInfo.ConvertTime(until, zone));
            case PauseKind.UntilTomorrow:
                var localNow = TimeZoneInfo.ConvertTime(nowUtc, zone);
                var wallClock = localNow.Date.AddDays(1).AddHours(TomorrowResumeHour);
                var resolved = ResolveLocalWallClock(wallClock, zone);
                return new PauseResolution(false, resolved, zone.Id, TimeZoneInfo.ConvertTime(resolved, zone));
            default:
                throw new ArgumentOutOfRangeException(nameof(option));
        }
    }

    // D2: an invalid local time advances to the first valid local minute; an ambiguous local
    // time selects the later UTC instant.
    public static DateTimeOffset ResolveLocalWallClock(DateTime localWallClock, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = DateTime.SpecifyKind(localWallClock, DateTimeKind.Unspecified);
        var guard = 0;
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
            if (++guard > 24 * 60)
                throw new InvalidDataException("No valid local time was reachable within a day of the requested wall clock.");
        }
        if (zone.IsAmbiguousTime(local))
        {
            // The later UTC instant carries the smaller offset.
            var later = local - zone.GetAmbiguousTimeOffsets(local).Min();
            return new DateTimeOffset(later, TimeSpan.Zero);
        }
        return new DateTimeOffset(local - zone.GetUtcOffset(local), TimeSpan.Zero);
    }

    public static TimeZoneInfo? TryResolveZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return null;
        return TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone) ? zone : null;
    }

    public static bool IsPauseActive(bool untilResumed, DateTimeOffset? untilUtc, DateTimeOffset nowUtc) =>
        untilResumed || untilUtc > nowUtc;

    // A deadline found in the past is expired whether it was reached while running, after a
    // restart or after the machine was asleep.
    public static bool IsExpired(bool untilResumed, DateTimeOffset? untilUtc, DateTimeOffset nowUtc) =>
        !untilResumed && untilUtc is { } until && until <= nowUtc;

    public static string Describe(bool untilResumed, DateTimeOffset? untilUtc, string? timeZoneId, DateTimeOffset nowUtc)
    {
        if (untilResumed) return "Paused Until Resumed";
        if (untilUtc is not { } until || until <= nowUtc) return "Pause Sync";
        var local = TimeZoneInfo.ConvertTime(until, TryResolveZone(timeZoneId) ?? TimeZoneInfo.Local);
        return "Paused Until " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    public static string DescribeDetail(bool untilResumed, DateTimeOffset? untilUtc, string? timeZoneId, DateTimeOffset nowUtc)
    {
        if (untilResumed) return "Paused until you resume. No deadline is stored.";
        if (untilUtc is not { } until) return "Not paused by a deadline.";
        if (until <= nowUtc) return "The pause deadline has passed.";
        var zone = TryResolveZone(timeZoneId) ?? TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(until, zone);
        return "Resumes " + local.ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture) +
               " (" + zone.Id + "), " + until.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
               " UTC.";
    }

    // The CLI accepts an exact deadline only with an explicit offset, never a bare local time.
    public static bool TryParseDeadline(string? text, DateTimeOffset nowUtc, out DateTimeOffset deadline, out string reason)
    {
        deadline = default;
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            reason = "An exact deadline requires an ISO-8601 timestamp with an explicit offset.";
            return false;
        }
        var trimmed = text.Trim();
        // Documented grammar only: a date, the literal 'T', a time with an optional seconds and
        // fraction part, and a mandatory explicit offset ('Z' or +/-hh:mm). A date-only value, a
        // space instead of 'T', or a bare local time is rejected.
        if (!DeadlineGrammar.IsMatch(trimmed) || !HasExplicitOffset(trimmed))
        {
            reason = "An exact deadline requires an ISO-8601 timestamp with an explicit offset, for example 2026-09-18T09:00:00+01:00.";
            return false;
        }
        if (!DateTimeOffset.TryParseExact(trimmed, DeadlineFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            reason = "The exact deadline is not a valid instant with an explicit offset.";
            return false;
        }
        if (!ValidateDeadline(parsed, nowUtc, out reason)) return false;
        deadline = parsed.ToUniversalTime();
        return true;
    }

    // The same check is applied at the authoritative mutation, so a deadline that was valid in a
    // front end but has since expired can never be committed, and never degrades into an
    // open-ended pause.
    public static bool ValidateDeadline(DateTimeOffset deadline, DateTimeOffset nowUtc, out string reason)
    {
        reason = string.Empty;
        if (deadline <= nowUtc)
        {
            reason = "The exact deadline must be in the future.";
            return false;
        }
        if (deadline - nowUtc > DeadlineCeiling)
        {
            reason = "The exact deadline must be within 30 days. Use --until-resumed for a pause without a deadline.";
            return false;
        }
        return true;
    }

    private static readonly string[] DeadlineFormats =
    [
        "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"
    ];

    private static readonly System.Text.RegularExpressions.Regex DeadlineGrammar =
        new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]{1,7})?)?(Z|[+-][0-9]{2}:[0-9]{2})$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool HasExplicitOffset(string text) =>
        text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) ||
        text.Length >= 6 && (text[^6] is '+' or '-') && text[^3] == ':';
}

// Displayed pause state always derives from the stored state and the current clock; a scope
// never silently falls back to another scope.
public static class PauseStateProjection
{
    public static bool GlobalPaused(UiPreferences preferences, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return PauseSchedule.IsPauseActive(preferences.PauseUntilResumed, preferences.PauseUntilUtc, nowUtc);
    }

    public static SyncAccountPause? AccountPause(UiPreferences preferences, string? connectionId, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (string.IsNullOrEmpty(connectionId)) return null;
        return (preferences.AccountPauses ?? []).FirstOrDefault(item =>
            string.Equals(item.ConnectionId, connectionId, StringComparison.Ordinal) && item.IsActiveAt(nowUtc));
    }

    public static bool AccountPaused(UiPreferences preferences, string? connectionId, DateTimeOffset nowUtc) =>
        AccountPause(preferences, connectionId, nowUtc) is not null;

    public static IReadOnlyList<SyncAccountPause> ActiveAccountPauses(UiPreferences preferences, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return (preferences.AccountPauses ?? []).Where(item => item.IsActiveAt(nowUtc)).ToArray();
    }

    // The account pauses whose deadline has passed and that never resume on their own. Continuous sync's paused
    // branch waits for a Kick, so expiry must be detected to clear the entry and re-evaluate that account's pairs.
    public static IReadOnlyList<SyncAccountPause> ExpiredAccountPauses(UiPreferences preferences, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return (preferences.AccountPauses ?? [])
            .Where(item => PauseSchedule.IsExpired(item.UntilResumed, item.UntilUtc, nowUtc)).ToArray();
    }
}

public static class PauseScopeRules
{
    // D8 and the owner's conservative rule: the app holds no account-scoped control over an active
    // run, so an account request that would have to control one is refused with an explanation
    // instead of being widened to every account.
    public static string ActiveRunRefusal(string scope) =>
        "A sync run is active and there is no exact " + scope + " control for an active run, so this request was " +
        "refused. Nothing was changed in another scope and no account was paused globally. Wait for the run to " +
        "finish, or use the explicit run pause and resume actions, then apply this " + scope + " pause.";

    // Bounded, provider-aware identity validation over the existing rules: a Dropbox connection id
    // or an approved Google Drive connection id, each followed by exactly 24 lowercase hex digits.
    public static bool IsExactConnectionId(string? connectionId)
    {
        if (string.IsNullOrEmpty(connectionId)) return false;
        if (connectionId.StartsWith("gdrive-connection-", StringComparison.Ordinal))
            return IsLowerHex24(connectionId.AsSpan("gdrive-connection-".Length));
        if (connectionId.StartsWith("connection-", StringComparison.Ordinal))
            return IsLowerHex24(connectionId.AsSpan("connection-".Length));
        return false;
    }

    private static bool IsLowerHex24(ReadOnlySpan<char> value)
    {
        if (value.Length != 24) return false;
        foreach (var character in value)
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        return true;
    }
}
