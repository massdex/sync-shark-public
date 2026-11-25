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
            return await DispatchAsync(invocation, host, output, error, ct).ConfigureAwait(false);
        }
        catch (EngineRequestException refusal)
        {
            error.WriteLine(refusal.Message);
            return Cli.UsageExitCode;
        }
    }

    private static async Task<int> DispatchAsync(CliInvocation invocation, Run.Host host, TextWriter output, TextWriter error, CancellationToken ct)
    {
        switch (invocation.CommandKey)
        {
            case CommandKeys.PairsList: return await PairsListAsync(host, output, ct).ConfigureAwait(false);
            case CommandKeys.PairsAdd: return await PairsAddAsync(invocation, host, output, ct).ConfigureAwait(false);
            case CommandKeys.PairsRemove: return await PairsRemoveAsync(invocation, host, output, ct).ConfigureAwait(false);
            case CommandKeys.PairsBind: return await PairsBindAsync(invocation, host, output, ct).ConfigureAwait(false);
            case CommandKeys.PairsExclude: return await PairsExcludeAsync(invocation, host, output, error, ct).ConfigureAwait(false);
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
            case CommandKeys.Resolve: return await ResolveAsync(invocation, host, output, error, ct).ConfigureAwait(false);
            case CommandKeys.AccountsConnect: output.WriteLine(await host.ConnectAsync(ct).ConfigureAwait(false)); return Cli.SuccessExitCode;
            case CommandKeys.AccountsDisconnect: return await AccountsDisconnectAsync(invocation, host, ct).ConfigureAwait(false);
            default: return Cli.UsageExitCode;
        }
    }

    private static async Task<int> PairsListAsync(Run.Host host, TextWriter output, CancellationToken ct)
    {
        var pairs = new List<PairConfig>();
        await foreach (var pair in host.PairsAsync(ct)) pairs.Add(pair);
        output.WriteLine(CommandText.RenderPairs(pairs));
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
        output.WriteLine(CommandText.RenderPair(pair));
        return Cli.SuccessExitCode;
    }

    private static async Task<int> PairsRemoveAsync(CliInvocation invocation, Run.Host host, TextWriter output, CancellationToken ct)
    {
        var pairId = invocation.Value(OptionKeys.Pair);
        if (pairId is null) return Cli.UsageExitCode;
        var cause = await host.RemovePairAsync(pairId, ct).ConfigureAwait(false);
        output.WriteLine(cause is { } refused ? CommandText.RenderPairNotRemoved(pairId, refused) : CommandText.RenderPairRemoved(pairId));
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

    private static async Task<int> PairsExcludeAsync(CliInvocation invocation, Run.Host host, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var pairId = invocation.Value(OptionKeys.Pair);
        var sideText = invocation.Value(OptionKeys.Side);
        var rel = invocation.Value(OptionKeys.Rel);
        if (pairId is null || sideText is null || rel is null || !TryParseSide(sideText, out var side)) return Cli.UsageExitCode;

        var exclusion = new Exclusion(side, rel, invocation.Flag(OptionKeys.Keep));
        var confirmed = invocation.Flag(OptionKeys.Yes);
        var result = await host.SetExclusionAsync(pairId, exclusion, include: false, confirmed, ct).ConfigureAwait(false);

        if (result.Outcome == ExclusionOutcome.Applied) output.WriteLine(CommandText.RenderExclusionApplied(rel));
        else if (result.Outcome is { } outcome)
        {
            error.WriteLine(result.Cause is { } cause ? Text.Get(cause, (TextArg.Path, rel)) :
                outcome == ExclusionOutcome.Pending ? Text.Get(TextKey.WaitItemRetry) : outcome.ToString());
            return Cli.TransientExitCode;
        }
        else if (result.Cause is { } previewCause) { error.WriteLine(Text.Get(previewCause, (TextArg.Path, rel))); return Cli.TransientExitCode; }
        else output.WriteLine(CommandText.RenderExclusionPreview(rel, side, result.CopyFiles, result.CopyBytes,
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
        output.WriteLine(CommandText.RenderIgnores(pairId, pair.IgnoreRules));
        return Cli.SuccessExitCode;
    }

    private static async Task<int> IgnoresSetAsync(CliInvocation invocation, Run.Host host, CancellationToken ct)
    {
        var pairId = invocation.Value(OptionKeys.Pair);
        var spec = invocation.Value(OptionKeys.Rules);
        if (pairId is null || spec is null) return Cli.UsageExitCode;
        try
        {
            await host.SetIgnoresAsync(pairId, [spec], ct).ConfigureAwait(false);
        }
        catch (InvalidDataException error)
        {
            throw new EngineRequestException(TextKey.IgnoreRulesInvalid, (TextArg.Message, error.Message));
        }
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
        var pair = await FindPairAsync(host, pairId, ct).ConfigureAwait(false);
        await foreach (var notice in host.NoticesAsync(pairId, ct))
        {
            if (!includeIgnored && notice.Ignored) continue;
            var choices = (notice.Choices ?? Array.Empty<ResolutionChoice>()).Select(Cli.ChoiceWord).ToArray();
            output.WriteLine(Render.Notice(notice, pairId, choices, pair?.ConnectionId));
        }
        return Cli.SuccessExitCode;
    }

    private static async Task<int> ResolveAsync(CliInvocation invocation, Run.Host host, TextWriter output, TextWriter diagnostic, CancellationToken ct)
    {
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
            diagnostic.WriteLine(Text.Get(TextKey.ResultResolveFailed, (TextArg.Id, issueText), (TextArg.Reason, error.Message)));
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
            CommandText.WriteLogMissing(output, pair, run);
            return Cli.SuccessExitCode;
        }

        var showFiles = invocation.Flag(OptionKeys.ShowFiles);
        var status = Run.RunLog.Read(files, path,
            (line, _) => { if (CommandText.ShowLogRecord(line, showFiles)) output.WriteLine(line); },
            SettingsParser.Load().LogReadBufferBytes, ct, out _);
        if (status == ILogFiles.ReadStatus.Found) return Cli.SuccessExitCode;
        if (status == ILogFiles.ReadStatus.NotFound) { CommandText.WriteLogMissing(output, pair, run); return Cli.SuccessExitCode; }
        output.WriteLine(Text.Get(TextKey.HumanLogUnreadable));
        return Cli.SuccessExitCode;
    }

    private static async Task<PairConfig?> FindPairAsync(Run.Host host, string pairId, CancellationToken ct)
    {
        await foreach (var pair in host.PairsAsync(ct))
            if (string.Equals(pair.PairId, pairId, StringComparison.Ordinal)) return pair;
        return null;
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
        var entry = Cli.Definitions.Choices.First(pair => string.Equals(pair.Value, value, StringComparison.Ordinal));
        return Enum.Parse<ResolutionChoice>(entry.Key);
    }

    internal static bool TryParseSide(string value, out Side side) => Enum.TryParse(value, ignoreCase: true, out side);
}
