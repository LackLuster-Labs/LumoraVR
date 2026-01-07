using System;

namespace Lumora.Core.Components;

/// <summary>
/// Minimal locomotion module interface for desktop/VR locomotion delegation.
/// Implementations handle movement/jump logic and drive CharacterController.
/// </summary>
public interface ILocomotionModule : IDisposable
{
    /// <summary>
    /// Called when this module becomes active.
    /// </summary>
    void Activate(LocomotionController owner);

    /// <summary>
    /// Called when this module is deactivated.
    /// </summary>
    void Deactivate();

    /// <summary>
    /// Per-frame update.
    /// </summary>
    void Update(float delta);
}
