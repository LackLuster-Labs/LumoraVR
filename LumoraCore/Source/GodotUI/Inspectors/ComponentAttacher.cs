using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.GodotUI.Inspectors;

/// <summary>
/// Component attacher wizard for adding components to slots.
/// Displays a searchable list of available component types.
/// </summary>
[ComponentCategory("GodotUI/Inspectors")]
public class ComponentAttacher : GodotUIPanel
{
    protected override string DefaultScenePath => LumAssets.UI.ComponentAttacher;
    protected override float2 DefaultSize => new(350, 500);

    /// <summary>
    /// The slot to attach components to.
    /// </summary>
    public SyncRef<Slot> TargetSlot { get; private set; } = null!;

    /// <summary>
    /// Current search filter text.
    /// </summary>
    public Sync<string> SearchFilter { get; private set; } = null!;

    /// <summary>
    /// Currently selected category filter.
    /// </summary>
    public Sync<string> CategoryFilter { get; private set; } = null!;

    /// <summary>
    /// Event fired when a component is attached.
    /// </summary>
    public event Action<Slot, Component>? OnComponentAttached;

    /// <summary>
    /// Event fired when search filter changes.
    /// </summary>
    public event Action<string>? OnSearchChanged;

    /// <summary>
    /// Event fired when category filter changes.
    /// </summary>
    public event Action<string>? OnCategoryChanged;

    private static List<ComponentTypeInfo>? _cachedComponentTypes;
    private static readonly object _cacheLock = new();

    public override void OnAwake()
    {
        base.OnAwake();
        InitializeAttacherSyncMembers();
    }

    private void InitializeAttacherSyncMembers()
    {
        TargetSlot = new SyncRef<Slot>(this);
        SearchFilter = new Sync<string>(this, "");
        CategoryFilter = new Sync<string>(this, "");

        SearchFilter.OnChanged += _ =>
        {
            OnSearchChanged?.Invoke(SearchFilter.Value ?? "");
            NotifyChanged();
        };

        CategoryFilter.OnChanged += _ =>
        {
            OnCategoryChanged?.Invoke(CategoryFilter.Value ?? "");
            NotifyChanged();
        };
    }

    /// <summary>
    /// Get all available component types.
    /// </summary>
    public IReadOnlyList<ComponentTypeInfo> GetAvailableComponents()
    {
        lock (_cacheLock)
        {
            if (_cachedComponentTypes == null)
            {
                _cachedComponentTypes = DiscoverComponentTypes();
            }
            return _cachedComponentTypes;
        }
    }

    /// <summary>
    /// Get filtered component types based on search and category.
    /// </summary>
    public IEnumerable<ComponentTypeInfo> GetFilteredComponents()
    {
        var components = GetAvailableComponents();
        var search = SearchFilter.Value?.ToLowerInvariant() ?? "";
        var category = CategoryFilter.Value ?? "";

        foreach (var comp in components)
        {
            // Filter by category
            if (!string.IsNullOrEmpty(category) && comp.Category != category)
                continue;

            // Filter by search
            if (!string.IsNullOrEmpty(search))
            {
                if (!comp.Name.ToLowerInvariant().Contains(search) &&
                    !comp.FullName.ToLowerInvariant().Contains(search) &&
                    !comp.Category.ToLowerInvariant().Contains(search))
                    continue;
            }

            yield return comp;
        }
    }

    /// <summary>
    /// Get all available categories.
    /// </summary>
    public IEnumerable<string> GetCategories()
    {
        return GetAvailableComponents()
            .Select(c => c.Category)
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct()
            .OrderBy(c => c);
    }

    /// <summary>
    /// Attach a component type to the target slot.
    /// </summary>
    public Component? AttachComponent(Type componentType)
    {
        if (TargetSlot.Target == null) return null;
        if (!typeof(Component).IsAssignableFrom(componentType)) return null;

        var component = TargetSlot.Target.AttachComponent(componentType);
        if (component != null)
        {
            OnComponentAttached?.Invoke(TargetSlot.Target, component);
        }

        return component;
    }

    /// <summary>
    /// Attach a component by type name.
    /// </summary>
    public Component? AttachComponent(string typeName)
    {
        var typeInfo = GetAvailableComponents().FirstOrDefault(c => c.FullName == typeName || c.Name == typeName);
        if (typeInfo == null) return null;

        return AttachComponent(typeInfo.Type);
    }

    /// <summary>
    /// Setup with a target slot.
    /// </summary>
    public void Setup(Slot target)
    {
        TargetSlot.Target = target;
    }

    public override void HandleButtonPress(string buttonPath)
    {
        if (buttonPath.StartsWith("Component_"))
        {
            var typeName = buttonPath.Substring("Component_".Length);
            AttachComponent(typeName);
            Close();
            return;
        }

        if (buttonPath.StartsWith("Category_"))
        {
            var category = buttonPath.Substring("Category_".Length);
            CategoryFilter.Value = category == "All" ? "" : category;
            return;
        }

        if (buttonPath.EndsWith("ClearSearchButton"))
        {
            SearchFilter.Value = "";
            return;
        }

        base.HandleButtonPress(buttonPath);
    }

    private static List<ComponentTypeInfo> DiscoverComponentTypes()
    {
        var result = new List<ComponentTypeInfo>();
        var componentType = typeof(Component);

        // Get all assemblies
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();

        foreach (var assembly in assemblies)
        {
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    // Skip abstract types and non-component types
                    if (type.IsAbstract || !componentType.IsAssignableFrom(type))
                        continue;

                    // Skip internal/system types
                    if (type.Namespace == null || type.Namespace.StartsWith("System"))
                        continue;

                    // Get category from attribute
                    var categoryAttr = type.GetCustomAttribute<ComponentCategoryAttribute>();
                    var category = categoryAttr?.Category ?? "Uncategorized";

                    result.Add(new ComponentTypeInfo
                    {
                        Type = type,
                        Name = type.Name,
                        FullName = type.FullName ?? type.Name,
                        Category = category,
                        Namespace = type.Namespace ?? ""
                    });
                }
            }
            catch
            {
                // Ignore assemblies that can't be reflected
            }
        }

        return result.OrderBy(c => c.Category).ThenBy(c => c.Name).ToList();
    }

    /// <summary>
    /// Clear the component type cache (call after loading new assemblies).
    /// </summary>
    public static void ClearCache()
    {
        lock (_cacheLock)
        {
            _cachedComponentTypes = null;
        }
    }
}

/// <summary>
/// Information about an attachable component type.
/// </summary>
public class ComponentTypeInfo
{
    public Type Type { get; set; } = null!;
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Category { get; set; } = "";
    public string Namespace { get; set; } = "";
}
