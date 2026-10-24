using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>Deterministic planner ids: a prefix plus 16 lower-hex of SHA-256 over the joined text.</summary>
internal static class PlanIds
{
    /// <summary>The stable key of one file identity; baseline rows keep this across commits.</summary>
    public static string ObjectKey(string pairId, Side side, string objectId) =>
        "k" + Hex16(pairId, SideText(side), objectId);

    /// <summary>The key of an identity split off a baseline (rules 10, 11, 27); never equals the baseline key.</summary>
    public static string Split(string objectKey, Side side) => "k" + Hex16(objectKey, "split", SideText(side));

    /// <summary>The plan-unique id of one action.</summary>
    public static string ActionId(string pairId, long gen, int rule, ActionKind kind, Side target, string path) =>
        "a" + Hex16(pairId, gen.ToString(CultureInfo.InvariantCulture), rule.ToString(CultureInfo.InvariantCulture),
            kind.ToString(), SideText(target), path.ToLowerInvariant());

    /// <summary>Baseline retirement is identity-scoped; all other effects retain their existing ids.</summary>
    public static string ActionId(string pairId, long gen, int rule, ActionKind kind, Side target, string path,
        string? objectKey) => kind == ActionKind.Forget
        ? "a" + Hex16(pairId, gen.ToString(CultureInfo.InvariantCulture), rule.ToString(CultureInfo.InvariantCulture),
            kind.ToString(), SideText(target), path.ToLowerInvariant(), objectKey ?? string.Empty)
        : ActionId(pairId, gen, rule, kind, target, path);

    /// <summary>The text form of a side used to build ids.</summary>
    private static string SideText(Side side) => side == Side.A ? "A" : "B";

    /// <summary>The first 8 bytes of SHA-256 over the newline-joined parts, as 16 lower-hex characters.</summary>
    /// <remarks>Allocation-light: a plan computes one per action and per new identity (a million on a large first sync).</remarks>
    private static string Hex16(params string[] parts)
    {
        const int StackLimit = 1024;
        var text = string.Join('\n', parts);
        var maxBytes = Encoding.UTF8.GetMaxByteCount(text.Length);
        var buffer = maxBytes <= StackLimit ? stackalloc byte[StackLimit] : new byte[maxBytes];
        var length = Encoding.UTF8.GetBytes(text, buffer);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(buffer[..length], hash);
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }
}
