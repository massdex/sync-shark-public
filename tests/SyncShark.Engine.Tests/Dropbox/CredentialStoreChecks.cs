using System.Security.Cryptography;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for CredentialStore: the per-connection target naming, and a protected save-read-remove
/// round trip on Windows Credential Manager through the module surface.</summary>
public static class CredentialStoreChecks
{
    public static Task RunAsync()
    {
        var target = CredentialStore.AccountTarget("connection-" + new string('a', 24));
        Check.Require(target == "massdex/account-access/v1/dropbox/sync-shark-" + new string('a', 24) + "/production/refresh-token",
            "Account target is not the per-connection protected name");
        Check.Require(CredentialStore.AccountTarget("connection-" + new string('a', 24)) !=
                      CredentialStore.AccountTarget("connection-" + new string('b', 24)), "Two connections share a protected target");
        foreach (var invalid in new[] { "connection-" + new string('A', 24), "connection-" + new string('a', 23),
                     "connection-" + new string('a', 25), "nope", "connection-" + new string('g', 24), "" })
            Throws<InvalidDataException>(() => CredentialStore.AccountTarget(invalid));

        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        var connectionId = "connection-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var store = new CredentialStore();
        try
        {
            store.Save(connectionId, "refresh-fixture-one");
            Check.Require(store.Read(connectionId) == "refresh-fixture-one", "A saved token was not read from its own target");
            store.Save(connectionId, "refresh-fixture-two");
            Check.Require(store.Read(connectionId) == "refresh-fixture-two", "A re-saved token was not read");
        }
        finally { store.Remove(connectionId); }
        store.Remove(connectionId);
        Throws<ArgumentException>(() => store.Save(connectionId, " "));
        Throws<ArgumentOutOfRangeException>(() => store.Save(connectionId, new string('x', 3000)));
        Throws<InvalidDataException>(() => store.Read("not-a-connection-id"));
        return Task.CompletedTask;
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (Exception error) when (error is T) { return; }
        throw new InvalidDataException("Expected " + typeof(T).Name);
    }
}
