using System.Text.Json;

namespace Massdex.SyncShark;

internal sealed record SyncConsentTerminalRange(int First, int Last, string State);

// This nonserialized layout accompanies only the surviving original choices.
// A missing path never becomes a new choice; each surviving position still
// maps to the original bitmap ordinal.
internal sealed record SyncConsentTerminalLayout(int OriginalCount, int CompletedPrefix,
    IReadOnlyList<SyncConsentTerminalRange> Ranges, IReadOnlyList<int> OriginalOrdinals)
{
    internal IReadOnlyList<SyncAuthorityConsentItemLifecycle> SurvivingStates { get; init; } = [];

    internal long RetiredCount => checked((long)CompletedPrefix + 1 +
        Ranges.Sum(r => checked((long)r.Last - r.First + 1)));

    internal static SyncConsentTerminalLayout Read(int count, int prefix, string json, long retiredCount)
    {
        var ranges = JsonSerializer.Deserialize<SyncConsentTerminalRange[]>(json)
            ?? throw new InvalidDataException("The original consent terminal ranges are unreadable.");
        if (count < 0 || prefix < -1 || prefix >= count)
            throw new InvalidDataException("The original consent completion prefix is invalid.");
        var prior = prefix;
        foreach (var r in ranges)
        {
            if (r.First <= prior || r.Last < r.First || r.Last >= count ||
                r.State is not ("completed" or "expired"))
                throw new InvalidDataException("The original consent terminal ranges overlap or exceed its count.");
            prior = r.Last;
        }
        var layout = new SyncConsentTerminalLayout(count, prefix, ranges, []);
        if (layout.RetiredCount != retiredCount)
            throw new InvalidDataException("The original consent terminal count contradicts its exact ranges.");
        return layout;
    }

    internal bool IsRetired(int ordinal) => ordinal >= 0 && ordinal <= CompletedPrefix ||
        Ranges.Any(r => ordinal >= r.First && ordinal <= r.Last);

    internal int NextRetainedOrdinal(int ordinal)
    {
        var next = Math.Max(ordinal, checked(CompletedPrefix + 1));
        foreach (var r in Ranges)
        {
            if (next < r.First) break;
            if (next <= r.Last) next = checked(r.Last + 1);
        }
        return next;
    }

    internal SyncConsentTerminalLayout Add(int ordinal, string state)
    {
        if (ordinal < 0 || ordinal >= OriginalCount || IsRetired(ordinal) ||
            state is not ("completed" or "expired"))
            throw new InvalidDataException("The original consent ordinal cannot be retired twice or with an unknown outcome.");
        var merged = new List<SyncConsentTerminalRange>();
        foreach (var r in Ranges.Append(new(ordinal, ordinal, state)).OrderBy(r => r.First))
        {
            if (merged.Count > 0 && merged[^1].State == r.State && merged[^1].Last + 1 == r.First)
                merged[^1] = merged[^1] with { Last = r.Last };
            else merged.Add(r);
        }
        var prefix = CompletedPrefix;
        while (merged.Count > 0 && merged[0].State == "completed" && merged[0].First == prefix + 1)
        { prefix = merged[0].Last; merged.RemoveAt(0); }
        return new(OriginalCount, prefix, merged, OriginalOrdinals);
    }
}
