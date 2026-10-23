namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
    public Task<SyncCliIgnoreListData> LoadIgnoresAsync(string pairId, CancellationToken ct) =>
        Task.FromResult(ProjectIgnores(RequirePair(pairId)));

    public async Task<SyncCliIgnoreListData> SaveIgnoresAsync(string pairId, string rules, CancellationToken ct)
    {
        IReadOnlyList<SyncIgnoreRule> normalized;
        try
        {
            normalized = SyncIgnorePolicy.Normalize(rules.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)
                ? [] : SyncIgnorePolicy.Parse(rules));
        }
        catch (InvalidDataException error)
        {
            throw new SyncCliUsageException(error.Message);
        }
        // Like folder exclusions, this changes only the definition, serialized with prepare/apply.
        return await _run.WithPairAdmissionAsync(pairId, () =>
        {
            var pair = RequirePair(pairId);
            var stored = normalized.Select(rule => $"{rule.Kind}:{rule.Value}").ToArray();
            var prior = SyncIgnorePolicy.Normalize(PairInclude.IgnoreRules(pair))
                .Select(rule => $"{rule.Kind}:{rule.Value}").ToArray();
            IReadOnlyList<string> warnings = [];
            if (!stored.SequenceEqual(prior, StringComparer.OrdinalIgnoreCase))
            {
                var removedDesktopIni = HasDesktopIniRule(PairInclude.IgnoreRules(pair)) && !HasDesktopIniRule(normalized);
                var updated = pair with { IgnoreRules = stored };
                // An addition can only remove reviewed effects. Any removed/replaced rule may widen
                // coverage, so retain the existing fresh-prepare requirement for that case.
                if (prior.All(rule => stored.Contains(rule, StringComparer.OrdinalIgnoreCase)))
                    warnings = SaveNarrowerPair(pair, updated);
                else
                {
                    FirstSyncChoicesState.Clear(_productRoot, pairId, _store);
                    _store.SavePair(updated);
                }
                pair = updated;
                if (removedDesktopIni)
                    Console.Error.WriteLine("Warning: removing FileName:desktop.ini makes desktop.ini files eligible for sync and independent deletion propagation unless another ignore rule covers them. Deletions made while ignored can now propagate; Dropbox retains history and local targets receive Recovery copies first. The requested rules were saved.");
            }
            foreach (var warning in warnings) Console.Error.WriteLine(warning);
            return Task.FromResult(ProjectIgnores(RequirePair(pairId)) with { Warnings = warnings });
        }, ct, Console.Error.WriteLine);
    }

    private static bool HasDesktopIniRule(IReadOnlyList<SyncIgnoreRule> rules) => rules.Any(rule =>
        rule.Kind == SyncIgnoreKind.FileName && rule.Value.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase));

    private static SyncCliIgnoreListData ProjectIgnores(PairDef pair)
    {
        var rules = PairInclude.IgnoreRules(pair);
        return new(pair.PairId,
            rules.Select(rule => new SyncCliIgnoreRuleData(rule.Kind.ToString(), rule.Value)).ToArray(), rules.Count);
    }
}
