using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Lumora.Services.Faces.Assignment;

namespace Lumora.Lightbox.Faces;

/// <summary>
/// ÜBERGANG bis zum Merge mit P1 (0.2.38): stellt <see cref="IFaceRecognitionService"/> für ein Objekt bereit, das die
/// Methoden nur gleichnamig anbietet (Ersatzdienst, Test-Fake), ohne das Interface zu implementieren. So kompiliert der
/// Ersatz auch dann noch, wenn P1s echtes Interface weitere Mitglieder hat (z. B. Ausreißer-Prüfung) — die Integration
/// (seit der Zusammenführung mit 0.2.37 nur noch von Test-Fakes genutzt). Fehlende Methoden: <see cref="Task"/>/void → tun nichts, sonst
/// <see cref="NotSupportedException"/>.
/// </summary>
public class FaceRecognitionForwarder : DispatchProxy
{
    private object _target = null!;

    public static IFaceRecognitionService Create(object target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var proxy = Create<IFaceRecognitionService, FaceRecognitionForwarder>();
        ((FaceRecognitionForwarder)(object)proxy)._target = target;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var parameterTypes = targetMethod.GetParameters().Select(p => p.ParameterType).ToArray();
        var method = _target.GetType().GetMethod(targetMethod.Name, BindingFlags.Public | BindingFlags.Instance, parameterTypes);
        if (method is not null)
        {
            try
            {
                return method.Invoke(_target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        if (targetMethod.ReturnType == typeof(void))
        {
            return null;
        }

        if (targetMethod.ReturnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        throw new NotSupportedException($"{_target.GetType().Name} bietet {targetMethod.Name} nicht an (Übergang bis zum Merge mit P1).");
    }
}
