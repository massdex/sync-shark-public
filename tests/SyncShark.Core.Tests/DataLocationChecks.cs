using Massdex.SyncShark;
using Microsoft.Win32;

internal static class DataLocationChecks
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "syncshark-locations-" + Guid.NewGuid().ToString("N"));
        var registry = @"Software\Massdex\SyncShark.Tests\" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root);
        using var isolation = SyncSharkRuntimePaths.OverrideProductRootForTest(root);
        Require(SyncDataLocation.ProductRoot == Path.GetFullPath(root),
            "the product root follows the installed test override without reading the real store");
        SyncDataLocation.RegistryPathForTest = registry;
        SyncDataLocation.DefaultRootForTest = Path.Combine(root, "default");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(registry);
            var chosen = Path.Combine(root, "chosen");
            ExpectFailure(() => SyncDataLocation.ReadProductRoot());
            Require(!Directory.Exists(SyncDataLocation.DefaultRootForTest), "absent setting creates no default folder");
            key.SetValue("DataLocation", chosen);
            ExpectFailure(() => SyncDataLocation.ReadProductRoot());
            Require(!Directory.Exists(chosen), "missing configured folder is not created");
            key.SetValue("DataLocation", "relative");
            ExpectFailure(() => SyncDataLocation.ReadProductRoot());
            key.SetValue("DataLocation", 7, RegistryValueKind.DWord);
            try { SyncDataLocation.ReadProductRoot(); throw new InvalidOperationException("Expected refusal"); }
            catch (InvalidDataException error)
            { Require(error.Message.Contains("Reconnect or restore", StringComparison.Ordinal), "wrong registry kind has actionable instructions"); }
            key.DeleteValue("DataLocation");

            SyncDataLocation.InitializeNewStore(chosen, CreateStore);
            Require(SyncDataLocation.ReadProductRoot() == chosen, "explicit setup registers a valid store");
            ExpectFailure(() => SyncDataLocation.InitializeNewStore(Path.Combine(root, "other"), CreateStore));
            key.DeleteValue("DataLocation");
            ExpectFailure(() => SyncDataLocation.InitializeNewStore(chosen, CreateStore));

            var orphan = Path.Combine(root, "orphan");
            Directory.CreateDirectory(Path.GetDirectoryName(SyncDataLocation.EngineDatabasePath(orphan))!);
            File.WriteAllBytes(SyncDataLocation.EngineDatabasePath(orphan), []);
            SyncDataLocation.InitializeNewStore(orphan, CreateStore);
            Require(SyncDataLocation.StoreState(orphan) == SyncDataLocation.StoreValid, "an empty kill orphan is reclaimed");
            key.DeleteValue("DataLocation");

            Directory.CreateDirectory(SyncDataLocation.DefaultRootForTest);
            File.WriteAllText(SyncDataLocation.PointerPath, chosen + Environment.NewLine);
            SyncDataLocation.InitializeNewStore(Path.Combine(root, "other"), _ => throw new InvalidOperationException("Must adopt"));
            Require(SyncDataLocation.ReadProductRoot() == chosen, "legacy pointer imports existing store");
            Require((string?)key.GetValue("DataLocation") == chosen, "migration publishes registry");
            Require(File.Exists(SyncDataLocation.PointerPath), "migration retains legacy pointer");
            File.WriteAllText(SyncDataLocation.PointerPath, "invalid");
            Require(SyncDataLocation.ReadProductRoot() == chosen, "registry supersedes retained pointer");
            key.DeleteValue("DataLocation");
            ExpectFailure(() => SyncDataLocation.ReadProductRoot());
            var retry = Path.Combine(root, "retry");
            ExpectFailure(() => SyncDataLocation.InitializeNewStore(retry, location =>
            {
                CreateStore(location);
                throw new InvalidDataException("injected initialization failure");
            }));
            Require(!File.Exists(SyncDataLocation.EngineDatabasePath(retry)), "failed initialization removes owned database");
            ExpectFailure<ArgumentException>(() => SyncDataLocation.InitializeNewStore(retry, location =>
            {
                CreateStore(location);
                SyncDataLocation.RegistryPathForTest = new string('a', 300);
            }));
            SyncDataLocation.RegistryPathForTest = registry;
            Require(!File.Exists(SyncDataLocation.EngineDatabasePath(retry)), "registry failure removes owned database");
            SyncDataLocation.InitializeNewStore(retry, CreateStore);
            Require(File.Exists(SyncDataLocation.PointerPath), "stale pointer retained after explicit setup retry");
            key.DeleteValue("DataLocation");
            File.Delete(SyncDataLocation.PointerPath);

            CreateStore(SyncDataLocation.DefaultRootForTest);
            Require(SyncDataLocation.ReadProductRoot() == SyncDataLocation.DefaultRootForTest, "existing default store adopted");
            var corrupt = Path.Combine(root, "corrupt");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(corrupt, "engine", "state.db"))!);
            File.WriteAllText(Path.Combine(corrupt, "engine", "state.db"), "not a database");
            key.SetValue("DataLocation", corrupt);
            ExpectFailure(() => SyncDataLocation.ReadProductRoot());
            Require((string?)key.GetValue("DataLocation") == corrupt, "invalid configured store never falls back");
            Require(SyncDataLocation.StoreState(SyncDataLocation.DefaultRootForTest) == SyncDataLocation.StoreValid, "a valid store is reported valid");
            Require(SyncDataLocation.StoreState(Path.Combine(root, "no-store")) == SyncDataLocation.StoreAbsent, "a missing store is reported absent");
            Require(SyncDataLocation.StoreState(corrupt) == SyncDataLocation.StoreInvalid, "an unusable store is reported invalid");
            await MoveAsync(root, key);
        }
        finally
        {
            SyncDataLocation.RegistryPathForTest = null;
            SyncDataLocation.DefaultRootForTest = null;
            Registry.CurrentUser.DeleteSubKeyTree(registry, throwOnMissingSubKey: false);
        }
    }

    private static async Task MoveAsync(string root, RegistryKey key)
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = Path.Combine(root, "move-source");
        var target = Path.Combine(root, "move-target");
        CreateStore(source);
        key.SetValue("DataLocation", source);
        File.WriteAllText(SyncDataLocation.PointerPath, source);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(source, "state", SyncSharkRuntimePaths.InstanceLockFileName));
        var broken = Path.Combine(source, "vNext", "state", "syncshark.db");
        Directory.CreateDirectory(Path.GetDirectoryName(broken)!);
        File.WriteAllText(broken, "invalid legacy store");
        try { await SyncDataLocation.MoveAsync(instance.StateAuthority, target); throw new InvalidOperationException("Move should fail"); }
        catch (Exception error) when (error is InvalidDataException or Microsoft.Data.Sqlite.SqliteException) { }
        Require((string?)key.GetValue("DataLocation") == source && !Directory.Exists(target), "failed verification preserves registration");
        File.Delete(broken);
        var restore = SyncDataLocation.RegistryPathForTest;
        SyncDataLocation.RegistryPathForTest = new string('a', 300);
        try { await SyncDataLocation.MoveAsync(instance.StateAuthority, target); throw new InvalidOperationException("Expected failure"); }
        catch (ArgumentException) { }
        finally { SyncDataLocation.RegistryPathForTest = restore; }
        Require((string?)key.GetValue("DataLocation") == source && !Directory.Exists(target) && !File.Exists(Path.Combine(source, "MOVED-TO.txt")), "move rollback preserves registry and removes owned copy/marker");
        var result = await SyncDataLocation.MoveAsync(instance.StateAuthority, target);
        Require(result.RestartRequired && (string?)key.GetValue("DataLocation") == target, "move publishes registry");
        Require(File.ReadAllBytes(SyncDataLocation.EngineDatabasePath(target)).SequenceEqual(File.ReadAllBytes(SyncDataLocation.EngineDatabasePath(source))), "published destination contains verified bytes");
        Require(File.ReadAllText(Path.Combine(source, "MOVED-TO.txt")) == target, "publication retains durable marker");
        Require(File.ReadAllText(SyncDataLocation.PointerPath) == source, "move retains legacy pointer");
    }

    private static void CreateStore(string root)
    {
        var path = SyncDataLocation.EngineDatabasePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, "SQLite format 3\0"u8.ToArray());
    }

    private static void ExpectFailure(Action action) => ExpectFailure<InvalidDataException>(action);

    private static void ExpectFailure<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException("Expected data-location refusal.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
