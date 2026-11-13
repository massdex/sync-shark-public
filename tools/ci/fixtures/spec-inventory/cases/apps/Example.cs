namespace Fixture.Apps;
public class Example
{
    public int Number = 17;
    public System.Func<int> Factory = () => 42;
    public event System.Action Changed = delegate { };
    public int Overload(int value) => value + Number;
    public string Overload(string value) => value + "alpha";
    public void Raise() => Changed();
    public void EmptyCatch()
    {
        try { Raise(); } catch (System.Exception) { }
    }
}
public class Other { }
