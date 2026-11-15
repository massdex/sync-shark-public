using System.Text;

namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>The shared CM01-CM10 content-match case data exercised by both transfer gates; gate and failure labels are test data, not product vocabulary.</summary>
internal static class ContentMatchCases
{
    private const string EmptyDigest =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string LowerHash =
        "c4859b12a7021c65b00a15b817f80af43a326bbf2e861b70e20196895b2d0f3d";
    private const string UpperHash =
        "C4859B12A7021C65B00A15B817F80AF43A326BBF2E861B70E20196895B2D0F3D";
    private const string MixedHash =
        "C4859B12a7021c65b00a15b817f80af43a326bbf2e861b70e20196895b2d0f3d";
    private const string OtherDigest =
        "0000000000000000000000000000000000000000000000000000000000000000";
    private const string ShortHash = "abc123";
    private const string LongHash = LowerHash + "0";
    private const string NonHexHash =
        "g4859b12a7021c65b00a15b817f80af43a326bbf2e861b70e20196895b2d0f3d";
    private const string LeadingSpaceHash = " " + LowerHash;
    private const string TrailingSpaceHash = LowerHash + " ";
    private const string WhitespaceHash = "   ";

    private static readonly byte[] BaseBody = Encoding.UTF8.GetBytes("content-match-cm02");

    /// <summary>Every CM case in contract order, with each specified subcase as its own row.</summary>
    internal static IEnumerable<(string Id, byte[] Body, long AdvertisedSize, string? ExpectedHash,
        string ExpectedGate, string? LaterFailure)> All() =>
    [
        ("CM01", [], 0, EmptyDigest, "accept", null),
        ("CM02", BaseBody, BaseBody.Length, LowerHash, "accept", null),
        ("CM03-uppercase", BaseBody, BaseBody.Length, UpperHash, "accept", null),
        ("CM03-mixedcase", BaseBody, BaseBody.Length, MixedHash, "accept", null),
        ("CM04-null", BaseBody, BaseBody.Length, null, "hash", null),
        ("CM04-empty", BaseBody, BaseBody.Length, "", "hash", null),
        ("CM04-whitespace", BaseBody, BaseBody.Length, WhitespaceHash, "hash", null),
        ("CM05-short", BaseBody, BaseBody.Length, ShortHash, "hash", null),
        ("CM05-long", BaseBody, BaseBody.Length, LongHash, "hash", null),
        ("CM05-nonhex", BaseBody, BaseBody.Length, NonHexHash, "hash", null),
        ("CM05-leading-space", BaseBody, BaseBody.Length, LeadingSpaceHash, "hash", null),
        ("CM05-trailing-space", BaseBody, BaseBody.Length, TrailingSpaceHash, "hash", null),
        ("CM06", BaseBody, BaseBody.Length, OtherDigest, "hash", null),
        ("CM07-empty", [], 0, "", "hash", null),
        ("CM07-different", [], 0, OtherDigest, "hash", null),
        ("CM08-below", BaseBody, BaseBody.Length + 1, LowerHash, "size", null),
        ("CM08-above", BaseBody, BaseBody.Length - 1, LowerHash, "size", null),
        ("CM09-below-null", BaseBody, BaseBody.Length + 1, null, "size", null),
        ("CM09-below-empty", BaseBody, BaseBody.Length + 1, "", "size", null),
        ("CM09-below-whitespace", BaseBody, BaseBody.Length + 1, WhitespaceHash, "size", null),
        ("CM09-below-short", BaseBody, BaseBody.Length + 1, ShortHash, "size", null),
        ("CM09-below-long", BaseBody, BaseBody.Length + 1, LongHash, "size", null),
        ("CM09-below-nonhex", BaseBody, BaseBody.Length + 1, NonHexHash, "size", null),
        ("CM09-below-leading-space", BaseBody, BaseBody.Length + 1, LeadingSpaceHash, "size", null),
        ("CM09-below-trailing-space", BaseBody, BaseBody.Length + 1, TrailingSpaceHash, "size", null),
        ("CM09-below-different", BaseBody, BaseBody.Length + 1, OtherDigest, "size", null),
        ("CM09-above-null", BaseBody, BaseBody.Length - 1, null, "size", null),
        ("CM09-above-empty", BaseBody, BaseBody.Length - 1, "", "size", null),
        ("CM09-above-whitespace", BaseBody, BaseBody.Length - 1, WhitespaceHash, "size", null),
        ("CM09-above-short", BaseBody, BaseBody.Length - 1, ShortHash, "size", null),
        ("CM09-above-long", BaseBody, BaseBody.Length - 1, LongHash, "size", null),
        ("CM09-above-nonhex", BaseBody, BaseBody.Length - 1, NonHexHash, "size", null),
        ("CM09-above-leading-space", BaseBody, BaseBody.Length - 1, LeadingSpaceHash, "size", null),
        ("CM09-above-trailing-space", BaseBody, BaseBody.Length - 1, TrailingSpaceHash, "size", null),
        ("CM09-above-different", BaseBody, BaseBody.Length - 1, OtherDigest, "size", null),
        // CM10 upload rows: envelope = invalid result identity/envelope before the gate; identity = failed recording after a valid result.
        ("CM10-flush", BaseBody, BaseBody.Length, LowerHash, "accept", "flush"),
        ("CM10-timestamp", BaseBody, BaseBody.Length, LowerHash, "accept", "timestamp"),
        ("CM10-publication", BaseBody, BaseBody.Length, LowerHash, "accept", "publication"),
        ("CM10-commit", BaseBody, BaseBody.Length, LowerHash, "accept", "commit"),
        ("CM10-upload-envelope", BaseBody, BaseBody.Length, LowerHash, "accept", "upload-envelope"),
        ("CM10-upload-identity", BaseBody, BaseBody.Length, LowerHash, "accept", "upload-identity"),
    ];
}
