using System.IO.Pipes;
using System.Security.Principal;
using Massdex.SyncShark.Engine.Edge;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Massdex.SyncShark.Engine.Tests.Edge;

public static class HostServerChecks
{
    public static async Task RunAsync()
    {
        var root = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Environment.CurrentDirectory;
        var source = File.ReadAllText(Path.Combine(root, "src", "SyncShark.Engine", "Edge", "HostServer.cs"));
        var client = File.ReadAllText(Path.Combine(root, "src", "SyncShark.Engine", "Edge", "HostClient.cs"));
        foreach (var unit in new[] {source,client})
        {
            var connect = CSharpSyntaxTree.ParseText(unit).GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Single(call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConnectAsync" });
            Check.Require(connect.ArgumentList.Arguments[0].Expression.GetLastToken().ValueText == "ConnectTimeoutMilliseconds" &&
                connect.ArgumentList.Arguments[1].Expression.ToString() == "ct", "Both connect paths must observe the shared deadline and cancellation");
        }
        var deadline=CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(v=>v.Identifier.ValueText=="ConnectTimeoutMilliseconds");
        Check.Require(deadline.Initializer?.Value is LiteralExpressionSyntax literal && Equals(literal.Token.Value,300),"Pipe deadline differs from the original local connection window");
        var handler = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == nameof(HostServer.ServeAsync))
            .SelectMany(method => method.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
            .Single(local => local.Identifier.ValueText == "HandleAsync");
        Check.Require(handler.DescendantNodes().OfType<CatchClauseSyntax>().Any(clause =>
            (clause.Declaration is null || clause.Declaration.Type.ToString() == nameof(Exception)) &&
            !clause.Block.DescendantNodes().OfType<ThrowStatementSyntax>().Any() &&
            clause.Block.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call =>
                call.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == nameof(object.GetType))),
            "The connection handler must report unexpected exceptions without stopping the listener");
        var frameWrites=CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call=>call.Expression.ToString()=="PipeFrame.WriteAsync").ToArray();
        Check.Require(frameWrites.Length!=0 && frameWrites.All(call=>call.ArgumentList.Arguments.Last().Expression.ToString()!="default"),
            "A server frame write still ignores shutdown");
        using var identity=WindowsIdentity.GetCurrent();
        var hostPipe=$"{nameof(HostServer)}.{ResourceNames.HostExecutable}.{identity.User!.Value}";
        using (var owner = new HostServer()) {
            Check.Require(await owner.ClaimAsync(default), "First server failed to claim the pipe");
            // Use SMB loopback, not the local pipe namespace. A broken SMB route must not count as rejection.
            var controlName=Guid.NewGuid().ToString("N");
            using var control=new NamedPipeServerStream(controlName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.CurrentUserOnly);
            using var allowed=new NamedPipeClientStream("127.0.0.1",controlName,PipeDirection.InOut);
            allowed.Connect(0);
            using var remote=new NamedPipeClientStream("127.0.0.1",hostPipe,PipeDirection.InOut);
            try {remote.Connect(0);throw new InvalidDataException("Host accepted a network pipe connection");}
            catch(UnauthorizedAccessException) { /* The available host listener refused the remote client. */ }
            using var contender = new HostServer();
            bool refused;
            try { refused = !await contender.ClaimAsync(default); } catch (UnauthorizedAccessException) { refused = true; }
            Check.Require(refused, "Second claim succeeded before the owner released the pipe");
        }
        using var next = new HostServer();
        Check.Require(await next.ClaimAsync(default), "Released pipe could not be claimed");
        using var local=new NamedPipeClientStream(".",hostPipe,PipeDirection.InOut,PipeOptions.CurrentUserOnly);
        local.Connect(0);
    }
}
