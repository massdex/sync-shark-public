using System.Globalization;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>Commands operations for the edge module. Translates the registered command to its Host operation and
/// renders the returned facts; the recovery place in the exclusion preview and a refused pair removal's reason are
/// rendered from the returned values without probing the drive or looking the pair up again.</summary>
public sealed class Commands
{
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
                case CommandKeys.HostStart: await host.StartAsync(ct).ConfigureAwait(false); return Cli.SuccessExitCode;
                case CommandKeys.HostStop: await host.StopAsync(ct).ConfigureAwait(false); return Cli.SuccessExitCode;
                case CommandKeys.Status: return await StatusAsync(invocation, host, output, ct).ConfigureAwait(false);
                case CommandKeys.Pause: await host.PauseAsync(invocation.Value(OptionKeys.Pair), ct).ConfigureAwait(false); return Cli.SuccessExitCode;
                case CommandKeys.Resume: await host.ResumeAsync(invocation.Value(OptionKeys.Pair), ct).ConfigureAwait(false); return Cli.SuccessExitCode;
                case CommandKeys.Repair: return await RepairAsync(invocation, host, ct).ConfigureAwait(false);
                case CommandKeys.Notices: return await NoticesAsync(invocation, host, output, ct).ConfigureAwait(false);
                case CommandKeys.Resolve: return await ResolveAsync(invocation, host, output, ct).ConfigureAwait(false);
                case CommandKeys.AccountsConnect: await host.ConnectAsync(ct).ConfigureAwait(false); return Cli.SuccessExitCode;
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
        return Cli.SuccessExitCode;
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
        return Cli.SuccessExitCode;
    }

    private static async Task<int> PairsRemoveAsync(CliInvocation invocation, Run.Host host, TextWriter output, CancellationToken ct)
    {
        var pairId = invocation.Value(OptionKeys.Pair);
        if (pairId is null) return Cli.UsageExitCode;
        var cause = await host.RemovePairAsync(pairId, ct).ConfigureAwait(false);
        output.WriteLine(cause is { } refused ? RenderPairNotRemoved(pairId, refused) : RenderPairRemoved(pairId));
        return Cli.SuccessExitCode;
    }

    private static async Task<int> PairsBindAsync(CliInvocation invocation, Run.Host host, TextWriter output, CancellationToken ct)
    {
        var pairId = invocation.Value(OptionKeys.Pair);
        if (pairId is null) return Cli.UsageExitCode;
        await host.BindAsync(pairId, ct).ConfigureAwait(false);
        output.WriteLine(Text.Get(TextKey.ResultRootsBound, (TextArg.Pair, pairId)));
        return Cli.SuccessExitCode;
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

        if (result.Applied) output.WriteLine(RenderExclusionApplied(rel));
        else if (result.Cause is { } cause) output.WriteLine(Text.Get(cause, (TextArg.Path, rel)));
        else if (confirmed) output.WriteLine(RenderExclusionApplied(rel));
        else output.WriteLine(RenderExclusionPreview(rel, side, result.CopyFiles, result.CopyBytes,
                result.RemoveFiles, result.RemoveBytes, result.RecoveryPlace ?? string.Empty));
        return Cli.SuccessExitCode;
    }

    private static async Task<int> PairsIncludeAsync(CliInvocation invocation, Run.Host host, CancellationToken ct)
    {
        var pairId = invocation.Value(OptionKeys.Pair);
        var sideText = invocation.Value(OptionKeys.Side);
        var rel = invocation.Value(OptionKeys.Rel);
        if (pairId is null || sideText is null || rel is null || !TryParseSide(sideText, out var side)) return Cli.UsageExitCode;
        await host.SetExclusionAsync(pairId, new Exclusion(side, rel, Keep: false), include: true, confirmed: true, ct).ConfigureAwait(false);
        return Cli.SuccessExitCode;
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
        return Cli.SuccessExitCode;
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
        return Cli.SuccessExitCode;
    }

    private static async Task<int> IgnoresSetAsync(CliInvocation invocation, Run.Host host, CancellationToken ct)
    {
        var pairId = invocation.Value(OptionKeys.Pair);
        var spec = invocation.Value(OptionKeys.Rules);
        if (pairId is null || spec is null) return Cli.UsageExitCode;
        IReadOnlyList<string> rules;
        try
        {
            rules = IgnoreRule.ParseForEdit(spec).Select(IgnoreRule.Format).ToArray();
        }
        catch (InvalidDataException error)
        {
            throw new EngineRequestException(TextKey.IgnoreRulesInvalid, (TextArg.Message, error.Message));
        }
        await host.SetIgnoresAsync(pairId, rules, ct).ConfigureAwait(false);
        return Cli.SuccessExitCode;
    }

    private static async Task<int> StatusAsync(CliInvocation invocation, Run.Host host, TextWriter output, CancellationToken ct)
    {
        var status = await host.StatusAsync(invocation.Value(OptionKeys.Pair), ct).ConfigureAwait(false);
        output.WriteLine(status);
        return Cli.SuccessExitCode;
    }

    private static async Task<int> RepairAsync(CliInvocation invocation, Run.Host host, CancellationToken ct)
    {
        var pairId = invocation.Value(OptionKeys.Pair);
        if (pairId is null) return Cli.UsageExitCode;
        await host.RepairAsync(pairId, ct).ConfigureAwait(false);
        return Cli.SuccessExitCode;
    }

    private static async Task<int> NoticesAsync(CliInvocation invocation, Run.Host host, TextWriter output, CancellationToken ct)
    {
        var pairId = invocation.Value(OptionKeys.Pair);
        if (pairId is null) return Cli.UsageExitCode;
        var includeIgnored = invocation.Flag(OptionKeys.Ignored);
        await foreach (var notice in host.NoticesAsync(pairId, ct))
        {
            if (!includeIgnored && notice.Ignored) continue;
            var choices = (notice.Choices ?? Array.Empty<ResolutionChoice>()).Select(Cli.ChoiceWord).ToArray();
            output.WriteLine(Render.Notice(notice, pairId, choices));
        }
        return Cli.SuccessExitCode;
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
            return Cli.TransientExitCode;
        }
        output.WriteLine(Text.Get(TextKey.ResultResolveDone, (TextArg.Id, issueText), (TextArg.Choice, choice.ToString())));
        return Cli.SuccessExitCode;
    }

    private static async Task<int> AccountsDisconnectAsync(CliInvocation invocation, Run.Host host, CancellationToken ct)
    {
        var account = invocation.Value(OptionKeys.Account);
        if (account is null) return Cli.UsageExitCode;
        await host.DisconnectAsync(account, ct).ConfigureAwait(false);
        return Cli.SuccessExitCode;
    }

    /// <summary>The detached logs read (R19): Edge.Commands forwards to RunLog.Read and never starts the host.</summary>
    public static int ReadLogs(CliInvocation invocation, ILogFiles files, TextWriter output, CancellationToken ct)
    {
        var pair = invocation.Value(OptionKeys.Pair);
        var run = invocation.Value(OptionKeys.Run);
        if (pair is null || run is null) return Cli.UsageExitCode;

        var folder = ResourceNames.LogDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var path = files.Resolve(folder, pair, run);
        if (path is null)
        {
            WriteLogMissing(output, pair, run);
            return Cli.SuccessExitCode;
        }

        var showFiles = invocation.Flag(OptionKeys.ShowFiles);
        var status = Run.RunLog.Read(files, path,
            (line, _) => { if (ShowLogRecord(line, showFiles)) output.WriteLine(line); },
            SettingsParser.Load().LogReadBufferBytes, ct, out _);
        if (status == ILogFiles.ReadStatus.Found) return Cli.SuccessExitCode;
        if (status == ILogFiles.ReadStatus.NotFound) { WriteLogMissing(output, pair, run); return Cli.SuccessExitCode; }
        output.WriteLine(Text.Get(TextKey.HumanLogUnreadable));
        return Cli.SuccessExitCode;
    }

    /// <summary>Per-file (Effect) records print only with --show-files; all other records always print.</summary>
    public static bool ShowLogRecord(string line, bool showFiles)
    {
        if (showFiles) return true;
        // Record grammar (LogText.Format, W22): timestamp<TAB>kind<TAB>what<TAB>path<TAB>detail<TAB>outcome.
        var firstTab = line.IndexOf('	');
        if (firstTab < 0) return true;
        var start = firstTab + 1;
        var kindEnd = line.IndexOf('	', start);
        var kind = kindEnd < 0 ? line[start..] : line[start..kindEnd];
        return !string.Equals(kind, RunEventKind.Effect.ToString(), StringComparison.Ordinal);
    }

    private static void WriteLogMissing(TextWriter output, string pair, string run) =>
        output.WriteLine(Text.Get(TextKey.HumanLogNotFound, (TextArg.Pair, pair), (TextArg.Run, run)));

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
        pairs.Count == default ? Text.Get(TextKey.NoPairsYet) : string.Join(Environment.NewLine, pairs.Select(RenderPair));

    /// <summary>The applied exclusion sentence.</summary>
    public static string RenderExclusionApplied(string path) => Text.Get(TextKey.ResultExcludeApplied, (TextArg.Path, path));

    /// <summary>The blocked exclusion sentence.</summary>
    public static string RenderExclusionBlocked(string path) => Text.Get(TextKey.ResultExcludeBlocked, (TextArg.Path, path));

    /// <summary>The destructive-exclusion warning: the recovery destination together with the recovery period.</summary>
    public static string RenderExclusionPreview(string path, Side side, long copyFiles, long copyBytes,
        long removeFiles, long removeBytes, string recoveryPlace) =>
        Text.Get(TextKey.ResultExcludePreview, (TextArg.Path, path),
            (TextArg.CopyFiles, copyFiles.ToString(CultureInfo.InvariantCulture)),
            (TextArg.CopyBytes, copyBytes.ToString(CultureInfo.InvariantCulture)),
            (TextArg.RemoveFiles, removeFiles.ToString(CultureInfo.InvariantCulture)),
            (TextArg.RemoveBytes, removeBytes.ToString(CultureInfo.InvariantCulture)),
            (TextArg.Side, side.ToString()), (TextArg.Place, recoveryPlace));

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
