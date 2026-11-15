using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine;

/// <summary>The effect kind of a written side (section 6.1). Effect selection stays in the Executor.</summary>
public static class EffectKinds
{
    /// <summary>The deleted-entry effect of a side.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EffectKind Deleted(Side side) => side == Side.Local
        ? EffectKind.DeletedLocal : EffectKind.DeletedDropbox;

    /// <summary>The moved-entry effect of a side.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EffectKind Moved(Side side) => side == Side.Local
        ? EffectKind.MovedLocal : EffectKind.MovedDropbox;
}
