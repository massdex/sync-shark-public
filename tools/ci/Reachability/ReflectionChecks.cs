using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class ReflectionChecks
{
    static readonly (string Expression, bool Reject)[] Cases = [
            ("System.Type.GetType(\"Example.Hidden\")", true),
            ("typeof(object).Assembly.GetType(\"Example.Hidden\")", true),
            ("typeof(object).Assembly.CreateInstance(\"Example.Hidden\")", true),
            ("typeof(object).Module.GetType(\"Example.Hidden\")", true),
            ("typeof(object).Module.GetTypes()", true),
            ("typeof(object).Assembly.GetTypes()", true),
            ("typeof(object).Assembly.DefinedTypes", true),
            ("typeof(object).Assembly.ExportedTypes", true),
            ("typeof(object).Assembly.GetExportedTypes()", true),
            ("typeof(object).Assembly.GetForwardedTypes()", true),
            ("System.Reflection.Assembly.Load(\"Example\")", true),
            ("System.Activator.CreateInstance(\"Example\", \"Example.Hidden\")", true),
            ("System.Activator.CreateInstanceFrom(\"Example.dll\", \"Example.Hidden\")", true),
            ("System.AppDomain.CurrentDomain.CreateInstance(\"Example\", \"Example.Hidden\")", true),
            ("System.AppDomain.CurrentDomain.CreateInstanceAndUnwrap(\"Example\", \"Example.Hidden\")", true),
            ("typeof(object).MakeGenericType(typeof(string))", true),
            ("typeof(object).InvokeMember(\"Example\", System.Reflection.BindingFlags.Default, null, null, null)", true),
            ("System.Activator.CreateInstance(typeof(object))", false),
            ("System.Activator.CreateInstance<object>()", false),
            ("new object().GetType()", false),
            ("typeof(object).GetMethod(\"ToString\")", false),
            ("new Local().GetType(\"Example\")", false)
    ];

    internal static void Verify()
    {
        var source = "class Local { public object GetType(string name) => name; } class Fixture { " +
            string.Join("\n", Cases.Select((c, i) => $"object Case{i}() => {c.Expression};")) + " }";
        var tree = CSharpSyntaxTree.ParseText(source, path: "src/Fixture.cs");
        var compilation = CSharpCompilation.Create("Fixture", [tree], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Analysis.ValidateCompilation(compilation);
        var model = compilation.GetSemanticModel(tree);
        var graph = new Analysis(Environment.CurrentDirectory, new Config([], [], [], [], []));
        foreach (var method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText.StartsWith("Case")))
            Check(graph, model, method, Cases[int.Parse(method.Identifier.ValueText[4..])].Reject);
        Console.WriteLine($"Reflection enforcement fixtures passed: {Cases.Length} product Cases and matching test exemptions.");
    }

    static void Check(Analysis graph, SemanticModel model, MethodDeclarationSyntax method, bool reject)
    {
        var expression = method.ExpressionBody!.Expression;
        var symbol = model.GetSymbolInfo(expression).Symbol ?? throw new InvalidOperationException("Unbound reflection fixture.");
        var owner = model.GetDeclaredSymbol(method)!.ContainingType;
        foreach (var product in new[] { true, false })
        {
            var node = new TypeNode("fixture", "src/Fixture.csproj", owner, product);
            string? diagnostic = null;
            try { graph.Reference(node, symbol, "src/Fixture.cs", expression.SpanStart); }
            catch (InvalidOperationException error) { diagnostic = error.Message; }
            var expected = product && reject;
            if (expected != (diagnostic is not null) || expected &&
                (!diagnostic!.Contains($"src/Fixture.cs@{expression.SpanStart}") || !diagnostic.Contains(symbol.ToDisplayString())))
                throw new InvalidOperationException($"Reflection fixture failed: {method.Identifier} product={product}: {diagnostic}");
        }
    }
}
