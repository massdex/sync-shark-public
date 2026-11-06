using System.Threading;
using System.Threading.Tasks;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

// Fixture-side operations surface over a host the fixture already owns: it never acquires or
// releases the product lock and never disposes the host or its state authority.
internal sealed class InProcessFixtureRuntime(SyncAuthorityRuntimeHost host, PilotStateAuthority state)
    : SyncCliOperationsBase
{
    protected override SyncAuthorityRuntimeHost Host => host;
    protected override PilotStateAuthority StateAuthority => state;
    public override string Mode => "running-application";

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
