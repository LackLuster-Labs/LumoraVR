using Godot;
using Lumora.Core.Logging;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Source.Input;

/// <summary>
/// Manages laser pointer interaction for both hands.
/// Attach to VR input root to enable laser UI interaction.
/// </summary>
public partial class LaserInteractionManager : Node3D
{
    private LaserPointer _leftLaser;
    private LaserPointer _rightLaser;

    /// <summary>
    /// Left hand laser pointer.
    /// </summary>
    public LaserPointer LeftLaser => _leftLaser;

    /// <summary>
    /// Right hand laser pointer.
    /// </summary>
    public LaserPointer RightLaser => _rightLaser;

    /// <summary>
    /// Whether laser interaction is enabled.
    /// </summary>
    public bool Enabled
    {
        get => _leftLaser?.Visible ?? false;
        set
        {
            if (_leftLaser != null) _leftLaser.Visible = value;
            if (_rightLaser != null) _rightLaser.Visible = value;
        }
    }

    public override void _Ready()
    {
        CreateLasers();
        AquaLogger.Log("LaserInteractionManager: Initialized");
    }

    private void CreateLasers()
    {
        // Create left hand laser
        _leftLaser = new LaserPointer();
        _leftLaser.Name = "LeftLaser";
        _leftLaser.Side = LaserPointer.Hand.Left;
        AddChild(_leftLaser);

        // Create right hand laser
        _rightLaser = new LaserPointer();
        _rightLaser.Name = "RightLaser";
        _rightLaser.Side = LaserPointer.Hand.Right;
        AddChild(_rightLaser);
    }

    /// <summary>
    /// Get the laser that is currently hovering over a panel, or null.
    /// </summary>
    public LaserPointer GetHoveringLaser()
    {
        if (_rightLaser?.IsHovering == true)
            return _rightLaser;
        if (_leftLaser?.IsHovering == true)
            return _leftLaser;
        return null;
    }

    /// <summary>
    /// Get the current hit point from either laser, prioritizing right hand.
    /// </summary>
    public Vector3? GetHitPoint()
    {
        if (_rightLaser?.IsHovering == true)
            return _rightLaser.HitPoint;
        if (_leftLaser?.IsHovering == true)
            return _leftLaser.HitPoint;
        return null;
    }
}
