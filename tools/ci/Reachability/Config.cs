internal sealed record Config(Config.Cut[] Cuts, Dictionary<string, string> InteropFiles, Config.Sentinel[] Sentinels, Config.EdgeCheck[] EdgeChecks, Dictionary<string, string[]> TextKeepExclusions)
{
    internal sealed record Cut(string Route, string Path, int First, int Last, string Hash, bool Gui);
    internal sealed record Sentinel(string Type, string Profile);
    internal sealed record EdgeCheck(string Route, string Target);
}
