#pragma warning disable CS1591
#nullable disable
namespace Fixture.Tools;
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "Synthetic suppression fixture")]
public class Suppressions
{
    public void Run() { try { throw new System.Exception(); } catch (System.Exception) { } }
}
#pragma warning restore CS1591
#nullable restore
