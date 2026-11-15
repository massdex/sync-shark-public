using Massdex.SyncShark.Engine.Local;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks for StageNames's fixed reserved grammar (`--engine-checks local`), written by StageNames's writer.</summary>
public static class StageNamesChecks
{
    private const string Prefix = ".syncshark-";
    private const string Suffix = ".stage";
    private const string Hex = "0123456789abcdef0123456789abcdef";

    public static void Run()
    {
        var target = Path.Combine("Parent", "Child", "résumé file.txt");
        var stage = StageNames.For(target);
        var stageName = Path.GetFileName(stage);
        Check.Require(Path.GetDirectoryName(stage) == Path.GetDirectoryName(target),
            "Stage must be beside its target.");
        Check.Require(stageName.Length == Prefix.Length + ResourceNames.IdLength + Suffix.Length,
            "Stage name must be exactly the discriminator, one id and the suffix.");
        Check.Require(stageName.StartsWith(Prefix, StringComparison.Ordinal) &&
            stageName.EndsWith(Suffix, StringComparison.Ordinal),
            "Stage name must use the exact ordinal discriminator and suffix.");
        Check.Require(Guid.TryParseExact(stageName.AsSpan(Prefix.Length, ResourceNames.IdLength),
                ResourceNames.IdFormat, out var id) && id != Guid.Empty,
            "Stage name must use a fresh id in the resource format.");
        Check.Require(StageNames.IsStage(stageName), "Generated stage names must be recognized.");
        Check.Require(stage != StageNames.For(target), "Repeated stage creation must produce distinct names.");
        Check.Require(!stageName.Contains(Path.GetFileNameWithoutExtension(target), StringComparison.Ordinal),
            "Stage name must not carry the target basename.");

        var stageShapedTarget = Path.Combine("Parent", Prefix + Hex + Suffix);
        var freshName = Path.GetFileName(StageNames.For(stageShapedTarget));
        Check.Require(StageNames.IsStage(freshName) && freshName != Prefix + Hex + Suffix,
            "A stage-shaped target must still get a fresh fixed reserved name.");

        var invalid = new[]
        {
            "",
            Suffix,
            Prefix,
            Prefix + Suffix,
            Prefix + Hex[..^1] + Suffix,
            Prefix + Hex + "0" + Suffix,
            Prefix + "g" + Hex[1..] + Suffix,
            Prefix + Hex[..^1] + "g" + Suffix,
            Prefix + "０" + Hex[1..] + Suffix,
            Prefix + " " + Hex[1..] + Suffix,
            Prefix + Hex[..^1] + "\n" + Suffix,
            Prefix + "01234567-89ab-cdef-0123-456789abcdef" + Suffix,
            Prefix + "{" + Hex + "}" + Suffix,
            Hex + Suffix,
            "syncshark-" + Hex + Suffix,
            "." + Hex + Suffix,
            "file." + Hex + Suffix,
            "report.txt" + Prefix + Hex + Suffix,
            Prefix + Hex,
            Prefix + Hex + ".stagexx",
            Prefix + Hex + Suffix + ".txt",
            Prefix + Hex + Suffix.ToUpperInvariant(),
            Prefix.ToUpperInvariant() + Hex + Suffix,
            Prefix + Hex + Suffix + "\n",
            "report.txt." + Hex + ".syncshark-stage"
        };
        foreach (var name in invalid)
            Check.Require(!StageNames.IsStage(name), $"Not a reserved stage name: {name}.");

        Check.Require(StageNames.IsStage(Prefix + Hex.ToUpperInvariant() + Suffix),
            "An id in the resource format may use uppercase hex digits.");

        var maxName = new string('n', 255);
        var maxStageName = Path.GetFileName(StageNames.For(Path.Combine("Parent", maxName)));
        Check.Require(maxStageName.Length == Prefix.Length + ResourceNames.IdLength + Suffix.Length &&
            StageNames.IsStage(maxStageName),
            "A maximum-length target basename must not change the fixed reserved name.");

        var surrogateName = new string('n', 253) + "\U0001F600";
        var surrogateStageName = Path.GetFileName(StageNames.For(Path.Combine("Parent", surrogateName)));
        Check.Require(surrogateStageName.Length == Prefix.Length + ResourceNames.IdLength + Suffix.Length &&
            StageNames.IsStage(surrogateStageName),
            "A supplementary character in the target basename must not affect the fixed reserved name.");
    }
}
