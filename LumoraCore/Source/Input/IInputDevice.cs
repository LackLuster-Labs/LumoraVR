using System.Collections.Generic;

namespace Lumora.Core.Input;

/// <summary>
/// Interface for all input devices (mouse, keyboard, controllers, etc.)
/// Standard input device interface
/// </summary>
public interface IInputDevice
{
    bool IsDeviceActive { get; set; }
    int DeviceIndex { get; }
    string Name { get; }
    int PropertyCount { get; }

    T GetProperty<T>(int index) where T : ControllerProperty;
    T GetProperty<T>(string name) where T : ControllerProperty;

    void Initialize(InputInterface input, int deviceIndex, string name);
    void RegisterProperty(ControllerProperty property);
}

/// <summary>
/// Base implementation of IInputDevice
/// Standard input device base class
/// </summary>
public class InputDevice : IInputDevice
{
    private List<ControllerProperty> _properties = new List<ControllerProperty>();

    public InputInterface Input { get; private set; }
    public bool IsDeviceActive { get; set; } = true;
    public int DeviceIndex { get; private set; }
    public string Name { get; private set; }
    public int PropertyCount => _properties.Count;

    public virtual void Initialize(InputInterface input, int deviceIndex, string name)
    {
        Input = input;
        DeviceIndex = deviceIndex;
        Name = name;
    }

    public void RegisterProperty(ControllerProperty property)
    {
        _properties.Add(property);
        property.Initialize(this, _properties.Count - 1, property.GetType().Name);
    }

    public T GetProperty<T>(int index) where T : ControllerProperty
    {
        if (index < 0 || index >= _properties.Count)
            return null;
        return _properties[index] as T;
    }

    public T GetProperty<T>(string name) where T : ControllerProperty
    {
        return _properties.Find(p => p.Name == name) as T;
    }
}
