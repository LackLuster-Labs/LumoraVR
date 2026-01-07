using System;

namespace Lumora.Core;

/// <summary>
/// Identifies a delegate by target RefID, method name, and optional static type.
/// </summary>
public readonly struct WorldDelegate
{
    public readonly RefID Target;
    public readonly string Method;
    public readonly Type Type;

    public WorldDelegate(RefID target, string method, Type type)
    {
        Target = target;
        Method = method;
        Type = type;
    }
}
