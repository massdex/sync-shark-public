using System.Globalization;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>Commands operations for the edge module. Translates the registered command to its Host operation and
/// renders the returned facts; the recovery place in the exclusion preview and a refused pair removal's reason are
/// rendered from the returned values without probing the drive or looking the pair up again.</summary>
public sealed class Commands
{
    private const int TransientExitCode = 1;

    /// <summary>Single-writer form; keeps the historic signature by routing diagnostics to the same writer.</summary>
    public Task<int> ExecuteAsync(string[] args, Run.Host host, TextWriter output, CancellationToken ct) =>
        ExecuteAsync(args, host, output, output, ct);

    /// <summary>Translates one registered command to its Host operation and renders the result; diagnostics go to the supplied error writer.</summary>
    public async Task<int> ExecuteAsync(string[] args, Run.Host host, TextWriter output, TextWriter error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(output);
        var invocation = Cli.Parse(args);
        if (invocation is null)
        {
            foreach (var line in Cli.Help()) error.WriteLine(line);
            return Cli.UsageExitCode;
        }

        try
        {
            switch (invocation.CommandKey)
            {
                case CommandKeys.PairsList: return await PairsListAsync(host, output, ct).ConfigureAwait(false);
                case CommandKeys.PairsAdd: return await PairsAddAsync(invocation, host, output, ct).ConfigureAwait(false);
                case CommandKeys.PairsRemove: return await PairsRemoveAsync(invocation, host, output, ct).ConfigureAwait(false);
                case CommandKeys.PairsBind: return await PairsBindAsync(invocation, host, output, ct).ConfigureAwait(false);
                case CommandKeys.PairsExclude: return await PairsExcludeAsync(invocation, host, output, ct).ConfigureAwait(false);
                case CommandKeys.PairsInclude: return await PairsIncludeAsync(invocation, host, ct).ConfigureAwait(false);
                case CommandKeys.PairsExclusions: return await PairsExclusionsAsync(invocation, host, output, ct).ConfigureAwait(false);
                case CommandKeys.IgnoresList: return await IgnoresListAsync(invocation, host, output, ct).ConfigureAwait(false);
                case CommandKeys.IgnoresSet: return await IgnoresSetAsync(invocation, host, ct).ConfigureAwait(false);
                case CommandKeys.HostStart: await host.StartAsync(ct).ConfigureAwait(false); return 0;
                case CommandKeys.HostStop: await host.StopAsync(ct).ConfigureAwait(false); return 0;
                case CommandKeys.Status: return await StatusAsync(invocation, host, output, ct).ConfigureAwait(false);
                case CommandKeys.Pause: await host.PauseAsync(invocation.Value(OptionKeys.Pair), ct).ConfigureAwait(false); return 0;
                case CommandKeys.Resume: await host.ResumeAsync(invocation.Value(OptionKeys.Pair), ct).ConfigureAwait(false); return 0;
                case CommandKeys.Repair: return await RepairAsync(invocation, host, ct).ConfigureAwait(false);
                case CommandKeys.Notices: return await NoticesAsync(invocation, host, output, ct).ConfigureAwait(false);
                case CommandKeys.Resolve: return await ResolveAsync(invocation, host, output, ct).ConfigureAwait(false);
                case CommandKeys.AccountsConnect: await host.ConnectAsync(ct).ConfigureAwait(false); return 0;
                case CommandKeys.AccountsDisconnect: return await AccountsDisconnectAsync(invocation, host, ct).ConfigureAwait(false);
                default: return Cli.UsageExitCode;
            }
        }
        catch (EngineRequestException refusal)
        {
            error.WriteLine(refusal.Message);
            return Cli.UsageExitCode;
        }
    }

    private static async Task<int> PairsListAsync(Run.Host host, TextWriter output, CancellationToken ct)
    {
        var pairs = new List<PairConfig>();
        await foreach (var pair in host.PairsAsync(ct)) pairs.Add(pair);
        output.WriteLine(RenderPairs(pairs));
        return 0;
    }

