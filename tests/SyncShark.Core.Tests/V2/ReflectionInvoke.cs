using System.Reflection;

namespace Massdex.SyncShark.V2;

/// <summary>
/// The V2 checks reach private owners through reflection and pass only the arguments the check owns.
/// A later optional parameter must not break those calls, so the trailing arguments are filled from
/// each parameter's declared default instead of from a hand-counted array.
/// </summary>
internal static class ReflectionInvoke
{
    internal static object? WithDefaults(MethodInfo method, object? target, params object?[] supplied)
    {
        var parameters = method.GetParameters();
        if (supplied.Length > parameters.Length)
            throw new ArgumentException(
                $"{method.DeclaringType?.Name}.{method.Name} takes {parameters.Length} arguments, {supplied.Length} supplied.",
                nameof(supplied));
        var arguments = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
            arguments[i] = i < supplied.Length ? supplied[i] : DeclarationDefault(method, parameters[i]);
        return method.Invoke(target, arguments);
    }

    private static object? DeclarationDefault(MethodInfo method, ParameterInfo parameter) =>
        parameter.HasDefaultValue && parameter.DefaultValue is not (DBNull or Missing)
            ? parameter.DefaultValue
            : throw new ArgumentException(
                $"{method.DeclaringType?.Name}.{method.Name} parameter '{parameter.Name}' has no declared default; pass it explicitly.",
                nameof(parameter));
}
