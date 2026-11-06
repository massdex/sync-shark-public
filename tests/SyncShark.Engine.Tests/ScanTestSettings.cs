namespace Massdex.SyncShark.Engine.Tests;

/// <summary>The shipped scan save batch, encoded once for checks that open a store directly, so their work counts
/// match production. Checks that must cross a chunk boundary supply a smaller value.</summary>
internal static class ScanTestSettings
{
    public const int BatchRows = 20_000;
}
