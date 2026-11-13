namespace Fixture.Apps;
public class Example
{
    public int Number = 17;
    public System.Func<int> Factory = () => 42;
    public event System.Action Changed = delegate { };
    public int Overload(int value) => value + Number;
    public string Overload(string value) => value + "alpha";
    public void Raise() => Changed();
    public async System.Threading.Tasks.Task<int> BlockingAsync(
        System.IO.Stream stream, System.Threading.Tasks.Task<int> pending,
        System.Threading.CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1];
        stream.Read(buffer, 0, buffer.Length);
        await System.Threading.Tasks.Task.Delay(1);
        return pending.Result;
    }
    public void EmptyCatch()
    {
        try { Raise(); } catch (System.Exception) { }
    }
}
public class Other { }
