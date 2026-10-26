using System.Windows.Controls;

namespace Massdex.SyncShark;

/// <summary>Realizes one saved-path ancestor without constructing its off-screen siblings.</summary>
public sealed class FolderVirtualizingPanel : VirtualizingStackPanel
{
    /// <summary>Horizontal step applied for each folder level in the lazy folder pickers.</summary>
    public const double IndentStep = 16;

    /// <summary>
    /// Deep hierarchies keep a usable row width: the left indentation stops growing once a row would
    /// leave too little room for the folder name and its expand chevron.
    /// </summary>
    public const double MaxIndent = 96;

    /// <summary>Bounded left indentation for one folder row at the supplied depth.</summary>
    public static double BoundedIndent(int depth) => depth <= 0 ? 0 : System.Math.Min(depth * IndentStep, MaxIndent);

    public void RevealIndex(int index) => BringIndexIntoView(index);
}
