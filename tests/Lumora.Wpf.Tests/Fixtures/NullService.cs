using System.Reflection;

namespace Lumora.Wpf.Tests.Fixtures;

/// <summary>
/// Leere Implementierung beliebiger Service-Interfaces für ViewModel-Tests (DispatchProxy): jede Methode tut nichts und
/// liefert einen erledigten Task bzw. Standardwert — Listen leer statt null.
/// </summary>
public class NullService : DispatchProxy
{
    public static T Create<T>()
        where T : class => DispatchProxy.Create<T, NullService>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => DefaultFor(targetMethod!.ReturnType);

    private static object? DefaultFor(Type type)
    {
        if (type == typeof(void))
        {
            return null;
        }

        if (type == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var argument = type.GetGenericArguments()[0];
            if (definition == typeof(Task<>))
            {
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(argument).Invoke(null, [DefaultFor(argument)]);
            }

            if (definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>)
                || definition == typeof(IEnumerable<>) || definition == typeof(IList<>) || definition == typeof(ICollection<>))
            {
                return Array.CreateInstance(argument, 0);
            }
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
