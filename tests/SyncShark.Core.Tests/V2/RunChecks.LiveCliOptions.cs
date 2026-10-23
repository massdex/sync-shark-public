namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task LiveCliOptionsAsync(string root)
    {
        foreach (var maximum in new[] { 0, 100 })
        {
            var folder = Path.Combine(root, "live-options-" + maximum);
            Directory.CreateDirectory(folder);
            var product = Path.Combine(folder, "product");
            var export = Path.Combine(folder, "effects.csv");
            using var store = SqliteStateStore.Open(product, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("original.txt", "original");
            await FirstSyncAsync(fx.Run(), fx);
            fx.A.Seed("incoming.txt", "new bytes");
            var effectsBefore = fx.WriterA.Calls + fx.WriterB.Calls;
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await SyncCli.ExecuteAsync(["--cli", "run", "--pair", fx.Pair.PairId,
                "--yes", "--max-planned", maximum.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--export", export, "--json"], output, error,
                () => new V2CliRuntime(null, store, fx.Run(), new EndpointRootProbe(fx.Provider), product));
            Require(exit == (maximum == 0 ? 9 : 0), "real CLI maps the engine admission result to the distinct exit");
            Require(File.Exists(export), "real CLI forwards live export into the engine writer");
            if (maximum == 0)
            {
                Require(fx.WriterA.Calls + fx.WriterB.Calls == effectsBefore,
                    "zero bound admits no fresh endpoint effects");
                Require(output.ToString().Contains("plan_bound_exceeded", StringComparison.Ordinal),
                    "CLI bound failure carries the explicit outcome");
                using var reopened = SqliteStateStore.Open(product, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
                Require(reopened.OpenNotices(fx.Pair.PairId).Count == 1,
                    "zero-bound CLI persists one review notice across store reopen");
            }
            else
            {
                Require((await fx.B.ObserveAsync("incoming.txt", default)) is not null,
                    "admitted CLI run commits the new file");
                Require(File.ReadLines(export).Skip(1).Any(), "live CSV contains committed effects, not only its header");
            }
        }
    }
}
