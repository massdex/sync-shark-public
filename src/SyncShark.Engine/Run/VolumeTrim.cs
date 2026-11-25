namespace Massdex.SyncShark.Engine.Run;

/// <summary>One volume's startup trim, shared by its members. A pending bind never reports an attempt.</summary>
internal sealed class VolumeTrim
{
    private readonly HashSet<string> pending=new(StringComparer.Ordinal);
    internal DriveType Type;
    internal Task Work=Task.CompletedTask;
    private VolumeTrim() { }
    public static Dictionary<string,VolumeTrim> Register(IEnumerable<PairConfig> pairs)
    {
        var byPair=new Dictionary<string,VolumeTrim>(StringComparer.Ordinal);
        foreach(var volume in pairs.Where(p=>p.Enabled).GroupBy(p=>Path.GetPathRoot(p.LocalRoot)!,PathNames.Comparer))
        {
            var group=new VolumeTrim();
            foreach(var p in volume) {group.pending.Add(p.PairId); byPair.Add(p.PairId,group);}
        }
        return byPair;
    }
    public bool Attempted(string pairId)
    {
        lock(pending)
        {
            return pending.Remove(pairId) && pending.Count==0;
        }
    }
}
