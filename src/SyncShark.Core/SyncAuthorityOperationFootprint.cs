using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Massdex.SyncShark;

// One immutable scheduling descriptor per operation.
//
// The pump builds it once, on the first admission examination of that item, and reuses it
// for every later capacity check, barrier comparison and active-window comparison of the same
// item. It carries exactly the derived facts the scheduler used to recompute on every
// comparison: ready status and kind, member roles and shapes, the directory-create opt-in,
// dependency identities, object/membership aliases, same-root path overlaps and the source
// byte estimate.
//
// Parsing member expected-state JSON is the expensive part of a comparison, so each member's
// state is parsed exactly once here instead of once per comparison, and each relative path is
// normalized at most once. Nothing is cached across a pump invocation: there is no global
// cache and no new authority, and every fact is derived from the same immutable snapshot the
// caller's projection returned.
internal sealed class SyncAuthorityOperationFootprint
{
    // Test-only observations of the real descriptor work: one member expected-state parse and
    // one relative-path normalization. Production installs no callback, so the default null is
    // a no-op branch; these report the production work instead of replacing it.
    internal static Action<string>? MemberStateParsedForTest { get; set; }
    internal static Action<string>? MemberPathNormalizedForTest { get; set; }

    private static readonly string[] BytePropertyNames =
        ["Size", "size", "SizeBytes", "sizeBytes", "Length", "length"];

    private readonly Member[] _members;
    private readonly HashSet<string> _dependencies;
    private readonly long _expectedBytes;
    private readonly ExceptionDispatchInfo? _expectedBytesRefusal;

    private SyncAuthorityOperationFootprint(string operationId, bool fileEligible, bool directoryEligible,
        Member[] members, HashSet<string> dependencies, long expectedBytes,
        ExceptionDispatchInfo? expectedBytesRefusal)
    {
        OperationId = operationId;
        FileEligible = fileEligible;
        DirectoryEligible = directoryEligible;
        _members = members;
        _dependencies = dependencies;
        _expectedBytes = expectedBytes;
        _expectedBytesRefusal = expectedBytesRefusal;
    }

    /// <summary>The retained operation identity this descriptor was derived from.</summary>
    internal string OperationId { get; }

    /// <summary>Exactly the previous file-transfer eligibility of the same snapshot.</summary>
    internal bool FileEligible { get; }

    /// <summary>Exactly the previous explicitly opted-in directory-create eligibility.</summary>
    internal bool DirectoryEligible { get; }

    /// <summary>
    /// The source byte estimate of exactly the source member the previous implementation read, or
    /// the refusal that implementation raised from the same member when its expected state was not
    /// a JSON object. The refusal stays attached to the estimate, so a malformed retained state can
    /// never be read as a zero-byte estimate.
    /// </summary>
    internal long ExpectedBytes()
    {
        _expectedBytesRefusal?.Throw();
        return _expectedBytes;
    }

