using System.Text.Json;
using Massdex.SyncShark;

// Decision 76 focused checks: the Online-only return interval is an ordinary transfer setting with a
// 1 day default, four accepted values, and durable storage. It changes no sync behaviour.
internal static class OnlineOnlyReturnSettingsChecks
{
    internal static async Task RunAsync(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Use a fresh online-only settings fixture.");
        Directory.CreateDirectory(root);

        Require(new SyncTransferSettings().OnlineOnlyReturnInterval == "1 day",
            "a new transfer settings record defaults the online-only return interval to 1 day");
        var legacy = JsonSerializer.Deserialize<SyncTransferSettings>("{\"MaximumConcurrentPairs\":3}")!;
        Require(legacy.MaximumConcurrentPairs == 3 && legacy.OnlineOnlyReturnInterval == "1 day",
            "a settings document written before decision 76 loads with the 1 day default");

        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var store = new SyncTransferSettingsStore(instance.StateAuthority);
        foreach (var interval in new[] { "1 hour", "1 day", "1 week", "Never" })
        {
            var value = new SyncTransferSettings(MaximumConcurrentPairs: 3, UploadKiBPerSecond: 120,
                DownloadKiBPerSecond: 480, OnlineOnlyReturnInterval: interval);
            await store.SaveAsync(value);
            var reloaded = new SyncTransferSettingsStore(instance.StateAuthority).Load();
            Require(reloaded == value && reloaded.OnlineOnlyReturnInterval == interval,
                "the " + interval + " online-only return interval survives a durable save and reload with the other settings");
        }

        foreach (var invalid in new[] { "", "never", "2 days", "1 day " })
        {
            var rejected = false;
            try
            {
                await store.SaveAsync(new SyncTransferSettings(OnlineOnlyReturnInterval: invalid));
            }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected && store.Load().OnlineOnlyReturnInterval == "Never",
                "the unsupported return interval '" + invalid + "' is rejected without replacing the saved value");
        }

        var retained = new SyncTransferSettingsStore(instance.StateAuthority).Load();
        Require(retained.MaximumConcurrentPairs == 3 && retained.UploadKiBPerSecond == 120 &&
                retained.DownloadKiBPerSecond == 480 && retained.OnlineOnlyReturnInterval == "Never",
            "the saved record still carries every other transfer setting unchanged");
        Console.WriteLine("PASS decision 76 online-only return interval settings roundtrip");
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidDataException(description);
        Console.WriteLine("PASS " + description);
    }
}
