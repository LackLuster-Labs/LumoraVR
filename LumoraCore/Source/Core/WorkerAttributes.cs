using System;

namespace Lumora.Core;

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class NameOverrideAttribute : Attribute
{
    public string Name { get; }

    public NameOverrideAttribute(string name)
    {
        Name = name;
    }
}

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class NonPersistentAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class NonDrivableAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class DontCopyAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class DefaultValueAttribute : Attribute
{
    public object Default { get; }

    public DefaultValueAttribute(object value)
    {
        Default = value;
    }
}

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class OldNameAttribute : Attribute
{
    public string[] OldNames { get; }

    public OldNameAttribute(params string[] oldNames)
    {
        OldNames = oldNames ?? Array.Empty<string>();
    }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class SingleInstancePerSlotAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class PreserveWithAssetsAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class DontDuplicateAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class GloballyRegisteredAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class HideInInspectorAttribute : Attribute
{
}
