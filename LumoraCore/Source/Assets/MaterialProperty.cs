using System;

namespace Lumora.Core.Assets;

/// <summary>
/// Represents a shader property that can be set on materials.
/// Caches shader property IDs for efficient material updates.
/// </summary>
public class MaterialProperty
{
    private readonly string _name;
    private int _propertyID;
    private bool _initialized;

    /// <summary>
    /// The shader property name (e.g., "_MainTex", "_Color").
    /// </summary>
    public string Name => _name;

    /// <summary>
    /// The cached property ID (platform-specific shader property identifier).
    /// </summary>
    public int PropertyID => _propertyID;

    /// <summary>
    /// Check if this property has been initialized with a property ID.
    /// </summary>
    public bool Initialized => _initialized;

    public MaterialProperty(string name)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _initialized = false;
    }

    /// <summary>
    /// Initialize the property with a shader property ID.
    /// </summary>
    public void Initialize(int propertyID)
    {
        _propertyID = propertyID;
        _initialized = true;
    }

    /// <summary>
    /// Implicit conversion to int (returns PropertyID).
    /// </summary>
    public static implicit operator int(MaterialProperty prop) => prop._propertyID;

    public override string ToString()
    {
        return $"MaterialProperty(Name: {_name}, ID: {_propertyID}, Initialized: {_initialized})";
    }
}
