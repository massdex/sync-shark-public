using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class WpfHelper
{
    // WPF MarkupCompiler.GenerateInternalTypeHelperImplementation: bodies, signatures and member count are fixed.
    const string Template = """
        class Template {
            protected override object CreateInstance(System.Type type, System.Globalization.CultureInfo culture) {
                return System.Activator.CreateInstance(type, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.CreateInstance, null, null, culture);
            }
            protected override object GetPropertyValue(System.Reflection.PropertyInfo propertyInfo, object target, System.Globalization.CultureInfo culture) {
                return propertyInfo.GetValue(target, System.Reflection.BindingFlags.Default, null, null, culture);
            }
            protected override void SetPropertyValue(System.Reflection.PropertyInfo propertyInfo, object target, object value, System.Globalization.CultureInfo culture) {
                propertyInfo.SetValue(target, value, System.Reflection.BindingFlags.Default, null, null, culture);
            }
            protected override System.Delegate CreateDelegate(System.Type delegateType, object target, string handler) {
                return (System.Delegate)target.GetType().InvokeMember("_CreateDelegate", System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, target, new object[] { delegateType, handler }, null);
            }
            protected override void AddEventHandler(System.Reflection.EventInfo eventInfo, object target, System.Delegate handler) {
                eventInfo.AddEventHandler(target, handler);
            }
        }
        """;
    sealed class Parentheses : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitParenthesizedExpression(ParenthesizedExpressionSyntax node) => Visit(node.Expression);
    }
    static string Tokens(SyntaxNode node) => string.Join(" ", new Parentheses().Visit(node)!.DescendantTokens().Select(t => t.Text));
    internal static void Validate(Analysis graph)
    {
        var expected = CSharpSyntaxTree.ParseText(Template).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
        foreach (var node in graph.Types.Values.Where(t => t.Product && t.Files.Any(f => Path.GetFileName(f) == "GeneratedInternalTypeHelper.g.cs")))
        {
            var declarations = node.Symbol.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<ClassDeclarationSyntax>().ToArray();
            if (node.Files.Count != 1 || !node.Files.Single().StartsWith("build/ci/obj/") ||
                node.Symbol.ToDisplayString() != "XamlGeneratedNamespace.GeneratedInternalTypeHelper" ||
                node.Symbol.BaseType?.ToDisplayString() != "System.Windows.Markup.InternalTypeHelper" ||
                node.Symbol.BaseType?.ContainingAssembly?.Name != "WindowsBase" || !node.Symbol.IsSealed ||
                node.Symbol.DeclaredAccessibility != Accessibility.Public || declarations.Length != 1 ||
                declarations[0].Members.Count != expected.Members.Count ||
                !declarations[0].Members.Zip(expected.Members).All(pair => Tokens(pair.First) == Tokens(pair.Second)) ||
                declarations[0].SyntaxTree.GetRoot().DescendantNodes().Count(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax) != 1)
                throw new InvalidOperationException("Unreviewed WPF helper template: " + node.Files.First());
            graph.WpfHelpers.Add(node.Id);
            node.Roots.Add("component:wpf-helper");
        }
    }
    internal static void Close(Analysis graph)
    {
        foreach (var id in graph.WpfHelpers)
        foreach (var target in graph.Types.Values.Where(t => t.Product && t.Roots.Contains("component:xaml")))
            graph.Types[id].Edges.Add(new(target.Id, "", -1, "wpf-xaml"));
        var all = graph.Walk("ALL");
        var withoutHelper = graph.Walk("ALL", ignoreWpfHelpers: true);
        if (all.Keys.Except(withoutHelper.Keys).Any(id => !graph.WpfHelpers.Contains(id)) ||
            graph.WpfHelpers.SelectMany(id => graph.Types[id].Edges).Any(e => !graph.WpfHelpers.Contains(e.Target) && !graph.Types[e.Target].Roots.Contains("component:xaml")))
            throw new InvalidOperationException("WPF helper reaches a source type without independent XAML retention.");
        graph.Diagnostics.Add("Standard WPF helper template validated; every modeled source target is independently XAML-rooted.");
    }
}
