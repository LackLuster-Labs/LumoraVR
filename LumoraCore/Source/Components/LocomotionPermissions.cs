namespace Lumora.Core.Components;

/// <summary>
/// Placeholder permissions class for locomotion (stubbed to allow all).
/// </summary>
public class LocomotionPermissions
{
    public bool CanUseLocomotion(ILocomotionModule module) => true;
    public bool CanUseAnyLocomotion() => true;
}