    private static async Task<int> PairsAddAsync(CliInvocation invocation, Run.Host host, TextWriter output, CancellationToken ct)
    {
        var name = invocation.Value(OptionKeys.Name);
        var local = invocation.Value(OptionKeys.Local);
        var path = invocation.Value(OptionKeys.Path);
        var account = invocation.Value(OptionKeys.Account);
        if (name is null || local is null || path is null || account is null) return Cli.UsageExitCode;

        var firstRun = invocation.Value(OptionKeys.FirstSync) is { } text ? ParseFirstRun(text) : FirstRunPolicy.NewestWins;
        var pair = new PairConfig(ResourceNames.NewId(), name, local, path, account, Array.Empty<string>(),
            Array.Empty<Exclusion>(), firstRun, true, null);
        await host.AddPairAsync(pair, ct).ConfigureAwait(false);
        output.WriteLine(RenderPair(pair));
        return 0;
    }

    private static async Task<int> PairsRemoveAsync(CliInvocation invocation, Run.Host host, TextWriter output, CancellationToken ct)
    {
        var pairId = invocation.Value(OptionKeys.Pair);
        if (pairId is null) return Cli.UsageExitCode;
        var cause = await host.RemovePairAsync(pairId, ct).ConfigureAwait(false);
        output.WriteLine(cause is { } refused ? RenderPairNotRemoved(pairId, refused) : RenderPairRemoved(pairId));
        return 0;
    }

    private static async Task<int> PairsBindAsync(CliInvocation invocation, Run.Host host, TextWriter output, CancellationToken ct)
    {
        var pairId = invocation.Value(OptionKeys.Pair);
        if (pairId is null) return Cli.UsageExitCode;
        await host.BindAsync(pairId, ct).ConfigureAwait(false);
        output.WriteLine(Text.Get(TextKey.ResultRootsBound, (TextArg.Pair, pairId)));
        return 0;
    }

    private static async Task<int> PairsExcludeAsync(CliInvocation invocation, Run.Host host, TextWriter output, CancellationToken ct)
    {
        var pairId = invocation.Value(OptionKeys.Pair);
        var sideText = invocation.Value(OptionKeys.Side);
        var rel = invocation.Value(OptionKeys.Rel);
        if (pairId is null || sideText is null || rel is null || !TryParseSide(sideText, out var side)) return Cli.UsageExitCode;

        var exclusion = new Exclusion(side, rel, invocation.Flag(OptionKeys.Keep));
        var confirmed = invocation.Flag(OptionKeys.Yes);
        var result = await host.SetExclusionAsync(pairId, exclusion, include: false, confirmed, ct).ConfigureAwait(false);

        if (confirmed)
            output.WriteLine(result.Applied ? RenderExclusionApplied(rel) : RenderExclusionBlocked(rel));
        else if (result.RemoveFiles == 0 || result.RecoveryPlace is null)
            output.WriteLine(RenderExclusionBlocked(rel));
        else
            output.WriteLine(RenderExclusionPreview(rel, side, result.CopyFiles, result.CopyBytes,
                result.RemoveFiles, result.RemoveBytes, result.RecoveryPlace ?? string.Empty));
        return 0;
    }

