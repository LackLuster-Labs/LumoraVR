namespace Lumora.Core.Components;

/// <summary>
/// Null locomotion module (no movement) used when locomotion is suppressed or unavailable.
/// </summary>
public class NullLocomotionModule : ILocomotionModule
{
    public void Activate(LocomotionController owner) { }
    public void Deactivate() { }
    public void Update(float delta) { }
    public void Dispose() { }
}
