using Lumora.Core.Math;

namespace Lumora.Core.Input;

/// <summary>
/// Mouse input device with buttons, position, and scroll wheel
/// Standard mouse input interface
/// </summary>
public class Mouse : InputDevice
{
    public readonly Digital LeftButton = new Digital();
    public readonly Digital RightButton = new Digital();
    public readonly Digital MiddleButton = new Digital();
    public readonly Digital MouseButton4 = new Digital();
    public readonly Digital MouseButton5 = new Digital();

    public readonly Analog2D DesktopPosition = new Analog2D();      // Screen coordinates
    public readonly Analog2D Position = new Analog2D();              // UI/logical coordinates
    public readonly Analog2D DirectDelta = new Analog2D();           // Delta in pixels
    public readonly Analog ScrollWheelDelta = new Analog();          // Scroll wheel

    public override void Initialize(InputInterface input, int deviceIndex, string name)
    {
        base.Initialize(input, deviceIndex, name);

        // Register all properties
        RegisterProperty(LeftButton);
        RegisterProperty(RightButton);
        RegisterProperty(MiddleButton);
        RegisterProperty(MouseButton4);
        RegisterProperty(MouseButton5);
        RegisterProperty(DesktopPosition);
        RegisterProperty(Position);
        RegisterProperty(DirectDelta);
        RegisterProperty(ScrollWheelDelta);
    }
}