    private static async Task<int> PairsIncludeAsync(CliInvocation invocation, Run.Host host, CancellationToken ct)
    {
        var pairId = invocation.Value(OptionKeys.Pair);
        var sideText = invocation.Value(OptionKeys.Side);
        var rel = invocation.Value(OptionKeys.Rel);
        if (pairId is null || sideText is null || rel is null || !TryParseSide(sideText, out var side)) return Cli.UsageExitCode;
        await host.SetExclusionAsync(pairId, new Exclusion(side, rel, Keep: false), include: true, confirmed: true, ct).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> PairsExclusionsAsync(CliInvocation invocation, Run.Host host, TextWriter output, CancellationToken ct)
    {
        var pairId = invocation.Value(OptionKeys.Pair);
        if (pairId is null) return Cli.UsageExitCode;
        var pair = await FindPairAsync(host, pairId, ct).ConfigureAwait(false);
        if (pair is null)
        {
            output.WriteLine(Text.Get(TextKey.PairUnknown, (TextArg.Pair, pairId)));
            return Cli.UsageExitCode;
        }
        foreach (var exclusion in pair.Exclusions)
            output.WriteLine(Text.Get(exclusion.Keep ? TextKey.ResultStatusExclusionKept : TextKey.ResultStatusExclusion,
                (TextArg.Side, exclusion.Side.ToString()), (TextArg.Path, exclusion.Path)));
        return 0;
    }

    private static async Task<int> IgnoresListAsync(CliInvocation invocation, Run.Host host, TextWriter output, CancellationToken ct)
    {
        var pairId = invocation.Value(OptionKeys.Pair);
        if (pairId is null) return Cli.UsageExitCode;
        var pair = await FindPairAsync(host, pairId, ct).ConfigureAwait(false);
        if (pair is null)
        {
            output.WriteLine(Text.Get(TextKey.PairUnknown, (TextArg.Pair, pairId)));
            return Cli.UsageExitCode;
        }
        output.WriteLine(RenderIgnores(pairId, pair.IgnoreRules));
        return 0;
    }

    private static async Task<int> IgnoresSetAsync(CliInvocation invocation, Run.Host host, CancellationToken ct)
    {
        var pairId = invocation.Value(OptionKeys.Pair);
        var spec = invocation.Value(OptionKeys.Rules);
        if (pairId is null || spec is null) return Cli.UsageExitCode;
        IReadOnlyList<string> rules;
        try
        {
            rules = IgnoreRule.Normalize(IgnoreRule.Parse(spec)).Select(IgnoreRule.Format).ToArray();
        }
        catch (InvalidDataException error)
        {
            throw new EngineRequestException(TextKey.IgnoreRulesInvalid, (TextArg.Message, error.Message));
        }
        await host.SetIgnoresAsync(pairId, rules, ct).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> StatusAsync(CliInvocation invocation, Run.Host host, TextWriter output, CancellationToken ct)
    {
        output.WriteLine(await host.StatusAsync(invocation.Value(OptionKeys.Pair), ct).ConfigureAwait(false));
        return 0;
    }

    private static async Task<int> RepairAsync(CliInvocation invocation, Run.Host host, CancellationToken ct)
    {
        var pairId = invocation.Value(OptionKeys.Pair);
        if (pairId is null) return Cli.UsageExitCode;
        await host.RepairAsync(pairId, ct).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> NoticesAsync(CliInvocation invocation, Run.Host host, TextWriter output, CancellationToken ct)
    {
        var pairId = invocation.Value(OptionKeys.Pair);
        if (pairId is null) return Cli.UsageExitCode;
        var includeIgnored = invocation.Flag(OptionKeys.Ignored);
        var notices = new List<Notice>();
        await foreach (var notice in host.NoticesAsync(pairId, ct))
            if (includeIgnored || !notice.Ignored) notices.Add(notice);
        output.WriteLine(RenderNotices(notices));
        return 0;
    }

    private static async Task<int> ResolveAsync(CliInvocation invocation, Run.Host host, TextWriter output, CancellationToken ct)    {
        var pairId = invocation.Value(OptionKeys.Pair);
        var issueText = invocation.Value(OptionKeys.Issue);
        var reviewed = invocation.Value(OptionKeys.Reviewed);
        var choiceText = invocation.Value(OptionKeys.Choice);
        if (pairId is null || issueText is null || reviewed is null || choiceText is null) return Cli.UsageExitCode;
        if (!long.TryParse(issueText, NumberStyles.None, CultureInfo.InvariantCulture, out var issue)) return Cli.UsageExitCode;

        var choice = ParseChoice(choiceText);
        try
        {
            await host.ResolveAsync(pairId, issue, reviewed, choice, invocation.Value(OptionKeys.Name), ct).ConfigureAwait(false);
        }
        catch (EngineRequestException error)
        {
            output.WriteLine(Text.Get(TextKey.ResultResolveFailed, (TextArg.Id, issueText), (TextArg.Reason, error.Message)));
            return TransientExitCode;
        }
        output.WriteLine(Text.Get(TextKey.ResultResolveDone, (TextArg.Id, issueText), (TextArg.Choice, choice.ToString())));
        return 0;
    }

    private static async Task<int> AccountsDisconnectAsync(CliInvocation invocation, Run.Host host, CancellationToken ct)
    {
        var account = invocation.Value(OptionKeys.Account);
        if (account is null) return Cli.UsageExitCode;
        await host.DisconnectAsync(account, ct).ConfigureAwait(false);
        return 0;
    }

    private static async Task<PairConfig?> FindPairAsync(Run.Host host, string pairId, CancellationToken ct)
    {
        await foreach (var pair in host.PairsAsync(ct))
            if (string.Equals(pair.PairId, pairId, StringComparison.Ordinal)) return pair;
        return null;
    }

    /// <summary>The ResultPairLine (ResultPairLineOff when switched off) of this pair.</summary>
    public static string RenderPair(PairConfig pair) =>
        Text.Get(pair.Enabled ? TextKey.ResultPairLine : TextKey.ResultPairLineOff,
            (TextArg.Pair, pair.PairId), (TextArg.Name, pair.Name), (TextArg.LocalRoot, pair.LocalRoot),
            (TextArg.DropboxRoot, pair.DropboxRoot), (TextArg.Account, pair.ConnectionId),
            (TextArg.FirstRun, pair.FirstRun.ToString()));

    /// <summary>A line per pair, or the no-pairs sentence.</summary>
    public static string RenderPairs(IReadOnlyList<PairConfig> pairs) =>
        pairs.Count == 0 ? Text.Get(TextKey.NoPairsYet) : string.Join(Environment.NewLine, pairs.Select(RenderPair));

    /// <summary>The applied exclusion sentence.</summary>
    public static string RenderExclusionApplied(string path) => Text.Get(TextKey.ResultExcludeApplied, (TextArg.Path, path));

    /// <summary>The blocked exclusion sentence.</summary>
    public static string RenderExclusionBlocked(string path) => Text.Get(TextKey.ResultExcludeBlocked, (TextArg.Path, path));

    /// <summary>The destructive-exclusion warning: the recovery destination together with the recovery period.</summary>
    public static string RenderExclusionPreview(string path, Side side, long copyFiles, long copyBytes,
        long removeFiles, long removeBytes, string recoveryPlace) => recoveryPlace;

    /// <summary>The removed-pair sentence.</summary>
    public static string RenderPairRemoved(string pairId) => Text.Get(TextKey.ResultPairRemoved, (TextArg.Pair, pairId));

    /// <summary>The refused pair-removal sentence, carrying the returned reason unchanged.</summary>
    public static string RenderPairNotRemoved(string pairId, TextKey cause) =>
        Text.Get(TextKey.ResultPairNotRemoved, (TextArg.Pair, pairId), (TextArg.Reason, Text.Get(cause)));

    /// <summary>The pair's ignore-rule listing.</summary>
    public static string RenderIgnores(string pairId, IReadOnlyList<string> rules)
    {
        var lines = new List<string> { Text.Get(TextKey.ResultIgnoreRulesHeader, (TextArg.Pair, pairId)) };
        lines.AddRange(rules.Select(rule => Text.Get(TextKey.ResultIgnoreRuleLine, (TextArg.Value, rule))));
        lines.Add(Text.Get(TextKey.ResultIgnoreRulesDefaults));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Open notices grouped by kind and cause, each followed by its issue lines.</summary>
    public static string RenderNotices(IReadOnlyList<Notice> notices)
    {
        var lines = new List<string>();
        foreach (var group in notices.GroupBy(notice => (notice.Kind, notice.Cause))
                     .OrderBy(group => group.Key.Kind).ThenBy(group => group.Key.Cause.ToString(), StringComparer.Ordinal))
        {
            lines.Add(Text.Get(TextKey.ResultIssueGroup, (TextArg.Kind, group.Key.Kind.ToString()),
                (TextArg.Cause, group.Key.Cause.ToString()),
                (TextArg.Count, group.Count().ToString(CultureInfo.InvariantCulture))));
            foreach (var notice in group.OrderBy(item => item.Id))
                lines.Add(Text.Get(TextKey.ResultIssueLine, (TextArg.Id, notice.Id.ToString(CultureInfo.InvariantCulture)),
                    (TextArg.Path, notice.Path), (TextArg.At, notice.At.ToString(CultureInfo.InvariantCulture))));
        }
        return string.Join(Environment.NewLine, lines);
    }

    internal static FirstRunPolicy ParseFirstRun(string value)
    {
        foreach (var pair in Cli.Definitions.FirstRunPolicies)
            if (string.Equals(pair.Value, value, StringComparison.Ordinal))
                return Enum.Parse<FirstRunPolicy>(pair.Key, ignoreCase: true);
        return FirstRunPolicy.NewestWins;
    }

    internal static ResolutionChoice ParseChoice(string value)
    {
        var name = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return Enum.Parse<ResolutionChoice>(name, ignoreCase: true);
    }

    internal static bool TryParseSide(string value, out Side side) => Enum.TryParse(value, ignoreCase: true, out side);
}
