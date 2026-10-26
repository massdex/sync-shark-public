namespace Massdex.SyncShark.V2;

internal static class DeleteVictimKey
{
    internal static string Of(Side side, string path) => (side == Side.A ? "A" : "B") + ":" + path;
}
