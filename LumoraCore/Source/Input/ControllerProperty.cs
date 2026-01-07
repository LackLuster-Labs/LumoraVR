using Lumora.Core.Math;

namespace Lumora.Core.Input;

/// <summary>
/// Base class for all input properties (buttons, axes, etc.)
/// Standard controller property interface
/// </summary>
public abstract class ControllerProperty
{
    public IInputDevice Device { get; private set; }
    public int Index { get; private set; } = -1;
    public string Name { get; private set; }

    internal void Initialize(IInputDevice device, int index, string name)
    {
        Device = device;
        Index = index;
        Name = name;
    }
}

/// <summary>
/// Digital input (button/key state with press/release detection)
/// Standard digital button property
/// </summary>
public class Digital : ControllerProperty
{
    public bool Held { get; private set; }
    public bool Pressed { get; private set; }    // Just pressed this frame
    public bool Released { get; private set; }   // Just released this frame

    public void UpdateState(bool held)
    {
        Pressed = false;
        Released = false;

        if (!Held && held)
            Pressed = true;
        if (Held && !held)
            Released = true;

        Held = held;
    }
}

/// <summary>
/// Analog input (single-axis value with delta and velocity)
/// Standard analog axis property
/// </summary>
public class Analog : ControllerProperty
{
    public float Value { get; private set; }
    public float Delta { get; private set; }        // Change this frame
    public float Velocity { get; private set; }     // Delta / deltaTime

    public void UpdateValue(float newValue, float deltaTime)
    {
        Delta = newValue - Value;
        Value = newValue;
        Velocity = deltaTime > 0 ? Delta / deltaTime : 0;
    }
}

/// <summary>
/// 2D analog input (thumbstick, touchpad, mouse delta)
/// Standard 2D analog property
/// </summary>
public class Analog2D : ControllerProperty
{
    public float2 Value { get; private set; }
    public float2 Delta { get; private set; }
    public float2 Velocity { get; private set; }

    public void UpdateValue(float2 newValue, float deltaTime)
    {
        Delta = newValue - Value;
        Value = newValue;
        Velocity = deltaTime > 0 ? Delta / deltaTime : float2.Zero;
    }
}
