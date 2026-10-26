using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal static partial class FirstSyncChoicesState
{
    internal static PrepareNarrowing PrepareNarrowing(Prepared small, PairDef previous, PairDef updated, string newScope)
    {
        var priorRules = SyncIgnorePolicy.Normalize(PairInclude.IgnoreRules(previous))
            .Select(rule => $"{rule.Kind}:{rule.Value}").ToArray();
        var addedRules = SyncIgnorePolicy.Normalize(PairInclude.IgnoreRules(updated))
            .Select(rule => $"{rule.Kind}:{rule.Value}").Except(priorRules, StringComparer.OrdinalIgnoreCase).ToArray();
        var addedExclusions = updated.Excluded.Where(path =>
            !previous.Excluded.Any(parent => PairPath.IsSelfOrUnder(path, parent))).ToArray();
        var rules = PairInclude.IgnoreRules(updated with { IgnoreRules = addedRules });
        bool ScopeRemoved(string path) => addedExclusions.Any(prefix => PairPath.IsSelfOrUnder(path, prefix));
        bool Removed(string path, bool folder) => ScopeRemoved(path) || SyncIgnorePolicy.IsIgnored(path, folder, rules);
        var narrowing = JsonSerializer.Serialize(new { Previous = small.Digest, Excluded = addedExclusions,
            IgnoreRules = addedRules, Scope = newScope }, Json);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(narrowing))).ToLowerInvariant();
        var winners = small.Paths.ToDictionary(entry => entry.Key, entry => entry.Value switch
        {
            "A" => (Side?)Side.A, "B" => Side.B, "none" => null,
            _ => throw new JsonException("Invalid saved winner.")
        }, StringComparer.Ordinal);
        var choices = new FirstSyncChoices.Parsed(winners,
            small.OneSided.Select(entry => new OneSidedChoice(entry.Prefix,
                entry.Side == "A" ? Side.A : Side.B, entry.Action == FirstSyncChoices.RemoveAction)).ToArray(),
            small.RebuildableChoices ?? []);
        var choicesDigest = small.NarrowedChoicesDigest ?? FirstSyncChoices.Digest(choices, []);
        var retained = small with
        {
            Digest = digest,
            NarrowedChoicesDigest = choicesDigest,
            NarrowedIgnoreRules = small.NarrowedIgnoreRules.Concat(addedRules).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            // Per-choice counts are ceilings, not authorization. Keep those original reviewed ceilings;
            // the filtered exact victim/held indexes are the authority for every guarded deletion.
            Rebuildables = small.Rebuildables.Where(folder => !Removed(folder.Path, true)).ToArray(),
            Warnings = (small.Warnings ?? []).Append("The saved review was narrowed by exclusions or ignore rules; protected paths are left untouched.").Distinct().ToArray(),
            InventoryCursors = [], InventoryScope = null
        };
        return new(digest, Removed, ScopeRemoved,
            count => JsonSerializer.Serialize(retained with { DeleteVictimCount = count }, Json));
    }
}