    /// <summary>
    /// Derives one descriptor from exactly one operation snapshot. It is total for every state the
    /// previous inline predicates tolerated: unreadable or misshaped member state only removes
    /// eligibility, so malformed retained data stays non-independent instead of becoming permissive.
    /// </summary>
    internal static SyncAuthorityOperationFootprint Create(SyncAuthorityOperationSnapshot operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var members = new Member[operation.Members.Count];
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in operation.Dependencies) dependencies.Add(dependency.DependsOnOperationId);
        var expectedBytes = 0L;
        ExceptionDispatchInfo? expectedBytesRefusal = null;
        var sourceBytesRead = false;
        var fileMembers = true;
        var fileSource = false;
        var directoryMembers = operation.Members.Count == 2;
        var directorySources = 0;
        var directoryDestinations = 0;
        for (var index = 0; index < operation.Members.Count; index++)
        {
            var member = operation.Members[index];
            var isSource = member.Role == "source";
            var isDestination = member.Role == "destination";
            // The previous byte estimate read exactly the first source member.
            var wantsBytes = isSource && !sourceBytesRead;
            var state = ReadMemberState(member, operation.OperationId, wantsBytes);
            if (wantsBytes)
            {
                sourceBytesRead = true;
                expectedBytes = state.Bytes ?? 0;
                expectedBytesRefusal = state.Refusal;
            }
            var parsed = new Member(operation.OperationId, member, state);
            members[index] = parsed;
            fileMembers &= parsed.Readable && parsed.KindUsable && parsed.HasPath &&
                           (isSource || isDestination) &&
                           (parsed.KindIsFile || isDestination && parsed.PresentFalse);
            fileSource |= isSource && parsed.KindIsFile;
            directoryMembers &= parsed.Readable && parsed.KindUsable && parsed.HasPath && parsed.HasRoot &&
                                (isSource && parsed.KindIsDirectory ||
                                 isDestination && (parsed.ObjectKind is null || parsed.KindIsDirectory) &&
                                 parsed.PresentFalse);
            if (isSource && parsed.KindIsDirectory) directorySources++;
            if (isDestination && (parsed.ObjectKind is null || parsed.KindIsDirectory) && parsed.PresentFalse)
                directoryDestinations++;
        }
        var ready = operation.Status == SyncAuthorityOperationStatus.Ready;
        var fileKinds = operation.Kind is SyncAuthorityOperationKind.CreateLocal or
            SyncAuthorityOperationKind.CreateRemote or SyncAuthorityOperationKind.UpdateLocal or
            SyncAuthorityOperationKind.UpdateRemote;
        var directoryKinds = operation.Kind is SyncAuthorityOperationKind.CreateLocal or
            SyncAuthorityOperationKind.CreateRemote;
        // Recovery/Rewind retain their directory barrier, and only an explicitly opted-in reviewed
        // create is directory-eligible; a subtree reservation or an existing attempt always stays serial.
        var fileEligible = ready && fileKinds && operation.SubtreeReservations.Count == 0 &&
            operation.Members.Count != 0 && fileMembers && fileSource;
        var directoryEligible = ready && directoryKinds && operation.SubtreeReservations.Count == 0 &&
            operation.Attempts.Count == 0 && directoryMembers && directorySources == 1 &&
            directoryDestinations == 1;
        return new SyncAuthorityOperationFootprint(operation.OperationId, fileEligible, directoryEligible,
            members, dependencies, expectedBytes, expectedBytesRefusal);
    }

    /// <summary>
    /// Exactly the previous independence decision between two descriptors: ready kind/status, the
    /// directory-create opt-in, the same operation, a dependency in either direction, an object or
    /// membership alias overlap, or a same-root equal/ancestor/descendant path overlap.
    /// </summary>
    internal static bool Independent(SyncAuthorityOperationFootprint left,
        SyncAuthorityOperationFootprint right, bool allowDirectoryCreates = false)
    {
        if (!(left.FileEligible || allowDirectoryCreates && left.DirectoryEligible) ||
            !(right.FileEligible || allowDirectoryCreates && right.DirectoryEligible) ||
            left.OperationId == right.OperationId ||
            left._dependencies.Contains(right.OperationId) ||
            right._dependencies.Contains(left.OperationId)) return false;
        foreach (var a in left._members)
        foreach (var b in right._members)
        {
            // An identity can have more than one name; path separation alone is
            // insufficient. Both endpoint members contribute to the footprint.
            if (a.ObjectId == b.ObjectId ||
                a.MembershipId is not null && a.MembershipId == b.MembershipId) return false;
            if (a.RootBindingId == b.RootBindingId && a.Overlaps(b)) return false;
        }
        return true;
    }

    /// <summary>
    /// One member's parsed scheduling facts: the raw identity/root/path fields the comparison uses,
    /// plus the shape facts the previous inline predicates derived from the expected state.
    /// </summary>
    private readonly record struct MemberState(bool Readable, string? ObjectKind, bool KindUsable,
        bool PresentFalse, long? Bytes, ExceptionDispatchInfo? Refusal);

    private static MemberState ReadMemberState(SyncAuthorityMember member, string operationId, bool wantsBytes)
    {
        MemberStateParsedForTest?.Invoke(operationId);
        try
        {
            using var document = JsonDocument.Parse(member.ExpectedStateJson);
            var state = document.RootElement;
            if (state.ValueKind != JsonValueKind.Object)
                return new MemberState(false, null, false, false, null,
                    wantsBytes ? RootNotObjectRefusal(state) : null);
            var kindUsable = true;
            string? kind;
            try
            {
                kind = state.TryGetProperty("ObjectKind", out var declared) ||
                       state.TryGetProperty("objectKind", out declared) ? declared.GetString() : null;
            }
            catch (InvalidOperationException)
            {
                kindUsable = false;
                kind = null;
            }
            var presentFalse = state.TryGetProperty("present", out var present) &&
                               present.ValueKind == JsonValueKind.False;
            long? bytes = null;
            if (wantsBytes)
                foreach (var name in BytePropertyNames)
                    if (state.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
                        value.TryGetInt64(out var parsed))
                    {
                        bytes = Math.Max(0, parsed);
                        break;
                    }
            return new MemberState(true, kind, kindUsable, presentFalse, bytes, null);
        }
        catch (JsonException)
        {
            return new MemberState(false, null, false, false, null, null);
        }
    }

    // The previous byte estimate read the first size property off the source member's root element, so
    // a non-object root raised exactly this refusal. Reproduce that same refusal instead of reporting a
    // fabricated zero-byte estimate, which would admit work against unreadable retained state.
    private static ExceptionDispatchInfo RootNotObjectRefusal(JsonElement state)
    {
        try
        {
            state.TryGetProperty(BytePropertyNames[0], out _);
        }
        catch (InvalidOperationException error)
        {
            return ExceptionDispatchInfo.Capture(error);
        }
        return ExceptionDispatchInfo.Capture(
            new InvalidOperationException("The retained member state is not a JSON object."));
    }

    private sealed class Member
    {
        private readonly string _operationId;
        private readonly string? _expectedPathKey;
        private string? _normalizedPathKey;
        private bool _normalizationReported;

        internal Member(string operationId, SyncAuthorityMember member, MemberState state)
        {
            _operationId = operationId;
            Role = member.Role;
            ObjectId = member.ObjectId;
            MembershipId = member.MembershipId;
            RootBindingId = member.RootBindingId;
            _expectedPathKey = member.ExpectedPathKey;
            Readable = state.Readable;
            ObjectKind = state.ObjectKind;
            KindUsable = state.KindUsable;
            PresentFalse = state.PresentFalse;
        }

        internal string Role { get; }
        internal string ObjectId { get; }
        internal string? MembershipId { get; }
        internal string RootBindingId { get; }
        internal bool Readable { get; }
        internal string? ObjectKind { get; }
        internal bool KindUsable { get; }
        internal bool PresentFalse { get; }
        internal bool HasPath => !string.IsNullOrWhiteSpace(_expectedPathKey);
        internal bool HasRoot => !string.IsNullOrWhiteSpace(RootBindingId);
        internal bool KindIsFile => KindUsable &&
            string.Equals(ObjectKind, "file", StringComparison.OrdinalIgnoreCase);
        internal bool KindIsDirectory => KindUsable &&
            string.Equals(ObjectKind, "directory", StringComparison.OrdinalIgnoreCase);

        // The previous overlap predicate normalized both paths on every comparison. The normalized
        // key is retained per member instead, so one member is normalized at most once while an
        // unsafe path still raises the same refusal on every comparison that reaches it.
        private string NormalizedPathKey
        {
            get
            {
                if (_normalizedPathKey is not null) return _normalizedPathKey;
                if (!_normalizationReported)
                {
                    _normalizationReported = true;
                    MemberPathNormalizedForTest?.Invoke(_operationId);
                }
                return _normalizedPathKey = PilotPath.NormalizeRelative(_expectedPathKey!);
            }
        }

        internal bool Overlaps(Member other)
        {
            var left = NormalizedPathKey;
            var right = other.NormalizedPathKey;
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
                   left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase) ||
                   right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
