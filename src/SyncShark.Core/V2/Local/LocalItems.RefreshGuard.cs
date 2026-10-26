namespace Massdex.SyncShark.V2;

internal static partial class LocalItems
{
    /// <summary>The scoped-refresh paths whose ancestors are verified, holding those ancestors until disposed.</summary>
    internal sealed class RefreshGuard : IDisposable
    {
        private readonly List<PilotDirectoryLease> _leases = new();

        /// <summary>Paths whose every ancestor is held, inside the root and the recorded object.</summary>
        public List<string> Safe { get; } = new();

        /// <summary>Unsafe ancestors whose whole subtree is reported not known.</summary>
        public List<string> Incomplete { get; } = new();
        internal List<PilotDecision> Quarantine { get; } = new();
        internal ContentProofFailure? Failure { get; private set; }
        internal void RecordFailure(string path, Exception error)
        {
            Failure ??= ContentProofFailure.FromException(ContentProofFailure.StageKind.ParentIdentity, error);
            if (QuarantineFailure.NativeCode(error) is { } code)
                Quarantine.Add(new PilotDecision(path, PilotDecisionKind.Blocked, error.Message) { QuarantineCode = code });
        }

        internal void Hold(PilotDirectoryLease lease) => _leases.Add(lease);

        /// <summary>Releases every held ancestor.</summary>
        public void Dispose()
        {
            foreach (var lease in _leases) lease.Dispose();
            _leases.Clear();
        }
    }

}
