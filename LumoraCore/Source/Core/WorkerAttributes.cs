// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

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

// On a field: no editor row for that member. On a class: the component renders no section at all,
// for render plumbing that is pure noise in the inspector. - xlinka
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class HideInInspectorAttribute : Attribute
{
}

// Opens a method up to be bound by a SyncDelegate that resolves from saved or replicated data.
// A delegate target loaded from a world file or received from a peer is refused unless its method
// carries this - it is the boundary that stops crafted data from binding an action to an arbitrary
// same-named method. Binding a method group through SetAction at runtime is unaffected (that path
// hands the delegate over directly and never goes through name resolution).
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class SyncMethodAttribute : Attribute
{
}

// Opens a public void method that takes nothing to Loom's Call Method. Only for a method that changes its own
// component's synced state and nothing else: the script is judged by the gate as able to change the component
// before the call, and every write inside it is its author's. Nothing that makes or removes things, touches
// someone's body, another component, a file or this machine's own settings. -xlinka
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class ScriptMethodAttribute : Attribute
{
}
