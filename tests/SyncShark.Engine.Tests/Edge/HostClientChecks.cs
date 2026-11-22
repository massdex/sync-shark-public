using Massdex.SyncShark.Engine.Edge;

namespace Massdex.SyncShark.Engine.Tests.Edge;

public static class HostClientChecks
{
    public static async Task RunAsync()
    {
        var result = await new HostClient(Guid.NewGuid().ToString("N")).SendAsync(["status"], TextWriter.Null, TextWriter.Null, default);
        Check.Require(result.ExitCode is null && !result.ReceivedExit, "An absent host did not return definite pre-send absence");
        using var request = new MemoryStream();
        await PipeFrame.WriteAsync(request, new byte[]{0,91,34,115,116,97,116,117,115,34,93}, default);
        foreach (bool terminal in new[] { true, false }) {
            using var exchange = new MemoryStream(); exchange.SetLength(request.Length); exchange.Position=request.Length;
            await PipeFrame.WriteAsync(exchange, new byte[]{1}, default);
            await PipeFrame.WriteAsync(exchange, new byte[]{2,65}, default);
            await PipeFrame.WriteAsync(exchange, new byte[]{3,66}, default);
            if (terminal) await PipeFrame.WriteAsync(exchange, new byte[]{4,1,0,0,0}, default);
            exchange.Position=0; using var output=new StringWriter(); using var error=new StringWriter();
            var received=await HostClient.ExchangeAsync(exchange,["status"],output,error,default);
            Check.Require(received.ExitCode==1 && received.ReceivedExit==terminal && output.ToString()=="A" && error.ToString()=="B", "Dropped reply confused with terminal failure or stderr lost");
        }
    }
}
