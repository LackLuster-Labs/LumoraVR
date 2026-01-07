using System;

namespace Lumora.Core;

/// <summary>
/// Marks component category for editor organization.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class ComponentCategoryAttribute : Attribute
{
    public string Category { get; }

    public ComponentCategoryAttribute(string category)
    {
        Category = category;
    }
}
