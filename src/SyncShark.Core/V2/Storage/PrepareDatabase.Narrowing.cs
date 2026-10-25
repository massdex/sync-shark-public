using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal sealed partial class PrepareDatabase
{
    public void Narrow(PairDef previous, PairDef updated, string oldScope, string newScope,
        Func<Prepared, PrepareNarrowing> prepare)
    {
        if (_store.ReadPrepare(_header.PairId) != _header)
            throw new PreparePublicationException("The pair or its prepare changed during narrowing.");
        var small = JsonSerializer.Deserialize<Prepared>(_header.MetadataJson, Json)
            ?? throw new JsonException("Missing prepare metadata.");
        if (!ValidSmall(small) || small.Digest != _header.Digest)
            throw new JsonException("Invalid prepare metadata.");
        var narrowing = prepare(small);
        _store.SavePairNarrowed(previous, updated, _header, narrowing.Removed, narrowing.ScopeRemoved,
            oldScope, newScope, narrowing.Digest, narrowing.Metadata);
    }
}
