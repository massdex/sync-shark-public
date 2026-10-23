// SS-D01: one reserved run identity and cancellation target per public run/scan/first-sync invocation.
//
// The public entries that spend real time before their command is durably accepted (batch run, bootstrap
// scan/preparation and reviewed first sync) reserve the existing run gate and publish the one
// command-<Guid> identity BEFORE that preparation, health and admission work. A reader can therefore see
// and stop the run while it is still preparing; the reserved control's own token reaches every
// preparation/health/database await that already accepts cancellation; and a refused invocation still
// publishes no control, no durable command and no effect. This is not a second run-control service: the
// existing PilotRunControl, run gate, active snapshot and checkpoint remain the only control surface.
namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityRuntimeHost
{
    /// <summary>
    /// Reserves the run gate with the existing zero-wait refusal, then allocates exactly one
    /// command-&lt;Guid&gt; identity and publishes the preparing control. A refused invocation throws
    /// before any control is published, so it produces no control, no durable command and no effect.
    /// </summary>
    private async Task<PendingRunLease> BeginPendingRunAsync(CancellationToken cancellationToken,
        string kind, string? pairId)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Another sync command is already active.");
        try
        {
            // The pending identity is now a reserved command ticket instead of a bare caller-selectable
            // string: the display id is exactly the ticket's own id, and the ticket carries the single-use
            // insertion claim that the actual durable INSERT consumes. Nothing here writes durably, so a
            // refused invocation still publishes no control, no durable command and no effect.
            var reservation = SyncAuthorityCommandReservation.Mint();
            var control = CreateRunControl(reservation.CommandId, kind, pairId, preparing: true);
            return new PendingRunLease(this, control, reservation, cancellationToken);
        }
        catch
        {
            _runGate.Release();
            throw;
        }
    }

    /// <summary>
    /// Owns the reserved gate and the preparing control until the exact command is accepted. Cancellation
    /// or an exception anywhere before acceptance clears only this invocation's own control and releases
    /// its gate exactly once through EndRun's reference check, so a replacement run's ownership is never
    /// released and no semaphore is leaked.
    /// </summary>
    private sealed class PendingRunLease : IDisposable
    {
        private readonly SyncAuthorityRuntimeHost _host;
        private readonly CancellationTokenSource _linked;
        private int _ownsGate = 1;

        internal PendingRunLease(SyncAuthorityRuntimeHost host, PilotRunControl control,
            SyncAuthorityCommandReservation reservation, CancellationToken cancellationToken)
        {
            _host = host;
            Control = control;
            Reservation = reservation;
            _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, control.StopToken);
        }

        internal PilotRunControl Control { get; }

        /// <summary>The one reserved command ticket this invocation owns until acceptance consumes it.</summary>
        internal SyncAuthorityCommandReservation Reservation { get; }

        /// <summary>The same identity the ticket carries, so the published control is unchanged.</summary>
        internal string CommandId => Reservation.CommandId;

        /// <summary>The reserved control's own token, which reaches every preparation/health/DB await.</summary>
        internal CancellationToken Token => _linked.Token;

        /// <summary>
        /// The durably accepted command now owns the published identity and the gate. EndRun remains the
        /// one idempotent, reference-checked release; this lease stops owning both and stops advertising
        /// the run as preparing only after that durable commit.
        /// </summary>
        internal PilotRunControl EnterExecution()
        {
            if (Interlocked.Exchange(ref _ownsGate, 0) != 0)
            {
                _linked.Dispose();
                _host.MarkRunAccepted(Control);
            }
            return Control;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ownsGate, 0) == 0) return;
            _linked.Dispose();
            _host.EndRun(Control);
        }
    }
}
