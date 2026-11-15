using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>The one pending port owns the mutation gate (C-RUN-04).</summary>
public static class PendingNotesChecks
{
    public static void Run()
    {
        var store = new InMemoryStateStore();
        var preview = new PendingNotes(store, "pending-port", 100, mutation: false);
        Check.Require(preview.Read() is NoPending,
            "a non-mutating port still reads the pair's pending note");
        Refused(() => preview.Arm(null!), "a non-mutating port refuses to arm a note");
        Refused(() => preview.Advance(null!, null!), "a non-mutating port refuses to advance a note");
        Refused(() => preview.AdvanceMember(null!, null!), "a non-mutating port refuses to settle a member");
        var mutating = new PendingNotes(store, "pending-port", 100, mutation: true);
        Check.Require(mutating.Read() is NoPending, "a mutating port reads the same pair");
    }

    private static void Refused(Action action, string what)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(what);
    }
}
